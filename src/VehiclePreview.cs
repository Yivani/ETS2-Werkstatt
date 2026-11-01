using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using TruckLib.HashFs;
using TruckLib.Models;
using Num=System.Numerics;

namespace Werkstatt;

/// <summary>The game's archives, opened read-only. DLC archives are asked first, as the game loads them after the base archives.</summary>
public sealed class GameFiles : IDisposable
{
    readonly List<IHashFsReader> readers=[];
    public List<string> Warnings {get;}=[];
    public static GameFiles Open(Installation install)
    {
        string root=install.GamePath??throw new InvalidOperationException(L.T("core.pickfolder"));
        var files=new GameFiles();
        var paths=install.Dlcs.Select(d=>d.File).Concat(new[]{"base_vehicle.scs","base_share.scs","base.scs","def.scs","core.scs"}.Select(f=>Path.Combine(root,f)).Where(File.Exists));
        foreach(var path in paths)
        {
            try{files.readers.Add(HashFsReader.Open(path));}
            catch(Exception e) when(e is not OutOfMemoryException){files.Warnings.Add($"{Path.GetFileName(path)}: {e.Message}");}
        }
        return files;
    }
    IHashFsReader? Find(string path)=>readers.FirstOrDefault(r=>r.FileExists(path));
    public byte[]? Read(string path)=>Find(path)?.Extract(path)[0];
    public string? Text(string path)=>Read(path) is {} bytes?Encoding.UTF8.GetString(bytes):null;
    /// <summary>The DDS image of a .tobj: stored next to it in HashFS v2, referenced by path in older archives.</summary>
    public byte[]? Dds(string tobj)
    {
        if(Find(tobj) is not {} reader)return null;
        var parts=reader.Extract(tobj);
        if(parts.Length>1)return parts[1];
        var path=Regex.Match(Encoding.ASCII.GetString(parts[0]),@"/[\w/.\-]+\.dds");
        return path.Success?Read(path.Value):null;
    }
    public void Dispose(){foreach(var reader in readers)reader.Dispose();readers.Clear();}
}

