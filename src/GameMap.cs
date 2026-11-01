using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TruckLib.HashFs;
using TruckLib.ScsMap;

namespace Werkstatt;

public sealed record CityInfo(string Id,string Name,string Country,string Dlc);
public sealed record MapCity(string Id,string Name,string Country,string Dlc,double X,double Y);
public sealed record MapRoad(double X1,double Y1,double X2,double Y2);
public sealed class MapData
{
    public string Signature {get;set;}="";
    public List<MapCity> Cities {get;set;}=[];
    public List<MapRoad> Roads {get;set;}=[];
    public List<string> Warnings {get;set;}=[];
}
public static class GameMap
{
    static string Signature(string kind,IEnumerable<string> paths)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind+"|"+string.Join("|",paths.Select(p=>$"{p}:{new FileInfo(p).Length}:{File.GetLastWriteTimeUtc(p).Ticks}")))));
    /// <summary>Roads and city positions from every map sector in base_map.scs and the DLC archives, cached per game version.</summary>
    public static MapData Load(Installation install,IProgress<string>? progress=null,CancellationToken cancel=default)
    {
        string root=install.GamePath??throw new InvalidOperationException(L.T("core.pickfolder"));
        var paths=new[]{"def.scs","base.scs","base_map.scs"}.Select(f=>Path.Combine(root,f)).Where(File.Exists).Concat(install.Dlcs.Select(d=>d.File)).ToList();
        string signature=Signature("map-v3",paths);
        string cache=Path.Combine(SaveSession.DataRoot,"map-"+signature[..16]+".json");
        if(File.Exists(cache))
        {
            try {var cached=JsonSerializer.Deserialize<MapData>(File.ReadAllText(cache)); if(cached?.Signature==signature){progress?.Report(L.T("map.cache"));Touch(cache);return cached;}}
            catch(Exception e) when(e is JsonException or IOException){}
        }
        var data=new MapData{Signature=signature};
        var definitions=new Dictionary<string,(string Name,string Country,string Dlc)>();
        var positions=new Dictionary<string,(double X,double Y)>();
        var roads=new Dictionary<ulong,MapRoad>();
        foreach(var path in paths)
        {
            cancel.ThrowIfCancellationRequested();
            string dlc=install.Dlcs.FirstOrDefault(d=>d.File==path)?.Name??"Basisspiel";
            progress?.Report(L.F("map.cities",Path.GetFileName(path)));
            try
            {
                using var fs=HashFsReader.Open(path);
                ReadCities(fs,dlc,definitions);
                List<string> sectorFiles=[];
                try {sectorFiles=fs.GetDirectoryListing("map/europe").Files.Where(f=>f.EndsWith(".base",StringComparison.Ordinal)).ToList();}catch(FileNotFoundException){}
                for(int index=0;index<sectorFiles.Count;index++)
                {
                    cancel.ThrowIfCancellationRequested();
                    if(index%8==0) progress?.Report(L.F("map.sector",dlc=="Basisspiel"?L.T("dlc.base"):dlc,index+1,sectorFiles.Count));
                    var m=Regex.Match(sectorFiles[index],@"sec([+-]\d+)([+-]\d+)\.base$");
                    if(!m.Success) continue;
                    try
                    {
                        var map=new Map();
                        map.Read("map/europe",fs,[new SectorCoordinate(int.Parse(m.Groups[1].Value),int.Parse(m.Groups[2].Value))]);
                        foreach(var city in map.MapItems.Values.OfType<CityArea>())
                            if(city.ShowInUi && city.Node is Node n) positions[city.Name.ToString()]=(n.Position.X,n.Position.Z);
                        foreach(var road in map.MapItems.Values.OfType<Road>())
                            if(road.Node is Node a && road.ForwardNode is Node b)
                                roads[road.Uid]=new(a.Position.X,a.Position.Z,b.Position.X,b.Position.Z);
                    }
                    catch(Exception e) when(e is not OperationCanceledException and not OutOfMemoryException)
                    {if(data.Warnings.Count<20) data.Warnings.Add($"{Path.GetFileName(path)} / {Path.GetFileName(sectorFiles[index])}: {e.Message}");}
                }
            }
            catch(Exception e) when(e is not OperationCanceledException and not OutOfMemoryException)
            {data.Warnings.Add($"{Path.GetFileName(path)}: {e.Message}");}
        }
        foreach(var (id,pos) in positions)
        {
            var info=definitions.GetValueOrDefault(id,(Name:id,Country:"",Dlc:"Basisspiel"));
            data.Cities.Add(new(id,info.Name,info.Country,info.Dlc,pos.X,pos.Y));
        }
        data.Cities=data.Cities.OrderBy(c=>c.Name).ToList();data.Roads=roads.Values.ToList();
        if(data.Cities.Count==0) throw new InvalidDataException(L.T("map.nocities"));
        Directory.CreateDirectory(SaveSession.DataRoot);
        if(data.Warnings.Count==0){File.WriteAllText(cache,JsonSerializer.Serialize(data));Touch(cache);}
        return data;
    }
    static void ReadCities(IHashFsReader fs,string dlc,Dictionary<string,(string Name,string Country,string Dlc)> definitions)
    {
        List<string> files=[];
        try {files=fs.GetDirectoryListing("def/city").Files;}catch(FileNotFoundException){}
        foreach(var f in files.Where(f=>f.EndsWith(".sui",StringComparison.Ordinal)||f.EndsWith(".sii",StringComparison.Ordinal)))
        {
            var text=Encoding.UTF8.GetString(fs.Extract(f)[0]);
            var id=Regex.Match(text,@"city_data\s*:\s*city\.([a-zA-Z0-9_]+)");
            if(!id.Success) continue;
            var name=Regex.Match(text,"city_name:\\s*(\"[^\"]*\")");
            var country=Regex.Match(text,@"country:\s*(\w+)");
            definitions[id.Groups[1].Value]=(name.Success?SiiDocument.Display(name.Groups[1].Value):id.Groups[1].Value,country.Groups[1].Value,dlc);
        }
    }
    /// <summary>Marks <paramref name="current"/> as used and deletes the other caches of its kind; a map cache is about 20 MB.</summary>
    static void Touch(string current)
    {
        try
        {
            File.SetLastWriteTimeUtc(current,DateTime.UtcNow);
            string prefix=Path.GetFileName(current).Split('-')[0]+"-";
            foreach(var stale in Directory.EnumerateFiles(SaveSession.DataRoot,prefix+"*.json").Where(f=>!string.Equals(f,current,StringComparison.OrdinalIgnoreCase)))
                try{File.Delete(stale);}catch(Exception e) when(e is IOException or UnauthorizedAccessException){}
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException){}
    }
    /// <summary>City names, countries and DLCs from def/city in def.scs and every dlc_*.scs, without the slow map sectors.
    /// Archives that can't be read are named in <paramref name="warnings"/>; the cache is then not written.</summary>
    public static List<CityInfo> Cities(Installation install,List<string>? warnings=null)
    {
        if(install.GamePath is not {} root)return [];
        var paths=new[]{"def.scs"}.Select(f=>Path.Combine(root,f)).Where(File.Exists).Concat(install.Dlcs.Select(d=>d.File)).ToList();
        string signature=Signature("cities-v3",paths);
        string cache=Path.Combine(SaveSession.DataRoot,"cities-"+signature[..16]+".json");
        if(File.Exists(cache))
        {
            try {if(JsonSerializer.Deserialize<List<CityInfo>>(File.ReadAllText(cache)) is {Count:>0} cached&&cached.All(c=>c.Id is {Length:>0}&&c.Name!=null&&c.Country!=null)){Touch(cache);return cached;}}
            catch(Exception e) when(e is JsonException or IOException or NotSupportedException){}
        }
        var definitions=new Dictionary<string,(string Name,string Country,string Dlc)>();bool complete=true;
        foreach(var path in paths)
        {
            try {using var fs=HashFsReader.Open(path);ReadCities(fs,install.Dlcs.FirstOrDefault(d=>d.File==path)?.Name??"Basisspiel",definitions);}
            catch(Exception e) when(e is not OutOfMemoryException){complete=false;warnings?.Add($"{Path.GetFileName(path)}: {e.Message}");}
        }
        var cities=definitions.Select(d=>new CityInfo(d.Key,d.Value.Name,d.Value.Country,d.Value.Dlc)).ToList();
        if(complete&&cities.Count>0){Directory.CreateDirectory(SaveSession.DataRoot);File.WriteAllText(cache,JsonSerializer.Serialize(cities));Touch(cache);}
        return cities;
    }
}

