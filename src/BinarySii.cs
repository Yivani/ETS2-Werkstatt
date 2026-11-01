using System.Globalization;
using System.Text;

namespace Werkstatt;

internal sealed class BinarySii
{
    sealed record Field(uint Type,string Name,Dictionary<uint,byte[]> Ordinals);
    sealed record Prototype(string Name,List<Field> Fields);
    internal sealed record SpanField(string Unit,string Name,uint Type,int Start,int Length);
    readonly byte[] bytes;
    readonly List<SpanField> spans=[];
    public IReadOnlyList<SpanField> Fields=>spans;
    public string Text {get;}
    bool unresolvedOrdinal;
    public string CompatibleText=>unresolvedOrdinal?throw new InvalidDataException(L.T("core.bsiienum")):Text;
    readonly uint version;
    const string Characters="0123456789abcdefghijklmnopqrstuvwxyz_";
    static readonly CultureInfo Invariant=CultureInfo.InvariantCulture;

    public BinarySii(byte[] data)
    {
        bytes=data;
        try
        {
            using var reader=new BinaryReader(new MemoryStream(data),Encoding.UTF8);
            if(!reader.ReadBytes(4).AsSpan().SequenceEqual("BSII"u8))throw new InvalidDataException(L.F("core.parse","not a BSII save"));
            version=reader.ReadUInt32();
            if(version is <1 or >3)throw new InvalidDataException(L.F("core.bsiiversion",version));
            var prototypes=new Dictionary<uint,Prototype>();var text=new StringBuilder("SiiNunit\n{\n");
            while(true)
            {
                uint type=reader.ReadUInt32();
                if(type==0)
                {
                    byte valid=reader.ReadByte();
                    if(valid==0){if(reader.BaseStream.Position!=data.Length)throw new InvalidDataException(L.F("core.parse","data after the BSII end"));break;}
                    if(valid!=1)throw new InvalidDataException(L.F("core.parse","invalid BSII structure"));
                    uint id=reader.ReadUInt32();string name=Name(reader);var fields=new List<Field>();
                    while(true)
                    {
                        uint fieldType=reader.ReadUInt32();if(fieldType==0)break;
                        string fieldName=Name(reader);var ordinals=new Dictionary<uint,byte[]>();
                        if(fieldType==0x37)
                        {
                            int count=Count(reader);
                            for(int i=0;i<count;i++)if(!ordinals.TryAdd(reader.ReadUInt32(),RawString(reader)))throw new InvalidDataException(L.F("core.parse","duplicate BSII enum value"));
                        }
                        if(fields.Any(f=>f.Name==fieldName))throw new InvalidDataException(L.F("core.parse","duplicate BSII field"));
                        fields.Add(new(fieldType,fieldName,ordinals));
                    }
                    if(id==0||!prototypes.TryAdd(id,new(name,fields)))throw new InvalidDataException(L.F("core.parse","duplicate BSII structure"));
                    continue;
                }
                if(!prototypes.TryGetValue(type,out var proto))throw new InvalidDataException(L.F("core.parse","unknown BSII structure"));
                string unit=ReadId(reader);text.Append(proto.Name).Append(" : ").Append(unit).Append(" {\n");
                foreach(var field in proto.Fields)
                {
                    int start=checked((int)reader.BaseStream.Position);
                    uint element=Element(field.Type);
                    if(element!=0)
                    {
                        int count=Count(reader);text.Append("  ").Append(field.Name).Append(": ").Append(count.ToString(Invariant)).Append('\n');
                        for(int i=0;i<count;i++)text.Append("  ").Append(field.Name).Append('[').Append(i.ToString(Invariant)).Append("]: ").Append(Value(reader,field,element)).Append('\n');
                    }
                    else text.Append("  ").Append(field.Name).Append(": ").Append(Value(reader,field,field.Type)).Append('\n');
                    spans.Add(new(unit,field.Name,field.Type,start,checked((int)reader.BaseStream.Position-start)));
                }
                text.Append("}\n");
            }
            Text=text.Append("}\n").ToString();
        }
        catch(Exception e) when(e is EndOfStreamException or OverflowException or DecoderFallbackException)
        {throw new InvalidDataException(L.T("core.bsiitruncated"),e);}
    }
    static int Count(BinaryReader r)
    {
        uint n=r.ReadUInt32();
        if(n>r.BaseStream.Length-r.BaseStream.Position)throw new InvalidDataException(L.F("core.parse","BSII length exceeds the file"));
        return checked((int)n);
    }
    static byte[] RawString(BinaryReader r)=>r.ReadBytes(Count(r));
    static string Name(BinaryReader r)
    {
        string name=new UTF8Encoding(false,true).GetString(RawString(r));
        if(name.Length==0||!(char.IsAsciiLetter(name[0])||name[0]=='_')||name.Any(c=>!(char.IsAsciiLetterOrDigit(c)||c=='_')))throw new InvalidDataException(L.F("core.parse","invalid BSII field name"));
        return name;
    }
    static string Encoded(BinaryReader r)
    {
        ulong n=r.ReadUInt64()&0x7fffffffffffffff;var text=new StringBuilder();
        while(n!=0){int i=(int)(n%38);n/=38;if(i==0)throw new InvalidDataException(L.F("core.parse","invalid BSII token"));text.Append(Characters[i-1]);}
        return text.ToString();
    }
    static string ReadId(BinaryReader r)
    {
        byte count=r.ReadByte();if(count==0)return "null";
        if(count!=255)return string.Join('.',Enumerable.Range(0,count).Select(_=>Encoded(r)));
        ulong n=r.ReadUInt64();var groups=new List<ushort>();do{groups.Add((ushort)n);n>>=16;}while(n!=0);
        return "_nameless."+groups[^1].ToString("x",Invariant)+string.Concat(groups.Take(groups.Count-1).Reverse().Select(g=>"."+g.ToString("x4",Invariant)));
    }
    static string Quote(byte[] data)
    {
        if(data.Length>0&&data.All(b=>char.IsAsciiLetterOrDigit((char)b)||b=='_'))return Encoding.ASCII.GetString(data);
        var text=new StringBuilder("\"");
        foreach(byte b in data)
        {
            if(b is (byte)'"' or (byte)'\\')text.Append('\\').Append((char)b);
            else if(b is >=32 and <127)text.Append((char)b);
            else text.Append("\\x").Append(b.ToString("x2",Invariant));
        }
        return text.Append('"').ToString();
    }
    static uint FloatBits(string? value)=>value is {Length:9}&&value[0]=='&'?uint.Parse(value.AsSpan(1),NumberStyles.HexNumber,Invariant):throw new InvalidDataException(L.T("core.binaryfield"));
    static string F(float value)=>"&"+BitConverter.SingleToUInt32Bits(value).ToString("x8",Invariant);
    string Value(BinaryReader r,Field field,uint type)
    {
        string Vector(int n,bool quaternion=false)
        {
            var values=Enumerable.Range(0,n).Select(_=>F(r.ReadSingle())).ToArray();
            return "("+(quaternion?values[0]+"; "+string.Join(", ",values.Skip(1)):string.Join(", ",values))+")";
        }
        switch(type)
        {
            case 0x01:return Quote(RawString(r));
            case 0x03:var token=Encoded(r);return token.Length==0?"\"\"":token;
            case 0x05:return F(r.ReadSingle());
            case 0x07:return Vector(2);
            case 0x09:return Vector(3);
            case 0x11:return $"({r.ReadInt32().ToString(Invariant)}, {r.ReadInt32().ToString(Invariant)}, {r.ReadInt32().ToString(Invariant)})";
            case 0x17:return Vector(4,true);
            case 0x19:
                if(version==1)return Vector(3)+" "+Vector(4,true);
                var v=Enumerable.Range(0,8).Select(_=>r.ReadSingle()).ToArray();long coef=float.IsFinite(v[3])?(long)v[3]:0;
                float x=v[0]+(float)(((coef&0xfff)-2048)<<9),z=v[2]+(float)((((coef>>12)&0xfff)-2048)<<9);
                return $"({F(x)}, {F(v[1])}, {F(z)}) ({F(v[4])}; {F(v[5])}, {F(v[6])}, {F(v[7])})";
            case 0x25:return r.ReadInt32().ToString(Invariant);
            case 0x27:case 0x2f:uint u32=r.ReadUInt32();return u32==uint.MaxValue?"nil":u32.ToString(Invariant);
            case 0x29:return r.ReadInt16().ToString(Invariant);
            case 0x2b:ushort u16=r.ReadUInt16();return u16==ushort.MaxValue?"nil":u16.ToString(Invariant);
            case 0x31:return r.ReadInt64().ToString(Invariant);
            case 0x33:ulong u64=r.ReadUInt64();return u64==ulong.MaxValue?"nil":u64.ToString(Invariant);
            case 0x35:return r.ReadByte()!=0?"true":"false";
            case 0x37:uint ordinal=r.ReadUInt32();if(field.Ordinals.TryGetValue(ordinal,out var raw))return Quote(raw);unresolvedOrdinal=true;return ordinal.ToString(Invariant);
            case 0x39:case 0x3b:case 0x3d:return ReadId(r);
            case 0x41:return $"({r.ReadInt32().ToString(Invariant)}, {r.ReadInt32().ToString(Invariant)})";
            default:throw new InvalidDataException(L.F("core.bsiitype",type.ToString("x",Invariant)));
        }
    }
    static uint Element(uint type)=>type switch
    {
        0x02=>0x01,0x04=>0x03,0x06=>0x05,0x08=>0x07,0x0a=>0x09,0x12=>0x11,0x18=>0x17,0x1a=>0x19,
        0x26=>0x25,0x28=>0x27,0x2a=>0x29,0x2c=>0x2b,0x32=>0x31,0x34=>0x33,0x36=>0x35,0x3a=>0x39,0x3c=>0x3b,0x3e=>0x3d,_=>0
    };
    static void WriteToken(BinaryWriter w,string text)
    {
        ulong n=0;foreach(char c in text.Reverse()){int i=Characters.IndexOf(c);if(i<0)throw new InvalidDataException(L.T("core.badref"));n=checked(n*38+(uint)i+1);}
        if(n>0x7fffffffffffffff)throw new InvalidDataException(L.T("core.badref"));w.Write(n);
    }
    static void WriteId(BinaryWriter w,string text)
    {
        if(text=="null"){w.Write((byte)0);return;}
        if(text.StartsWith("_nameless.",StringComparison.Ordinal))
        {
            var groups=text[10..].Split('.');if(groups.Length>4)throw new InvalidDataException(L.T("core.badref"));
            ulong id=0;foreach(string group in groups)id=checked((id<<16)|ushort.Parse(group,NumberStyles.HexNumber,Invariant));
            w.Write((byte)255);w.Write(id);return;
        }
        var parts=text.Split('.');if(parts.Length is 0 or >254||parts.Any(p=>p.Length==0))throw new InvalidDataException(L.T("core.badref"));
        w.Write((byte)parts.Length);foreach(string part in parts)WriteToken(w,part);
    }
    /// <summary>Bytes of a quoted or bare SII string; only spellings that <see cref="Quote"/> reproduces exactly are accepted.</summary>
    static byte[] Unquote(string value)
    {
        var data=new List<byte>();
        if(!value.StartsWith('"'))data.AddRange(Encoding.ASCII.GetBytes(value));
        else for(int i=1;i<value.Length-1;i++)
        {
            char c=value[i];
            if(c!='\\'){if(c is <' ' or >'~')throw new InvalidDataException(L.T("core.binaryfit"));data.Add((byte)c);continue;}
            if(++i>=value.Length-1)throw new InvalidDataException(L.T("core.binaryfit"));
            if(value[i]=='x'&&i+2<value.Length-1){data.Add(byte.Parse(value.AsSpan(i+1,2),NumberStyles.HexNumber,Invariant));i+=2;}else data.Add((byte)value[i]);
        }
        var bytes=data.ToArray();if(Quote(bytes)!=value)throw new InvalidDataException(L.T("core.binaryfit"));
        return bytes;
    }
    static void Write(BinaryWriter w,uint type,string value)
    {
        switch(type)
        {
            case 0x01:var text=Unquote(value);w.Write((uint)text.Length);w.Write(text);break;
            case 0x03:if(value=="\"\"")w.Write(0UL);else WriteToken(w,value);break;
            case 0x05:w.Write(FloatBits(value));break;
            case 0x25:w.Write(int.Parse(value,Invariant));break;
            case 0x27:case 0x2f:w.Write(value=="nil"?uint.MaxValue:uint.Parse(value,Invariant));break;
            case 0x31:w.Write(long.Parse(value,Invariant));break;
            case 0x33:w.Write(ulong.Parse(value,Invariant));break;
            case 0x35:w.Write(value switch{"true"=>(byte)1,"false"=>(byte)0,_=>throw new InvalidDataException(L.T("core.binaryfit"))});break;
            case 0x39:case 0x3b:case 0x3d:WriteId(w,value);break;
            default:throw new InvalidDataException(L.T("core.binaryfield"));
        }
    }
    public byte[] Patch(SiiDocument original,SiiDocument edited)
    {
        var patches=new List<(SpanField Field,byte[] Value)>();
        foreach(var field in spans)
        {
            var before=original.Find(field.Unit);var after=edited.Find(field.Unit);uint element=Element(field.Type);
            bool changed=before.Get(field.Name)!=after.Get(field.Name);
            if(element!=0)changed|=!before.Fields.Where(f=>f.Key.StartsWith(field.Name+"[",StringComparison.Ordinal)).Select(f=>(f.Key,f.Value)).SequenceEqual(after.Fields.Where(f=>f.Key.StartsWith(field.Name+"[",StringComparison.Ordinal)).Select(f=>(f.Key,f.Value)));
            if(!changed)continue;
            using var stream=new MemoryStream();using var writer=new BinaryWriter(stream,Encoding.UTF8,true);
            string value=after.Get(field.Name)??throw new InvalidDataException(L.T("core.binaryfield"));
            try
            {
                if(element!=0)
                {
                    int count=int.Parse(value,Invariant);if(count<0)throw new InvalidDataException(L.T("core.binaryfield"));
                    if(element is 0x05 or 0x31&&count!=int.Parse(before.Get(field.Name)??"-1",Invariant))throw new InvalidDataException(L.T("core.binaryfield"));
                    if(element is 0x39 or 0x3b or 0x3d&&count>5)throw new InvalidDataException(L.T("core.slotmissing"));
                    if(element is not (0x05 or 0x31 or 0x39 or 0x3b or 0x3d or 0x03 or 0x25 or 0x27))throw new InvalidDataException(L.T("core.binaryfield"));
                    writer.Write((uint)count);
                    for(int i=0;i<count;i++)Write(writer,element,after.Get($"{field.Name}[{i}]")??throw new InvalidDataException(L.T("core.slotmissing")));
                }
                else if(field.Type is 0x01 or 0x03 or 0x05 or 0x25 or 0x27 or 0x2f or 0x31 or 0x33 or 0x35)Write(writer,field.Type,value);
                else throw new InvalidDataException(L.T("core.binaryfield"));
            }
            catch(Exception e) when(e is FormatException or OverflowException){throw new InvalidDataException(L.T("core.binaryfit"),e);}
            patches.Add((field,stream.ToArray()));
        }
        using var output=new MemoryStream();int offset=0;
        foreach(var (field,value) in patches.OrderBy(p=>p.Field.Start))
        {
            output.Write(bytes.AsSpan(offset,field.Start-offset));output.Write(value);offset=field.Start+field.Length;
        }
        output.Write(bytes.AsSpan(offset));var result=output.ToArray();
        int oldOffset=0,newOffset=0;
        foreach(var (field,value) in patches.OrderBy(p=>p.Field.Start))
        {
            int length=field.Start-oldOffset;
            if(!bytes.AsSpan(oldOffset,length).SequenceEqual(result.AsSpan(newOffset,length)))throw new InvalidDataException(L.T("core.outputmismatch"));
            oldOffset=field.Start+field.Length;newOffset+=length+value.Length;
        }
        if(!bytes.AsSpan(oldOffset).SequenceEqual(result.AsSpan(newOffset)))throw new InvalidDataException(L.T("core.outputmismatch"));
        return result;
    }
}
