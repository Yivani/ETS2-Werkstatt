using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Werkstatt;

public sealed record SiiField(string Key, string Value, int Start, int Length);
public sealed record SiiUnit(string Type, string Id, int Start, int Length, int BodyStart, int BodyEnd, List<SiiField> Fields)
{
    Dictionary<string,SiiField>? lookup;
    Dictionary<string,SiiField> Lookup=>lookup??=BuildLookup();
    Dictionary<string,SiiField> BuildLookup(){var map=new Dictionary<string,SiiField>(Fields.Count,StringComparer.Ordinal);foreach(var f in Fields)map.TryAdd(f.Key,f);return map;}
    public string? Get(string key) => Lookup.TryGetValue(key,out var field)?field.Value:null;
    public long Number(string key) => long.TryParse(Get(key),NumberStyles.Integer,CultureInfo.InvariantCulture,out var n) ? n : 0;
}

public sealed class SiiDocument
{
    static readonly Regex Header = new(@"\G\s*(?<type>[a-zA-Z_]\w*)\s*:\s*(?<id>[^\s{}]+)\s*\{", RegexOptions.Compiled);
    static readonly Regex Field = new(@"(?m)^[ \t]*(?<key>[a-zA-Z_]\w*(?:\[\d*\])?)[ \t]*:[ \t]*(?<value>[^\r\n]*)", RegexOptions.Compiled);
    public string Text { get; private set; }
    public List<SiiUnit> Units { get; private set; } = [];
    Dictionary<string, SiiUnit> byId = [];
    public SiiDocument(string text) { Text = text.TrimStart('\uFEFF'); Parse(); }
    SiiDocument(SiiDocument source)
    {
        Text=source.Text;Units=source.Units.Select(u=>new SiiUnit(u.Type,u.Id,u.Start,u.Length,u.BodyStart,u.BodyEnd,new(u.Fields))).ToList();byId=Units.ToDictionary(u=>u.Id,StringComparer.Ordinal);
    }
    internal SiiDocument Clone()=>new(this);
    public SiiUnit Find(string id) => byId.TryGetValue(id, out var u) ? u : throw new InvalidDataException(L.F("core.missingentry",id));
    public SiiUnit? TryFind(string id) => byId.GetValueOrDefault(id);
    public SiiUnit? First(string type) => Units.FirstOrDefault(u => u.Type == type);
    public SiiUnit? Resolve(SiiUnit? unit, string key) => unit?.Get(key) is { } id && byId.TryGetValue(id, out var u) ? u : null;
    public SiiUnit? Economy => First("economy");
    public SiiUnit? Player => Resolve(Economy, "player") ?? First("player");
    public SiiUnit? Bank => Resolve(Economy, "bank") ?? First("bank");
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Text)));

    void Parse()
    {
        var start = Regex.Match(Text, @"\ASiiNunit\s*\{");
        if (!start.Success) throw new InvalidDataException(L.T("core.notsii"));
        var units = new List<SiiUnit>();
        var ids = new Dictionary<string, SiiUnit>(StringComparer.Ordinal);
        int p = start.Length;
        while (true)
        {
            SkipTrivia(ref p);
            if (p >= Text.Length) throw new InvalidDataException(L.F("core.parse","missing closing brace"));
            if (Text[p] == '}') { p++; SkipTrivia(ref p); if (p != Text.Length) throw new InvalidDataException(L.F("core.parse","data after the end of the file")); break; }
            var match = Header.Match(Text, p);
            if (!match.Success) throw new InvalidDataException(L.F("core.parse",$"invalid entry at character {p}"));
            int body = match.Index + match.Length;
            p = body;
            bool quoted = false, escape = false, comment = false;
            for (; p < Text.Length; p++)
            {
                char c = Text[p];
                if (comment) { if (c == '\n') comment = false; continue; }
                if (escape) { escape = false; continue; }
                if (quoted && c == '\\') { escape = true; continue; }
                if (c == '"') { quoted = !quoted; continue; }
                if (quoted) continue;
                if (c == '#' || (c == '/' && p + 1 < Text.Length && Text[p + 1] == '/')) { comment = true; continue; }
                if (c == '{') throw new InvalidDataException(L.F("core.parse","nested entries"));
                if (c == '}') break;
            }
            if (p >= Text.Length || quoted) throw new InvalidDataException(L.F("core.parse","unclosed brace or string"));
            var content = Text[body..p];
            var fields = new List<SiiField>();
            int last = 0;
            foreach (Match f in Field.Matches(content))
            {
                CheckTrivia(content[last..f.Index]);
                var v = f.Groups["value"];
                var value = StripComment(v.Value).TrimEnd();
                ValidateValue(value);
                fields.Add(new(f.Groups["key"].Value, value, body + v.Index, value.Length));
                last = f.Index + f.Length;
            }
            CheckTrivia(content[last..]);
            var unit = new SiiUnit(match.Groups["type"].Value, match.Groups["id"].Value, match.Index, p + 1 - match.Index, body, p, fields);
            if (!ids.TryAdd(unit.Id, unit)) throw new InvalidDataException(L.F("core.parse",$"duplicate entry id {unit.Id}"));
            var seen = new HashSet<string>(fields.Count, StringComparer.Ordinal);
            if (fields.Any(f => !f.Key.EndsWith("[]", StringComparison.Ordinal) && !seen.Add(f.Key))) throw new InvalidDataException(L.F("core.parse",$"duplicate field in {unit.Id}"));
            units.Add(unit); p++;
        }
        if (units.Count == 0) throw new InvalidDataException(L.F("core.parse","no entries"));
        Units = units; byId = ids;
    }
    static string StripComment(string s)
    {
        bool quote = false, escape = false;
        for (int i = 0; i < s.Length; i++)
        {
            if (escape) { escape = false; continue; }
            if (quote && s[i] == '\\') { escape = true; continue; }
            if (s[i] == '"') quote = !quote;
            if (!quote && (s[i] == '#' || (s[i] == '/' && i+1 < s.Length && s[i+1] == '/'))) return s[..i];
        }
        return s;
    }
    static void CheckTrivia(string s)
    {
        if(string.IsNullOrWhiteSpace(s))return;
        if (s.Split('\n').Any(l => StripComment(l).Trim().Length > 0)) throw new InvalidDataException(L.T("core.unsupportedline"));
    }
    void SkipTrivia(ref int p)
    {
        while (p < Text.Length)
        {
            if (char.IsWhiteSpace(Text[p])) { p++; continue; }
            if (Text[p] == '#' || (Text[p] == '/' && p+1 < Text.Length && Text[p+1] == '/')) { while (p < Text.Length && Text[p] != '\n') p++; continue; }
            break;
        }
    }
    public static void ValidateValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(['\r', '\n', '\0']) >= 0) throw new InvalidDataException(L.T("core.badvalue"));
        bool quote = false, escape = false; int paren = 0;
        for (int i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (escape) { escape = false; continue; }
            if (quote && c == '\\') { escape = true; continue; }
            if (c == '"') quote = !quote;
            if (quote) continue;
            if (c is '{' or '}' or '#' || (c == '/' && i+1 < value.Length && value[i+1] == '/')) throw new InvalidDataException(L.T("core.badvalue"));
            if (c == '(') paren++; if (c == ')' && --paren < 0) throw new InvalidDataException(L.T("core.badvalue"));
        }
        if (quote || paren != 0) throw new InvalidDataException(L.T("core.badvalue"));
    }
    public void Set(string id, string key, string value)
    {
        SetMany([(id,key,value)]);
    }
    public void SetMany(IEnumerable<(string Id,string Key,string Value)> values)
    {
        var edits=new List<(int Start,int Length,string Value)>();
        foreach(var (id,key,value) in values)
        {
            ValidateValue(value);
            var f=Find(id).Fields.SingleOrDefault(f=>f.Key==key)??throw new InvalidDataException(L.F("core.missingfield",key));
            if(f.Value!=value)edits.Add((f.Start,f.Length,value));
        }
        if(edits.Count==0)return;
        if(edits.Select(e=>e.Start).Distinct().Count()!=edits.Count)throw new InvalidDataException(L.T("core.doubleedit"));
        Apply(edits.OrderBy(e=>e.Start).ToList());
    }
    void Replace(int start, int length, string value)
    {
        var next = new SiiDocument(Text[..start] + value + Text[(start+length)..]);
        Text = next.Text; Units = next.Units; byId = next.byId;
    }
    public void SetArray(string id, string key, IReadOnlyList<string> values)
    {
        foreach (var v in values) ValidateValue(v);
        var u = Find(id); var count = u.Fields.SingleOrDefault(f => f.Key == key) ?? throw new InvalidDataException(L.F("core.arrayedit",key));
        var related = u.Fields.Where(f => f.Key.StartsWith(key + "[", StringComparison.Ordinal)).ToList();
        string body = Text[u.BodyStart..u.BodyEnd];
        foreach (var f in related.OrderByDescending(f=>f.Start))
        {
            int a = Text.LastIndexOf('\n', f.Start) + 1;
            int b = Text.IndexOf('\n', f.Start); if (b < 0) b = f.Start+f.Length; else b++;
            body = body.Remove(a-u.BodyStart, b-a);
        }
        string nl = Text.Contains("\r\n") ? "\r\n" : "\n";
        string insert = values.Count.ToString(CultureInfo.InvariantCulture) + string.Concat(values.Select((v,i)=>$"{nl} {key}[{i}]: {v}"));
        var pos = count.Start - u.BodyStart;
        body = body[..pos] + insert + body[(pos+count.Length)..];
        Replace(u.BodyStart, u.BodyEnd-u.BodyStart, body);
    }
    /// <summary>Replaces several contiguous arrays and single values with one reparse; large bulk edits stay fast.</summary>
    public void SetArrays(IEnumerable<(string Id,string Key,IReadOnlyList<string> Values)> arrays,IEnumerable<(string Id,string Key,string Value)> values)
    {
        string nl=Text.Contains("\r\n")?"\r\n":"\n";var edits=new List<(int Start,int Length,string Value)>();
        foreach(var (id,key,list) in arrays)
        {
            foreach(var v in list)ValidateValue(v);
            var u=Find(id);var count=u.Fields.SingleOrDefault(f=>f.Key==key)??throw new InvalidDataException(L.F("core.arrayedit",key));
            var related=u.Fields.Where(f=>f.Key.StartsWith(key+"[",StringComparison.Ordinal)).OrderBy(f=>f.Start).ToList();
            int end=related.Count==0?count.Start+count.Length:related[^1].Start+related[^1].Length;
            if(related.Any(f=>f.Start<count.Start)||Text[count.Start..end].Split('\n').Length!=related.Count+1)throw new InvalidDataException(L.F("core.arrayedit",key));
            edits.Add((count.Start,end-count.Start,list.Count.ToString(CultureInfo.InvariantCulture)+string.Concat(list.Select((v,i)=>$"{nl} {key}[{i}]: {v}"))));
        }
        foreach(var (id,key,value) in values)
        {
            ValidateValue(value);var f=Find(id).Fields.SingleOrDefault(f=>f.Key==key)??throw new InvalidDataException(L.F("core.missingfield",key));
            if(f.Value!=value)edits.Add((f.Start,f.Length,value));
        }
        if(edits.Count==0)return;
        var ordered=edits.OrderBy(e=>e.Start).ToList();
        for(int i=1;i<ordered.Count;i++)if(ordered[i].Start<ordered[i-1].Start+ordered[i-1].Length)throw new InvalidDataException(L.T("core.doubleedit"));
        Apply(ordered);
    }
    void Apply(List<(int Start,int Length,string Value)> ordered)
    {
        var text=new StringBuilder(Text.Length+ordered.Sum(e=>e.Value.Length));int at=0;
        foreach(var edit in ordered){text.Append(Text,at,edit.Start-at).Append(edit.Value);at=edit.Start+edit.Length;}
        var next=new SiiDocument(text.Append(Text,at,Text.Length-at).ToString());Text=next.Text;Units=next.Units;byId=next.byId;
    }
    public HashSet<string> MissingReferences() => Units.SelectMany(u=>u.Fields).Where(f=>Regex.IsMatch(f.Value,@"^_nameless\.[a-zA-Z0-9_.]+$")).Select(f=>f.Value).Where(v=>!byId.ContainsKey(v)).ToHashSet();
    public List<string> ValidateAgainst(SiiDocument original)
    {
        var errors = new List<string>();
        HashSet<string>? existingMissing=null;
        foreach (var u in Units)
        {
            original.byId.TryGetValue(u.Id, out var old);
            if(old!=null&&Text.AsSpan(u.Start,u.Length).SequenceEqual(original.Text.AsSpan(old.Start,old.Length)))continue;
            foreach(var field in u.Fields.Where(f=>f.Value!=old?.Get(f.Key)))
            {
                if(Regex.IsMatch(field.Value,@"^_nameless\.[a-zA-Z0-9_.]+$")&&!byId.ContainsKey(field.Value)&&!(existingMissing??=original.MissingReferences()).Contains(field.Value))errors.Add(L.F("core.missingref",field.Value));
            }
            var before=ArrayFields(old);var after=ArrayFields(u);
            foreach(string name in before.Keys.Concat(after.Keys).Distinct())
            {
                var array=after.GetValueOrDefault(name,[]);var prior=before.GetValueOrDefault(name,[]);
                if(old!=null&&u.Get(name)==old.Get(name)&&array.Select(f=>(f.Key,f.Value)).SequenceEqual(prior.Select(f=>(f.Key,f.Value))))continue;
                if(!int.TryParse(u.Get(name),NumberStyles.Integer,CultureInfo.InvariantCulture,out int n)||n<0||n!=array.Count||!array.Select(f=>int.Parse(f.Key.AsSpan(f.Key.IndexOf('[')+1,f.Key.Length-f.Key.IndexOf('[')-2),CultureInfo.InvariantCulture)).Order().SequenceEqual(Enumerable.Range(0,n)))errors.Add(L.F("core.badarray",u.Id,name));
            }
        }
        if(original.byId.Keys.Any(id=>!byId.ContainsKey(id)))
        {
            var missing=MissingReferences();missing.ExceptWith(original.MissingReferences());errors.AddRange(missing.Select(id=>L.F("core.missingref",id)));
        }
        return errors;
    }
    static Dictionary<string,List<SiiField>> ArrayFields(SiiUnit? unit)=>unit?.Fields.Where(f=>f.Key.IndexOf('[') is int i&&i>=0&&i+1<f.Key.Length-1).GroupBy(f=>f.Key[..f.Key.IndexOf('[')]).ToDictionary(g=>g.Key,g=>g.ToList())??[];
    public static double Float(string? value) => value?.StartsWith('&') == true ? BitConverter.Int32BitsToSingle(unchecked((int)uint.Parse(value[1..], NumberStyles.HexNumber))) : double.Parse(value ?? "0", CultureInfo.InvariantCulture);
    public static string FloatValue(double value) => "&" + BitConverter.SingleToUInt32Bits((float)value).ToString("x8");
    public static string Display(string? value)
    {
        if (value == null) return "—";
        if (!value.StartsWith('"')) return value;
        var raw = value.Trim('"');
        var bytes = new List<byte>();
        for (int i=0;i<raw.Length;i++)
        {
            if (raw[i]=='\\' && i+3<raw.Length && raw[i+1]=='x' && byte.TryParse(raw.AsSpan(i+2,2),NumberStyles.HexNumber,null,out var b)) {bytes.Add(b);i+=3;}
            else if (raw[i]=='\\' && i+1<raw.Length) bytes.AddRange(Encoding.UTF8.GetBytes(raw[++i].ToString()));
            else bytes.AddRange(Encoding.UTF8.GetBytes(raw[i].ToString()));
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