public sealed class MapView : FrameworkElement
{
    public MapData? Data {get;set;}
    public HashSet<string> Owned {get;set;}=[];
    public HashSet<string> Available {get;set;}=[];
    public string? Selected {get;set;}
    public Action<string>? CitySelected {get;set;}
    public bool OnlyOwned {get;set;}
    sealed record Tile(StreamGeometry Geometry,Rect Bounds);
    sealed record Prepared(Rect CityBounds,Rect RoadBounds,System.Windows.Media.Imaging.BitmapSource Atlas,List<Tile> Tiles);
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MapData,Prepared> Cache=new();
    static readonly Brush BackgroundBrush=Ui.MapGround,RoadBrush=Ui.Brush("#535D65"),NeutralBrush=Ui.Brush("#ACB1B6");
    static readonly Pen GridPen=FrozenPen(Ui.Brush("#1B1E21"),1),NeutralPen=FrozenPen(NeutralBrush,1.5),OwnedPen=FrozenPen(Ui.Accent,1.5),SelectedPen=FrozenPen(Ui.Accent,2),HaloPen=Halo();
    static Pen Halo(){var pen=new Pen(Ui.Bg,6){LineJoin=PenLineJoin.Round};pen.Freeze();return pen;}
    static readonly Typeface Typeface=new(Ui.Font,FontStyles.Normal,FontWeights.Normal,FontStretches.Normal);
    readonly Dictionary<(string,int),(FormattedText Text,Geometry Outline)> labels=[];
    Prepared? prepared;double zoom=1,baseScale=1,centerX,centerY,labelDpi;
    double renderedZoom=1;Vector renderedPan;bool rendered;
    Vector pan,panStart;Point down;bool dragging;Pen? roadPen;double penScale;
    readonly DispatcherTimer detailTimer=new(){Interval=TimeSpan.FromMilliseconds(120)};
    static Pen FrozenPen(Brush color,double width){var pen=new Pen(color,width);pen.Freeze();return pen;}
    public MapView()
    {
        Focusable=true;MinHeight=180;Cursor=Cursors.Hand;
        CacheMode=new BitmapCache{SnapsToDevicePixels=false};
        Loaded+=(s,e)=>ScaleCache();
        detailTimer.Tick+=(s,e)=>{detailTimer.Stop();Redraw();};Unloaded+=(s,e)=>detailTimer.Stop();
        AutomationName();RenderOptions.SetBitmapScalingMode(this,BitmapScalingMode.HighQuality);
        SizeChanged+=(s,e)=>Redraw();
        MouseWheel+=(s,e)=>{ZoomAt(Pointer(e),e.Delta>0?1.2:1/1.2);e.Handled=true;};
        MouseLeftButtonDown+=(s,e)=>{down=Pointer(e);panStart=pan;dragging=true;CaptureMouse();Focus();};
        MouseMove+=(s,e)=>{if(!dragging)return;pan=panStart+(Pointer(e)-down);var moved=pan-renderedPan;if(Math.Abs(moved.X)>ActualWidth*.2||Math.Abs(moved.Y)>ActualHeight*.2)Redraw();else Live();};
        LostMouseCapture+=(s,e)=>{if(dragging){dragging=false;Redraw();}};
        MouseLeftButtonUp+=(s,e)=>
        {
            if(!dragging)return;var p=Pointer(e);dragging=false;ReleaseMouseCapture();
            if((p-down).Length<5&&Data!=null&&prepared!=null)
            {
                var city=Data.Cities.Where(c=>Available.Contains(c.Id)&&(!OnlyOwned||Owned.Contains(c.Id))).MinBy(c=>(Project(c.X,c.Y)-p).LengthSquared);
                if(city!=null&&(Project(city.X,city.Y)-p).Length<15){Selected=city.Id;CitySelected?.Invoke(city.Id);}
            }
            Redraw();
        };
        KeyDown+=(s,e)=>{if(e.Key==Key.Home)Reset();if(e.Key is Key.Add or Key.OemPlus)Zoom(1.2);if(e.Key is Key.Subtract or Key.OemMinus)Zoom(1/1.2);if(e.Key is Key.Left or Key.Right or Key.Up or Key.Down){PanBy(e.Key switch{Key.Left=>new(40,0),Key.Right=>new(-40,0),Key.Up=>new(0,40),_=>new(0,-40)});e.Handled=true;}};
    }
    void ScaleCache(){if(CacheMode is BitmapCache cache)cache.RenderAtScale=Math.Max(1,VisualTreeHelper.GetDpi(this).PixelsPerDip);}
    protected override void OnDpiChanged(DpiScale oldDpi,DpiScale newDpi){base.OnDpiChanged(oldDpi,newDpi);ScaleCache();Redraw();}
    /// <summary>Pointer position in viewport coordinates. GetPosition is relative to the moved frame, so the live transform is applied back.</summary>
    Point Pointer(MouseEventArgs e)=>RenderTransform.Value.Transform(e.GetPosition(this));
    void AutomationName()=>System.Windows.Automation.AutomationProperties.SetName(this,L.T("map.a11y"));
    public void Reset(){zoom=1;pan=new();Redraw();}
    internal (double Zoom,Vector Pan) Viewport=>(zoom,pan);
    internal void RestoreViewport((double Zoom,Vector Pan) view){zoom=view.Zoom;pan=view.Pan;Redraw();}
    public void CenterSelected()
    {
        if(prepared==null||Data?.Cities.FirstOrDefault(c=>c.Id==Selected) is not {} city||ActualWidth<=0||ActualHeight<=0)return;
        UpdateProjection();zoom=Math.Max(zoom,5);var position=Project(city.X,city.Y);pan+=new Vector(ActualWidth/2-position.X,ActualHeight/2-position.Y);Live();
    }
    internal void PanBy(Vector delta){pan+=delta;Live();}
    /// <summary>Moves the last drawn frame to the live viewport and schedules a sharp redraw once input pauses.</summary>
    void Live()
    {
        double k=zoom/renderedZoom;
        if(!rendered||k<.6){Redraw();return;}
        RenderTransform=new MatrixTransform(k,0,0,k,pan.X+centerX-(renderedPan.X+centerX)*k,pan.Y+centerY-(renderedPan.Y+centerY)*k);
        detailTimer.Stop();if(!dragging)detailTimer.Start();
    }
    void Redraw(){detailTimer.Stop();RenderTransform=Transform.Identity;base.InvalidateVisual();}
    public new void InvalidateVisual()=>Redraw();
    public void Zoom(double factor)=>ZoomAt(new(ActualWidth/2,ActualHeight/2),factor);
    void ZoomAt(Point point,double factor)
    {
        double previous=zoom;zoom=Math.Clamp(zoom*factor,.75,30);double ratio=zoom/previous;
        pan=new((pan.X+centerX-point.X)*ratio+point.X-centerX,(pan.Y+centerY-point.Y)*ratio+point.Y-centerY);Live();
    }
    /// <summary>Redraws once input pauses; typing in the garage search must not redraw 250k road segments per key.</summary>
    public void Refresh(){detailTimer.Stop();detailTimer.Start();}
    /// <summary>Builds the road atlas and tiles on a background STA thread so opening the garages page does not freeze. All results are frozen.</summary>
    internal static Task PrepareAsync(MapData data)
    {
        var done=new TaskCompletionSource();
        var thread=new Thread(()=>{try{Cache.GetValue(data,Prepare);done.SetResult();}catch(Exception e){done.SetException(e);}}){IsBackground=true,Name="map-prepare"};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task;
    }
    public void Rebuild(){prepared=Data?.Cities.Count>0?Cache.GetValue(Data,Prepare):null;labels.Clear();InvalidateVisual();}
    static Prepared Prepare(MapData data)
    {
        var cityBounds=new Rect(data.Cities.Min(c=>c.X),data.Cities.Min(c=>c.Y),Math.Max(1,data.Cities.Max(c=>c.X)-data.Cities.Min(c=>c.X)),Math.Max(1,data.Cities.Max(c=>c.Y)-data.Cities.Min(c=>c.Y)));
        var groups=new Dictionary<(int,int),List<MapRoad>>();var full=new StreamGeometry();
        using(var drawing=full.Open())foreach(var road in data.Roads)
        {
            drawing.BeginFigure(new(road.X1,road.Y1),false,false);drawing.LineTo(new(road.X2,road.Y2),true,false);
            var key=((int)Math.Floor((road.X1+road.X2)/8000),(int)Math.Floor((road.Y1+road.Y2)/8000));
            if(!groups.TryGetValue(key,out var list))groups[key]=list=[];list.Add(road);
        }
        full.Freeze();var roadBounds=full.IsEmpty()?cityBounds:full.Bounds;roadBounds.Inflate(200,200);
        double scale=2048/Math.Max(roadBounds.Width,roadBounds.Height);int width=Math.Max(1,(int)Math.Ceiling(roadBounds.Width*scale)),height=Math.Max(1,(int)Math.Ceiling(roadBounds.Height*scale));
        var visual=new DrawingVisual();using(var dc=visual.RenderOpen())
        {
            dc.PushTransform(new MatrixTransform(scale,0,0,scale,-roadBounds.X*scale,-roadBounds.Y*scale));dc.DrawGeometry(null,FrozenPen(RoadBrush,4/scale),full);dc.Pop();
        }
        var atlas=new System.Windows.Media.Imaging.RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);atlas.Render(visual);atlas.Freeze();
        var tiles=new List<Tile>();foreach(var roads in groups.Values)
        {
            var geometry=new StreamGeometry();using(var dc=geometry.Open())foreach(var road in roads){dc.BeginFigure(new(road.X1,road.Y1),false,false);dc.LineTo(new(road.X2,road.Y2),true,false);}
            geometry.Freeze();tiles.Add(new(geometry,geometry.Bounds));
        }
        return new(cityBounds,roadBounds,atlas,tiles);
    }
    Point Project(double x,double y)=>new((x-prepared!.CityBounds.X)*baseScale*zoom+pan.X+centerX,(y-prepared.CityBounds.Y)*baseScale*zoom+pan.Y+centerY);
    void UpdateProjection()
    {
        var bounds=prepared!.CityBounds;baseScale=Math.Min(Math.Max(1,ActualWidth-56)/bounds.Width,Math.Max(1,ActualHeight-56)/bounds.Height);
        centerX=(ActualWidth-bounds.Width*baseScale)/2;centerY=(ActualHeight-bounds.Height*baseScale)/2;
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double mx=ActualWidth/2,my=ActualHeight/2;var area=new Rect(-mx,-my,ActualWidth+2*mx,ActualHeight+2*my);
        dc.PushClip(new RectangleGeometry(area));dc.DrawRectangle(BackgroundBrush,null,area);
        for(double x=pan.X%72-Math.Ceiling(mx/72)*72;x<area.Right;x+=72)dc.DrawLine(GridPen,new(x,area.Top),new(x,area.Bottom));
        for(double y=pan.Y%72-Math.Ceiling(my/72)*72;y<area.Bottom;y+=72)dc.DrawLine(GridPen,new(area.Left,y),new(area.Right,y));
        renderedZoom=zoom;renderedPan=pan;rendered=ActualWidth>0;
        if(prepared==null||Data==null){dc.Pop();return;}
        var bounds=prepared.CityBounds;UpdateProjection();
        double scale=baseScale*zoom;double dpi=VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if(prepared.RoadBounds.Width*scale*dpi<=prepared.Atlas.PixelWidth*.75)
        {
            var top=Project(prepared.RoadBounds.X,prepared.RoadBounds.Y);dc.DrawImage(prepared.Atlas,new Rect(top,new Size(prepared.RoadBounds.Width*scale,prepared.RoadBounds.Height*scale)));
        }
        else
        {
            var viewport=new Rect((area.X-pan.X-centerX)/scale+bounds.X,(area.Y-pan.Y-centerY)/scale+bounds.Y,area.Width/scale,area.Height/scale);viewport.Inflate(2/scale,2/scale);
            if(roadPen==null||penScale!=scale){penScale=scale;roadPen=FrozenPen(RoadBrush,1.3/scale);}
            dc.PushTransform(new MatrixTransform(scale,0,0,scale,centerX+pan.X-bounds.X*scale,centerY+pan.Y-bounds.Y*scale));
            foreach(var tile in prepared.Tiles)if(tile.Bounds.IntersectsWith(viewport))dc.DrawGeometry(null,roadPen,tile.Geometry);dc.Pop();
        }
        if(dpi!=labelDpi){labels.Clear();labelDpi=dpi;}
        var visible=Data.Cities.Where(c=>Available.Contains(c.Id)&&(!OnlyOwned||Owned.Contains(c.Id))).Select(c=>(City:c,Point:Project(c.X,c.Y),State:c.Id==Selected?2:Owned.Contains(c.Id)?1:0)).Where(c=>area.Contains(c.Point)).ToList();
        foreach(var (_,point,state) in visible.OrderBy(c=>c.State))dc.DrawEllipse(BackgroundBrush,state==2?SelectedPen:state==1?OwnedPen:NeutralPen,point,state==2?6:state==1?4:2.5,state==2?6:state==1?4:2.5);
        var used=new List<Rect>();var names=new List<(FormattedText Text,Geometry Outline,Point Origin)>();
        foreach(var (city,point,state) in visible.Where(c=>c.State>0||zoom>3).OrderByDescending(c=>c.State))
        {
            if(!labels.TryGetValue((city.Id,state),out var label))
            {
                var text=new FormattedText(city.Name,L.Culture,FlowDirection.LeftToRight,Typeface,state==2?13:12,state>0?Ui.Accent:NeutralBrush,labelDpi);var outline=text.BuildGeometry(new Point());outline.Freeze();
                labels[(city.Id,state)]=label=(text,outline);
            }
            var box=new Rect(point.X+9,point.Y-8,label.Text.Width+5,label.Text.Height);
            if(!used.Any(r=>r.IntersectsWith(box))){used.Add(box);names.Add((label.Text,label.Outline,new(box.X+2,box.Y)));}
        }
        for(int i=names.Count-1;i>=0;i--)
        {
            var (text,outline,origin)=names[i];
            dc.PushTransform(new TranslateTransform(origin.X,origin.Y));dc.DrawGeometry(null,HaloPen,outline);dc.Pop();dc.DrawText(text,origin);
        }
        dc.Pop();
    }
}