/// <summary>Decodes BC1, BC2, BC3 and 32-bit DDS textures. Other formats (BC7, normal maps) return null and the part keeps its plain colour.</summary>
public static class DdsImage
{
    public static BitmapSource? Decode(byte[] dds,int maxSize,bool alpha)
    {
        if(dds.Length<128||BitConverter.ToUInt32(dds,0)!=0x20534444)return null;
        int height=BitConverter.ToInt32(dds,12),width=BitConverter.ToInt32(dds,16),mips=Math.Max(1,BitConverter.ToInt32(dds,28)),offset=128,format;
        string fourCc=Encoding.ASCII.GetString(dds,84,4);
        if(fourCc=="DX10")
        {
            offset=148;
            format=BitConverter.ToInt32(dds,128) switch{70 or 71 or 72=>1,73 or 74 or 75=>2,76 or 77 or 78=>3,27 or 28 or 29=>4,87 or 88 or 90 or 91=>5,_=>0};
        }
        else format=fourCc switch{"DXT1"=>1,"DXT2" or "DXT3"=>2,"DXT4" or "DXT5"=>3,_=>(BitConverter.ToUInt32(dds,80)&0x40)!=0&&BitConverter.ToInt32(dds,88)==32?(BitConverter.ToUInt32(dds,92)==0xFF?4:5):0};
        if(format==0||width<=0||height<=0)return null;
        int Size(int w,int h)=>format>=4?w*h*4:Math.Max(1,(w+3)/4)*Math.Max(1,(h+3)/4)*(format==1?8:16);
        for(int level=0;level<mips-1&&width>maxSize;level++){offset+=Size(width,height);width=Math.Max(1,width/2);height=Math.Max(1,height/2);}
        if(offset+Size(width,height)>dds.Length)return null;
        var pixels=new byte[width*height*4];
        if(format>=4)
        {
            for(int i=0;i<width*height;i++)
            {
                int s=offset+i*4,d=i*4;
                if(format==4){pixels[d]=dds[s+2];pixels[d+1]=dds[s+1];pixels[d+2]=dds[s];}else{pixels[d]=dds[s];pixels[d+1]=dds[s+1];pixels[d+2]=dds[s+2];}
                pixels[d+3]=dds[s+3];
            }
        }
        else
        {
            int blockSize=format==1?8:16,at=offset;
            for(int by=0;by<height;by+=4)for(int bx=0;bx<width;bx+=4){Block(dds,at,pixels,bx,by,width,height,format);at+=blockSize;}
        }
        var bitmap=BitmapSource.Create(width,height,96,96,alpha?PixelFormats.Bgra32:PixelFormats.Bgr32,null,pixels,width*4);bitmap.Freeze();
        return bitmap;
    }
    static void Block(byte[] src,int at,byte[] dst,int bx,int by,int width,int height,int format)
    {
        int colorAt=format==1?at:at+8;
        ushort c0=BitConverter.ToUInt16(src,colorAt),c1=BitConverter.ToUInt16(src,colorAt+2);uint bits=BitConverter.ToUInt32(src,colorAt+4);
        Span<int> r=stackalloc int[4],g=stackalloc int[4],b=stackalloc int[4],a=stackalloc int[4];
        (r[0],g[0],b[0])=Rgb565(c0);(r[1],g[1],b[1])=Rgb565(c1);a[0]=a[1]=a[2]=a[3]=255;
        if(c0>c1||format!=1)
        {
            r[2]=(2*r[0]+r[1])/3;g[2]=(2*g[0]+g[1])/3;b[2]=(2*b[0]+b[1])/3;
            r[3]=(r[0]+2*r[1])/3;g[3]=(g[0]+2*g[1])/3;b[3]=(b[0]+2*b[1])/3;
        }
        else{r[2]=(r[0]+r[1])/2;g[2]=(g[0]+g[1])/2;b[2]=(b[0]+b[1])/2;r[3]=g[3]=b[3]=a[3]=0;}
        Span<byte> alpha=stackalloc byte[16];
        if(format==2){ulong v=BitConverter.ToUInt64(src,at);for(int i=0;i<16;i++)alpha[i]=(byte)(((v>>(4*i))&15)*17);}
        else if(format==3)
        {
            int a0=src[at],a1=src[at+1];ulong v=0;for(int i=0;i<6;i++)v|=(ulong)src[at+2+i]<<(8*i);
            Span<int> table=stackalloc int[8];table[0]=a0;table[1]=a1;
            if(a0>a1)for(int i=1;i<7;i++)table[i+1]=((7-i)*a0+i*a1)/7;
            else{for(int i=1;i<5;i++)table[i+1]=((5-i)*a0+i*a1)/5;table[6]=0;table[7]=255;}
            for(int i=0;i<16;i++)alpha[i]=(byte)table[(int)((v>>(3*i))&7)];
        }
        for(int i=0;i<16;i++)
        {
            int x=bx+i%4,y=by+i/4;if(x>=width||y>=height)continue;
            int index=(int)((bits>>(2*i))&3),o=(y*width+x)*4;
            dst[o]=(byte)b[index];dst[o+1]=(byte)g[index];dst[o+2]=(byte)r[index];dst[o+3]=format==1?(byte)a[index]:alpha[i];
        }
    }
    static (int,int,int) Rgb565(ushort c)=>((c>>11&31)*255/31,(c>>5&63)*255/63,(c&31)*255/31);
}

/// <summary>The parts of one truck or trailer as the save lists them: definition paths and the paint colours set in the game.</summary>
public sealed record VehicleSpec(string Id,bool Truck,List<(string Path,Color? Paint)> Parts,Color Body)
{
    public string Key=>Id+"|"+Body+"|"+string.Join("|",Parts.Select(p=>p.Path));
}
public sealed record PreviewModel(Model3DGroup Model,Rect3D Bounds,List<string> Missing);

public static class VehiclePreview
{
    static readonly Color DefaultBody=Color.FromRgb(0xC8,0xC8,0xC4);
    /// <summary>Reads the parts on the UI thread, so the model can be built in the background from plain values.</summary>
    public static VehicleSpec Describe(SiiDocument doc,SiiUnit vehicle)
    {
        var parts=new List<(string,Color?)>();Color body=DefaultBody;
        foreach(var field in vehicle.Fields.Where(f=>f.Key.StartsWith("accessories[",StringComparison.Ordinal)))
        {
            if(doc.Resolve(vehicle,field.Key) is not {} accessory||accessory.Get("data_path") is not {} raw)continue;
            string path=raw.Trim('"');if(path.Length==0)continue;
            if(accessory.Type=="vehicle_paint_job_accessory"){if(LinearColor(accessory.Get("base_color")) is {} paint)body=paint;continue;}
            parts.Add((path,accessory.Type=="vehicle_addon_accessory"?null:LinearColor(accessory.Get("paint_color"))));
        }
        return new(vehicle.Id,vehicle.Type=="vehicle",parts,body);
    }
    /// <summary>Converts a save colour "(r, g, b)" from linear light to sRGB.</summary>
    static Color? LinearColor(string? value)
    {
        if(value==null)return null;
        var channels=value.Trim('(',')').Split(',').Select(s=>SaveSession.FloatValue(s.Trim())).ToList();
        if(channels.Count!=3||channels.Any(c=>c==null))return null;
        byte Channel(float linear){double c=Math.Clamp(linear,0,1);return (byte)Math.Round(255*(c<=0.0031308?12.92*c:1.055*Math.Pow(c,1/2.4)-0.055));}
        return Color.FromRgb(Channel(channels[0]!.Value),Channel(channels[1]!.Value),Channel(channels[2]!.Value));
    }

    sealed record Definition(string Kind,string Folder,string? Model,string? Variant,string? Look,string? ExtModel,string? ExtLook);
    sealed record Placed(Model Model,HashSet<int> Visible,Matrix3D Transform,string Look);
    sealed record MaterialInfo(string Effect,Color Diffuse,string? Texture);

    /// <summary>Builds and freezes the model. Parts the game files don't contain (mods) are listed in <see cref="PreviewModel.Missing"/>.</summary>
    public static PreviewModel Build(GameFiles files,VehicleSpec spec)
    {
        var builder=new Builder(files);var missing=new List<string>();
        var definitions=new List<(Definition Def,Color? Paint)>();
        foreach(var (path,paint) in spec.Parts)
        {
            if(files.Text(path) is not {} text){missing.Add(path);continue;}
            var def=Parse(path,text);if(def!=null)definitions.Add((def,paint));
        }
        var frames=new List<Placed>();
        Placed? Place(Definition? def,Matrix3D transform,string? look=null)
        {
            if(def?.Model==null)return null;
            var model=builder.Model(def.Model);if(model==null){missing.Add(def.Model);return null;}
            var placed=new Placed(model,Visible(model,def.Variant),transform,def.Look??look??"default");frames.Add(placed);return placed;
        }
        var chassis=Place(definitions.FirstOrDefault(d=>d.Def.Kind=="chassis").Def,Matrix3D.Identity);
        var cabin=Place(definitions.FirstOrDefault(d=>d.Def.Kind=="cabin").Def,Matrix3D.Identity);
        string look=cabin?.Look??chassis?.Look??"default";
        if(definitions.FirstOrDefault(d=>d.Def.Kind=="body").Def is {} body)Place(body,Locate(frames,"body") is [var spot,..]?spot:Matrix3D.Identity);
        if(definitions.FirstOrDefault(d=>d.Def.Kind=="interior").Def is {ExtModel:{}} interior)
            Place(interior with{Model=interior.ExtModel,Variant=null,Look=interior.ExtLook},Locate(frames,"ext_interior") is [var inside,..]?inside:Matrix3D.Identity);
        foreach(var (def,_) in definitions.Where(d=>d.Def.Kind=="addon"))
        {
            var spots=Locate(frames,def.Folder);
            if(spots.Count==0&&Locate(frames,def.Folder,true) is [var near,..])spots=[near];
            foreach(var spot in spots)Place(def,spot,look);
        }
        var wheels=definitions.Where(d=>d.Def.Kind=="wheel").ToList();
        foreach(var frame in frames.ToList())
            foreach(var locator in Locators(frame).Where(l=>l.Name.String.StartsWith("wheel",StringComparison.Ordinal)))
            {
                char axle=locator.Name.String.StartsWith("wheel_r",StringComparison.Ordinal)?'r':'f';
                var set=wheels.Where(w=>w.Def.Folder[0]==axle).ToList();if(set.Count==0)set=wheels.Where(w=>w.Def.Folder[0]=='t').ToList();
                var transform=Matrix(locator,frame.Transform);bool left=locator.Position.X<0;
                foreach(var (def,paint) in set.GroupBy(w=>w.Def.Folder).Select(g=>g.First()))
                {
                    if(def.Model==null||builder.Model(def.Model) is not {} model)continue;
                    string side=left?"left":"right";
                    var visible=model.Variants.Any(v=>v.Name.String==side)?Visible(model,side):model.Parts.Count>1?model.Parts.Select((p,i)=>(p,i)).Where(x=>x.p.Name.String==(left?"l":"r")).Select(x=>x.i).ToHashSet():Visible(model,null);
                    builder.Add(new Placed(model,visible,transform,def.Look??"default"),spec.Body,paint);
                }
            }
        foreach(var frame in frames)builder.Add(frame,spec.Body,null);
        var (group,bounds)=builder.Finish();
        return new(group,bounds,missing);
    }

    static Definition? Parse(string path,string text)
    {
        var header=Regex.Match(text,@"^\s*(accessory_\w+)\s*:",RegexOptions.Multiline);if(!header.Success)return null;
        string Value(string key)=>Regex.Match(text,@"^\s*"+key+@"\s*:\s*""?([^""\s#]+)",RegexOptions.Multiline) is {Success:true} m?m.Groups[1].Value:"";
        string? Optional(string key)=>Value(key) is {Length:>0} v?v:null;
        var segments=path.Split('/',StringSplitOptions.RemoveEmptyEntries);string folder=segments.Length>1?segments[^2]:"";
        string kind=header.Groups[1].Value switch
        {
            "accessory_cabin_data"=>"cabin",
            "accessory_chassis_data"=>"chassis",
            "accessory_trailer_body_data"=>"body",
            "accessory_interior_data"=>"interior",
            _ when Regex.IsMatch(folder,"^[frt]_(tire|disc|hub|nuts|cover|wheel)$")=>"wheel",
            var c when c.StartsWith("accessory_addon",StringComparison.Ordinal)=>"addon",
            _=>"other"
        };
        if(kind=="other")return null;
        return new(kind,folder,Optional("model")??Optional("detail_model")??Optional("exterior_model"),Optional("variant"),Optional("look"),Optional("ext_model"),Optional("ext_look"));
    }
    /// <summary>Parts shown in a variant. Parts off in every variant but named *_off (empty deck, stowed cables) are kept, as is a parked trailer's landing gear.</summary>
    static HashSet<int> Visible(Model model,string? variant)
    {
        var chosen=model.Variants.FirstOrDefault(v=>v.Name.String==variant)??model.Variants.FirstOrDefault();
        var visible=new HashSet<int>();
        for(int i=0;i<model.Parts.Count;i++)
        {
            string name=model.Parts[i].Name.String;
            bool on=chosen==null||chosen.Attributes.Count!=model.Parts.Count||chosen.Attributes[i].Value!=0;
            if(on||name.EndsWith("_off",StringComparison.Ordinal)||name=="brace_on")visible.Add(i);
        }
        return visible;
    }
    static IEnumerable<Locator> Locators(Placed frame)=>frame.Visible.SelectMany(i=>frame.Model.Parts[i].Locators);
    static List<Matrix3D> Locate(List<Placed> frames,string name,bool loose=false)=>frames.SelectMany(f=>Locators(f)
        .Where(l=>loose?l.Name.String.StartsWith(name+"_",StringComparison.Ordinal)||l.Name.String==name+"s":l.Name.String==name)
        .Select(l=>Matrix(l,f.Transform))).ToList();
    static Matrix3D Matrix(Locator locator,Matrix3D parent)
    {
        var m=new Matrix3D();var q=locator.Rotation;
        if(q.LengthSquared()>0)m.Rotate(new System.Windows.Media.Media3D.Quaternion(q.X,q.Y,q.Z,q.W));
        m.Translate(new Vector3D(locator.Position.X,locator.Position.Y,locator.Position.Z));
        return m*parent;
    }

    /// <summary>Turns placed models into frozen WPF geometry: one mesh per piece, opaque pieces first, glass and decals last.</summary>
    sealed class Builder(GameFiles files)
    {
        readonly Dictionary<string,Model?> models=[];
        readonly Dictionary<string,MaterialInfo?> materials=[];
        readonly Dictionary<string,BitmapSource?> textures=[];
        readonly List<GeometryModel3D> opaque=[],transparent=[];
        public Model? Model(string path)
        {
            if(models.TryGetValue(path,out var cached))return cached;
            Model? model=null;
            try{if(files.Read(path) is {} pmd&&files.Read(Path.ChangeExtension(path,".pmg")) is {} pmg)model=TruckLib.Models.Model.Load(pmd,pmg);}
            catch(Exception e) when(e is not OutOfMemoryException){}
            return models[path]=model;
        }
        MaterialInfo? Material(string path)
        {
            if(materials.TryGetValue(path,out var cached))return cached;
            MaterialInfo? info=null;
            if(files.Text(path) is {} text)
            {
                string effect=Regex.Match(text,@"(?:effect|material)\s*:\s*""([^""]+)""").Groups[1].Value;
                var d=Regex.Match(text,@"diffuse\s*:\s*\{\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)");
                Color diffuse=d.Success?Color.FromScRgb(1,F(d.Groups[1].Value),F(d.Groups[2].Value),F(d.Groups[3].Value)):Colors.White;
                var texture=Regex.Match(text,@"""texture_base""\s*\{[^}]*?source\s*:\s*""([^""]+)""");
                if(!texture.Success)texture=Regex.Match(text,@"texture\s*:\s*""([^""]+\.tobj)""");
                string? source=texture.Success?texture.Groups[1].Value:null;
                if(source!=null&&!source.StartsWith('/'))source=Path.GetDirectoryName(path)!.Replace('\\','/')+"/"+source;
                info=new(effect,diffuse,source);
            }
            return materials[path]=info;
        }
        static float F(string s)=>float.Parse(s,CultureInfo.InvariantCulture);
        /// <summary>The texture at 256 px at most, multiplied by <paramref name="paint"/>. Rims and hubs paint only where the alpha mask says so.</summary>
        BitmapSource? Texture(string path,bool alpha,Color? paint=null,bool masked=false)
        {
            string key=path+(alpha?"#a":"")+paint+(masked?"#m":"");
            if(textures.TryGetValue(key,out var cached))return cached;
            BitmapSource? bitmap=null;
            try
            {
                if(paint is {} c&&Texture(path,true) is {} source)
                {
                    int w=source.PixelWidth,h=source.PixelHeight;var pixels=new byte[w*h*4];source.CopyPixels(pixels,w*4,0);
                    for(int i=0;i<pixels.Length;i+=4)
                    {
                        double mask=masked?pixels[i+3]/255.0:1;
                        pixels[i]=(byte)(pixels[i]*(1-mask+mask*c.B/255.0));pixels[i+1]=(byte)(pixels[i+1]*(1-mask+mask*c.G/255.0));pixels[i+2]=(byte)(pixels[i+2]*(1-mask+mask*c.R/255.0));pixels[i+3]=255;
                    }
                    bitmap=BitmapSource.Create(w,h,96,96,PixelFormats.Bgr32,null,pixels,w*4);bitmap.Freeze();
                }
                else if(paint==null&&files.Dds(path) is {} dds)bitmap=DdsImage.Decode(dds,256,alpha);
            }
            catch(Exception e) when(e is not OutOfMemoryException){}
            return textures[key]=bitmap;
        }
        public void Add(Placed placed,Color body,Color? paint)
        {
            var look=placed.Model.Looks.FirstOrDefault(l=>l.Name.String==placed.Look)??placed.Model.Looks.FirstOrDefault();
            if(look==null)return;
            var transform=new MatrixTransform3D(placed.Transform);transform.Freeze();
            foreach(int index in placed.Visible)
                foreach(var piece in placed.Model.Parts[index].Pieces)
                {
                    if(piece.Vertices.Count==0||piece.Triangles.Count==0||piece.Material>=look.Materials.Count)continue;
                    var info=Material(look.Materials[(int)piece.Material]);if(info==null)continue;
                    var material=Surface(info,body,paint,out bool see);if(material==null)continue;
                    var geometry=new GeometryModel3D(Mesh(piece),material){Transform=transform};geometry.Freeze();
                    (see?transparent:opaque).Add(geometry);
                }
        }
        static readonly string[] Hidden=["shadowonly","fakeshadow","shadowmap","flare","light.tex","sky","window.night","lamp.add"];
        Material? Surface(MaterialInfo info,Color body,Color? paint,out bool see)
        {
            see=false;string effect=info.Effect;
            if(effect.Length==0||Hidden.Any(effect.Contains))return null;
            Material Finish(Brush brush,double shine)
            {
                brush.Freeze();var diffuse=new DiffuseMaterial(brush);
                Material result=shine<=0?diffuse:new MaterialGroup{Children={diffuse,new SpecularMaterial(new SolidColorBrush(Color.FromArgb(255,(byte)(255*shine),(byte)(255*shine),(byte)(255*shine))),40)}};
                result.Freeze();return result;
            }
            if(effect.Contains("glass")){see=true;return Finish(new SolidColorBrush(Color.FromArgb(150,18,22,26)),.5);}
            if(effect.Contains("truckpaint"))return Finish(new SolidColorBrush(body),.35);
            bool alpha=effect.Contains(".a.")||effect.EndsWith(".a",StringComparison.Ordinal)||effect.Contains("decal")||effect.Contains(".over");
            Color? tint=effect.Contains(".paint")&&!alpha?paint??body:null;
            if(info.Texture!=null&&Texture(info.Texture,alpha,tint,paint!=null) is {} bitmap)
            {
                see=alpha;
                var brush=new ImageBrush(bitmap){ViewportUnits=BrushMappingMode.Absolute,Viewport=new Rect(0,0,1,1),TileMode=TileMode.Tile};
                brush.Freeze();var diffuse=new DiffuseMaterial(brush){Color=info.Diffuse};
                Material result=effect.Contains("env")||effect.Contains("chrome")?new MaterialGroup{Children={diffuse,new SpecularMaterial(new SolidColorBrush(Color.FromRgb(90,90,90)),30)}}:diffuse;
                result.Freeze();return result;
            }
            if(alpha)return null;
            var plain=info.Diffuse;
            return Finish(new SolidColorBrush(Color.FromRgb((byte)(plain.R*.55),(byte)(plain.G*.55),(byte)(plain.B*.55))),.1);
        }
        static MeshGeometry3D Mesh(Piece piece)
        {
            var mesh=new MeshGeometry3D();
            var positions=new Point3DCollection(piece.Vertices.Count);var normals=new Vector3DCollection(piece.Vertices.Count);var uv=new PointCollection(piece.Vertices.Count);
            foreach(var v in piece.Vertices)
            {
                positions.Add(new(v.Position.X,v.Position.Y,v.Position.Z));normals.Add(new(v.Normal.X,v.Normal.Y,v.Normal.Z));
                uv.Add(v.TextureCoordinates is {Count:>0} t?new Point(t[0].X,t[0].Y):new Point());
            }
            double facing=0;
            foreach(var t in piece.Triangles.Take(64))
            {
                var a=piece.Vertices[t.A];var b=piece.Vertices[t.B];var c=piece.Vertices[t.C];
                facing+=Num.Vector3.Dot(Num.Vector3.Cross(b.Position-a.Position,c.Position-a.Position),a.Normal+b.Normal+c.Normal);
            }
            var indices=new Int32Collection(piece.Triangles.Count*3);
            foreach(var t in piece.Triangles)
                if(facing>=0){indices.Add(t.A);indices.Add(t.B);indices.Add(t.C);}else{indices.Add(t.A);indices.Add(t.C);indices.Add(t.B);}
            mesh.Positions=positions;mesh.Normals=normals;mesh.TextureCoordinates=uv;mesh.TriangleIndices=indices;mesh.Freeze();
            return mesh;
        }
        public (Model3DGroup,Rect3D) Finish()
        {
            var group=new Model3DGroup();
            foreach(var g in opaque)group.Children.Add(g);
            var bounds=group.Bounds;
            if(!bounds.IsEmpty)group.Children.Add(Shadow(bounds));
            foreach(var g in transparent)group.Children.Add(g);
            group.Freeze();return (group,bounds);
        }
        /// <summary>A soft ellipse on the ground under the vehicle.</summary>
        static GeometryModel3D Shadow(Rect3D b)
        {
            double y=b.Y+0.01,cx=b.X+b.SizeX/2,cz=b.Z+b.SizeZ/2,rx=b.SizeX*.75,rz=b.SizeZ*.62;
            var mesh=new MeshGeometry3D
            {
                Positions=new Point3DCollection{new(cx-rx,y,cz-rz),new(cx+rx,y,cz-rz),new(cx+rx,y,cz+rz),new(cx-rx,y,cz+rz)},
                TextureCoordinates=new PointCollection{new(0,0),new(1,0),new(1,1),new(0,1)},
                TriangleIndices=new Int32Collection{0,2,1,0,3,2},
                Normals=new Vector3DCollection{new(0,1,0),new(0,1,0),new(0,1,0),new(0,1,0)}
            };
            mesh.Freeze();
            var brush=new RadialGradientBrush(Color.FromArgb(150,0,0,0),Color.FromArgb(0,0,0,0));brush.Freeze();
            var material=new EmissiveMaterial(brush);material.Freeze();
            var model=new GeometryModel3D(mesh,material);model.Freeze();return model;
        }
    }
}

/// <summary>Shows a built vehicle on the page background. Drag turns it, the wheel zooms, a double-click resets; arrow keys turn it too.</summary>
public sealed class VehicleStage : Grid
{
    readonly Viewport3D viewport=new(){ClipToBounds=true,IsHitTestVisible=false};
    readonly PerspectiveCamera camera=new(){FieldOfView=32,NearPlaneDistance=.1,FarPlaneDistance=500};
    readonly ModelVisual3D holder=new();
    readonly DirectionalLight key=new(Color.FromRgb(0xFF,0xFC,0xF6),new(0,-1,0)),fill=new(Color.FromRgb(0x7A,0x80,0x88),new(0,-1,0)),rim=new(Color.FromRgb(0x8A,0x86,0x80),new(0,-1,0));
    static readonly Brush Glow=Frozen(new RadialGradientBrush(Color.FromRgb(0x25,0x29,0x2E),Color.FromRgb(0x11,0x13,0x15)){RadiusX=.62,RadiusY=.75,Center=new(.5,.56),GradientOrigin=new(.5,.56)});
    static Brush Frozen(Brush brush){brush.Freeze();return brush;}
    Rect3D bounds=Rect3D.Empty;double yaw=-140,pitch=12,zoom=1;Point? last;
    public VehicleStage()
    {
        viewport.Camera=camera;Background=Glow;Focusable=true;Cursor=Cursors.SizeAll;
        viewport.Children.Add(new ModelVisual3D{Content=new Model3DGroup{Children={new AmbientLight(Color.FromRgb(0x62,0x66,0x6C)),key,fill,rim}}});
        viewport.Children.Add(holder);Children.Add(viewport);
        SizeChanged+=(s,e)=>Place();
        MouseLeftButtonDown+=(s,e)=>{if(e.ClickCount==2){Reset();return;}last=e.GetPosition(this);CaptureMouse();Focus();};
        MouseMove+=(s,e)=>{if(last is not {} from||!IsMouseCaptured)return;var p=e.GetPosition(this);yaw-=(p.X-from.X)*.4;pitch=Math.Clamp(pitch+(p.Y-from.Y)*.25,-2,60);last=p;Place();};
        MouseLeftButtonUp+=(s,e)=>{last=null;ReleaseMouseCapture();};
        MouseWheel+=(s,e)=>{zoom=Math.Clamp(zoom*(e.Delta>0?1/1.12:1.12),.45,2.2);Place();e.Handled=true;};
        KeyDown+=(s,e)=>{if(e.Key is Key.Left or Key.Right){yaw+=e.Key==Key.Left?10:-10;Place();e.Handled=true;}else if(e.Key==Key.Home){Reset();e.Handled=true;}};
    }
    public void Show(PreviewModel? model){holder.Content=model?.Model;bounds=model?.Bounds??Rect3D.Empty;Place();}
    public void Reset(){yaw=-140;pitch=12;zoom=1;Place();}
    internal void Turn(double angle){yaw=angle;Place();}
    void Place()
    {
        if(bounds.IsEmpty||ActualWidth<=0||ActualHeight<=0)return;
        var center=new Point3D(bounds.X+bounds.SizeX/2,bounds.Y+bounds.SizeY/2,bounds.Z+bounds.SizeZ/2);
        double y=yaw*Math.PI/180,p=pitch*Math.PI/180;
        var offset=new Vector3D(Math.Cos(p)*Math.Sin(y),Math.Sin(p),Math.Cos(p)*Math.Cos(y));
        var right=Vector3D.CrossProduct(-offset,new Vector3D(0,1,0));right.Normalize();var up=Vector3D.CrossProduct(right,-offset);
        double tanH=Math.Tan(camera.FieldOfView*Math.PI/360)*.9,tanV=tanH*ActualHeight/ActualWidth*.96,distance=0;
        foreach(double cx in new[]{bounds.X,bounds.X+bounds.SizeX})foreach(double cy in new[]{bounds.Y,bounds.Y+bounds.SizeY})foreach(double cz in new[]{bounds.Z,bounds.Z+bounds.SizeZ})
        {
            var corner=new Point3D(cx,cy,cz)-center;double depth=Vector3D.DotProduct(corner,offset);
            distance=Math.Max(distance,Math.Max(depth+Math.Abs(Vector3D.DotProduct(corner,right))/tanH,depth+Math.Abs(Vector3D.DotProduct(corner,up))/tanV));
        }
        distance*=zoom;
        camera.Position=center+offset*distance;camera.LookDirection=-offset*distance;camera.UpDirection=new(0,1,0);
        key.Direction=-offset+right*.55+new Vector3D(0,-.9,0);fill.Direction=-offset-right*.8+new Vector3D(0,-.2,0);rim.Direction=offset*1.2+new Vector3D(0,-.5,0);
    }
}
