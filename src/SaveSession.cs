using System.Diagnostics;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Werkstatt;

public sealed record Change(string Unit, string Field, string Before, string After);
public sealed record BackupInfo(string Folder, string Source, string Hash, DateTime Created, string FileName)
{ public DateTime LocalCreated=>Created.ToLocalTime(); }
public sealed class SaveSession
{
    internal static readonly byte[] SiiKey=Convert.FromHexString("2a5fcb1791d22fb60245b3d8369ed0b2c27371563fbf1f3c9edf6b11825a5d0a");
    byte[] originalBytes;
    readonly LinkedList<string> undo=[];
    readonly Stack<string> redo=[];
    List<Change>? changes;
    string? integrityWarning;
    public bool CanUndo=>undo.Count>0;
    public bool CanRedo=>redo.Count>0;
    public int ChangeCount=>GetChanges().Count;
    public static string DataRoot {get;internal set;}=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ETS2Werkstatt");
    public static string BackupRoot => Path.Combine(DataRoot,"Backups");
    public string FilePath { get; }
    public string OriginalHash { get; private set; }
    public SiiDocument Original { get; private set; }
    public SiiDocument Document { get; private set; }
    public string? IntegrityWarning=>integrityWarning;
    public bool Dirty => Document.Text != Original.Text;
    public bool NeedsFormatConversion=>originalBytes.AsSpan().StartsWith("BSII"u8)&&Original.Economy!=null&&Original.Bank!=null;
    static string? LegacyCorruption(SiiDocument doc)=>doc.Units.Any(u=>u.Type=="vehicle_cargo_accessory"&&new[]{"lashing_method","lashing_gear_type"}.Any(k=>SiiDocument.Display(u.Get(k)).StartsWith("area_cont_",StringComparison.Ordinal)))
        ?L.T("core.legacy"):null;
    SaveSession(string path, byte[] bytes, string text)
    { FilePath = Path.GetFullPath(path);originalBytes=bytes; OriginalHash = Hash(bytes); Original = new(text); Document = Original.Clone();integrityWarning=LegacyCorruption(Original); }
    public static string Decode(string path)
    {
        if(new FileInfo(path).Length>128*1024*1024)throw new InvalidDataException(L.T("core.toolarge"));
        return DecodeBytes(File.ReadAllBytes(path));
    }
    internal static string DecodeBytes(byte[] data)
    {
        data=Unwrap(data);
        if(data.AsSpan().StartsWith("BSII"u8))return new BinarySii(data).Text;
        if(!Encoding.UTF8.GetString(data.AsSpan(0,Math.Min(16,data.Length))).TrimStart('\uFEFF').StartsWith("SiiNunit"))
            throw new InvalidDataException(L.T("core.format"));
        try{return new UTF8Encoding(false,true).GetString(data);}
        catch(DecoderFallbackException e){throw new InvalidDataException(L.T("core.format"),e);}
    }
    internal static byte[] Unwrap(byte[] data)
    {
        if (data.Length > 128 * 1024 * 1024) throw new InvalidDataException(L.T("core.toolarge"));
        if(data.AsSpan().StartsWith("ScsC"u8))
        {
            if(data.Length<72 || (data.Length-56)%16!=0)throw new InvalidDataException(L.T("core.damaged"));
            uint size=BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(52,4));
            if(size<4 || size>128*1024*1024)throw new InvalidDataException(L.T("core.size"));
            try
            {
                using var aes=Aes.Create();aes.Key=SiiKey;
                using var compressed=new MemoryStream(aes.DecryptCbc(data.AsSpan(56),data.AsSpan(36,16),PaddingMode.None));
                using var zlib=new ZLibStream(compressed,CompressionMode.Decompress);
                var decoded=new byte[size];zlib.ReadExactly(decoded);
                if(zlib.ReadByte()!=-1)throw new InvalidDataException(L.T("core.damaged"));
                data=decoded;
            }
            catch(Exception e) when(e is CryptographicException or IOException)
            {throw new InvalidDataException(L.T("core.damaged"),e);}
        }
        return data;
    }
    public static SaveSession Open(string path)
    {
        if(new FileInfo(path).Length>128*1024*1024)throw new InvalidDataException(L.T("core.toolarge"));
        var bytes = File.ReadAllBytes(path); var text = DecodeBytes(bytes);
        if (Hash(File.ReadAllBytes(path))!=Hash(bytes)) throw new IOException(L.T("core.changedread"));
        return new(path,bytes,text);
    }
    public void Edit(Action<SiiDocument> edit)
    {
        var candidate = Document.Clone(); edit(candidate);
        var errors = candidate.ValidateAgainst(Original);
        if (errors.Count>0) throw new InvalidDataException(string.Join("\n",errors));
        if(candidate.Text==Document.Text)return;
        Remember(Document.Text);redo.Clear();Document = candidate;changes=null;
    }
    void Remember(string text)
    {
        undo.AddLast(text);
        long size=undo.Sum(t=>(long)t.Length);while(undo.Count>1&&(undo.Count>12||size>40_000_000)){size-=undo.First!.Value.Length;undo.RemoveFirst();}
    }
    public bool Undo()
    {
        if(undo.Last==null)return false;
        var restored=new SiiDocument(undo.Last.Value);redo.Push(Document.Text);undo.RemoveLast();Document=restored;changes=null;return true;
    }
    public bool Redo()
    {
        if(redo.Count==0)return false;
        var restored=new SiiDocument(redo.Peek());Remember(Document.Text);redo.Pop();Document=restored;changes=null;return true;
    }
    public void Reset(){Document=Original.Clone();undo.Clear();redo.Clear();changes=null;}
    public List<Change> Changes()=>new(GetChanges());
    List<Change> GetChanges()
    {
        if(this.changes!=null)return this.changes;
        var changes = new List<Change>();
        var old = Original.Units.ToDictionary(u=>u.Id,StringComparer.Ordinal);
        foreach (var u in Document.Units)
        {
            if (!old.TryGetValue(u.Id,out var prior)) { changes.Add(new(u.Id,EntryField,"—",u.Type)); continue; }
            if (Document.Text.AsSpan(u.Start,u.Length).SequenceEqual(Original.Text.AsSpan(prior.Start,prior.Length))) continue;
            foreach (var f in u.Fields)
                if (f.Value!=prior.Get(f.Key)) changes.Add(new(u.Id,f.Key,prior.Get(f.Key)??"—",f.Value));
            foreach (var f in prior.Fields.Where(f=>u.Get(f.Key)==null)) changes.Add(new(u.Id,f.Key,f.Value,Removed));
        }
        var currentIds=Document.Units.Select(u=>u.Id).ToHashSet();
        foreach(var u in Original.Units.Where(u=>!currentIds.Contains(u.Id))) changes.Add(new(u.Id,EntryField,u.Type,Removed));
        if (Dirty && changes.Count==0) changes.Add(new(LayoutUnit,"","",""));
        return this.changes=changes;
    }
    internal const string FormatUnit="$format",LayoutUnit="$layout",EntryField="$entry",Removed="$removed";
    internal static Func<bool> GameProbe {get;set;}=()=>{var processes=Process.GetProcessesByName("eurotrucks2");try{return processes.Length>0;}finally{foreach(var p in processes)p.Dispose();}};
    public static bool GameRunning=>GameProbe();
    public string Save()
    {
        SteamCloud.Refuse(FilePath);
        if (!Dirty&&!NeedsFormatConversion) throw new InvalidOperationException(L.T("status.nochanges"));
        if (GameRunning) throw new IOException(L.T("core.running"));
        if(!Path.GetFileName(FilePath).Equals("game.sii",StringComparison.OrdinalIgnoreCase)||Document.Economy==null||Document.Bank==null)
            throw new InvalidDataException(L.T("core.onlygame"));
        return CommitEdits(true);
    }
    internal string CommitEdits(bool requireClosedGame=false)
    {
        SteamCloud.Refuse(FilePath);var bytes=BuildOutput();return SaveBytes(bytes,DecodeBytes(bytes),requireClosedGame,Document);
    }
    byte[] BuildOutput()
    {
        ValidateEdits();var payload=Unwrap(originalBytes);byte[] bytes;
        if(payload.AsSpan().StartsWith("BSII"u8))bytes=CompatibleBytes(new BinarySii(payload).Patch(Original,Document));
        else bytes=(payload.AsSpan().StartsWith(new byte[]{239,187,191})?new byte[]{239,187,191}:[]).Concat(new UTF8Encoding(false).GetBytes(Document.Text)).ToArray();
        if(!SameValues(Document,new SiiDocument(DecodeBytes(bytes))))throw new InvalidDataException(L.T("core.outputmismatch"));
        return bytes;
    }
    public string ExportCopy(string folder)
    {
        if(!Path.GetFileName(FilePath).Equals("game.sii",StringComparison.OrdinalIgnoreCase)||Document.Economy==null||Document.Bank==null)throw new InvalidDataException(L.T("core.onlygame"));
        folder=Path.GetFullPath(folder);SteamCloud.Refuse(Path.Combine(folder,"game.sii"));if(Directory.Exists(folder)||File.Exists(folder))throw new IOException(L.T("core.copyexists"));
        byte[] output=BuildOutput();string sourceFolder=Path.GetDirectoryName(FilePath)!;
        string infoPath=Path.Combine(sourceFolder,"info.sii");if(!File.Exists(infoPath))throw new InvalidDataException(L.T("core.noinfo"));
        var companions=new[]{"info.sii","preview.tga"}.Where(n=>File.Exists(Path.Combine(sourceFolder,n))).ToDictionary(n=>n,n=>File.ReadAllBytes(Path.Combine(sourceFolder,n)));
        var infoPayload=Unwrap(companions["info.sii"]);
        var metadata=new SiiDocument(infoPayload.AsSpan().StartsWith("BSII"u8)?new BinarySii(infoPayload).CompatibleText:DecodeBytes(infoPayload));var container=metadata.First("save_container")??throw new InvalidDataException(L.T("core.badinfo"));
        var summary=new Dictionary<string,string>{{"name",$"\"{L.T("copy.savename")} {DateTime.Now.ToString("yyyy-MM-dd HH:mm",System.Globalization.CultureInfo.InvariantCulture)}\""},{"file_time",DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)}};
        foreach(var (target,value) in new[]{("info_money_account",Document.Bank.Get("money_account")),("info_players_experience",Document.Economy.Get("experience_points")),("time",Document.Economy.Get("game_time"))})if(value!=null)summary[target]=value;
        metadata.SetMany(summary.Where(p=>container.Get(p.Key)!=null).Select(p=>(container.Id,p.Key,p.Value)));
        void VerifySource()
        {
            if(Hash(File.ReadAllBytes(FilePath))!=OriginalHash||companions.Any(p=>Hash(File.ReadAllBytes(Path.Combine(sourceFolder,p.Key)))!=Hash(p.Value)))throw new IOException(L.T("core.changedexport"));
        }
        var files=new Dictionary<string,byte[]>{{"game.sii",output},{"info.sii",Encoding.UTF8.GetBytes(metadata.Text)}};
        if(companions.TryGetValue("preview.tga",out var preview))files["preview.tga"]=preview;
        VerifySource();Directory.CreateDirectory(folder);var created=new List<string>();
        void WriteNew(string name,byte[] bytes){string path=Path.Combine(folder,name);using var f=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);created.Add(path);f.Write(bytes);f.Flush(true);}
        try
        {
            foreach(var file in files)WriteNew(file.Key,file.Value);
            VerifySource();if(files.Any(file=>Hash(File.ReadAllBytes(Path.Combine(folder,file.Key)))!=Hash(file.Value)))throw new IOException(L.T("core.copyunconfirmed"));
            return folder;
        }
        catch
        {
            foreach(string path in created)File.Delete(path);
            if(!Directory.EnumerateFileSystemEntries(folder).Any())Directory.Delete(folder);throw;
        }
    }
    internal static void ConvertCopy(string source,string destination)
    {
        if(Path.GetFullPath(source).Equals(Path.GetFullPath(destination),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("The repair must be written to a new copy.");
        SteamCloud.Refuse(destination);
        var session=Open(source);if(session.IntegrityWarning is {} warning)throw new InvalidDataException(warning);
        byte[] output=CompatibleBytes(session.originalBytes);
        if(!SameValues(session.Original,new SiiDocument(DecodeBytes(output))))throw new InvalidDataException("The repair would change values.");
        using var file=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None);file.Write(output);file.Flush(true);
    }
    internal static byte[] CompatibleBytes(byte[] bytes)
    {
        var payload=Unwrap(bytes);
        return payload.AsSpan().StartsWith("BSII"u8)&&bytes.AsSpan().StartsWith("BSII"u8)?Encoding.UTF8.GetBytes(new BinarySii(payload).CompatibleText):bytes;
    }
    void ValidateEdits()
    {
        if(IntegrityWarning is {} warning)throw new InvalidDataException(warning);
        if(Original.Units.Count!=Document.Units.Count)throw new InvalidDataException(L.T("core.structure"));
        foreach(var unit in Document.Units)if(Original.Find(unit.Id).Type!=unit.Type)throw new InvalidDataException(L.T("core.structure"));
        HashSet<string>? owned=null,hired=null,visited=null;bool? visits=null;var unlocks=new Dictionary<string,bool>();
        foreach(var change in Changes())
        {
            if(change.Unit==LayoutUnit)continue;
            var unit=Document.Find(change.Unit);string key=change.Field.Split('[')[0];bool allowed=false;
            if(unit.Id==Original.Bank?.Id&&key=="money_account")allowed=long.TryParse(unit.Get(key),out _);
            if(unit.Id==Original.Economy?.Id)
            {
                long max=key switch{"experience_points" or "game_time"=>int.MaxValue,_=>SkillMax(key)};
                allowed=max>=0&&long.TryParse(unit.Get(key),out long value)&&value>=0&&value<=max;
                if(key is "visited_cities" or "visited_cities_count")allowed=visits??=VisitsAppended();
                if(UnlockKeys.Contains(key))allowed=unlocks.TryGetValue(key,out bool ok)?ok:unlocks[key]=UnlocksAppended(key);
                if(key=="red_light_fine_count")allowed=change.Field==key&&unit.Get(key)=="0";
            }
            if(unit.Type=="police_offence_log"&&key is "offence_total_counts" or "offence_total_fines"&&unit.Id==OffenceLog(Document)?.Id)
                allowed=change.Field!=key&&unit.Get(change.Field)=="0"&&unit.Get(key)==Original.Find(unit.Id).Get(key);
            if(unit.Type=="bus_stop"&&change.Field=="discovered")allowed=Original.Find(unit.Id).Get(key)=="false"&&unit.Get(key)=="true";
            if(unit.Type is "vehicle" or "trailer"&&OdometerFields.Contains(change.Field)&&(owned??=OwnedVehicleIds(Document)).Contains(unit.Id))
                allowed=change.Field.EndsWith("_float_part",StringComparison.Ordinal)?unit.Get(change.Field)==Zero
                    :ValidOdometer(unit.Get(change.Field))&&(unit.Get("integrity_odometer")==null||unit.Get("integrity_odometer")==unit.Get("odometer"));
            if(unit.Type=="company"&&change.Field=="discovered")
                allowed=Original.Find(unit.Id).Get(key)=="false"&&unit.Get(key)=="true"&&CityOf(unit.Id) is {} city&&(visited??=new(ArrayValues(Document.Economy,"visited_cities")??[])).Contains(city);
            if(unit.Type=="driver_ai"&&change.Field==key&&(hired??=new(HiredDriverIds(Document))).Contains(unit.Id))
                allowed=SkillMax(key) is >=0 and var max&&long.TryParse(unit.Get(key),out long value)&&value>=0&&value<=max;
            if(unit.Id==Original.Player?.Id&&key=="hq_city")allowed=Document.Units.Any(g=>g.Type=="garage"&&g.Id=="garage."+unit.Get(key)&&g.Number("status")>0);
            if(unit.Type is "vehicle" or "trailer"&&ServiceFields.Contains(key)&&(owned??=OwnedVehicleIds(Document)).Contains(unit.Id))
                allowed=FloatValue(unit.Get(change.Field)) is {} f&&f>=0&&f<=1&&(key!="wheels_wear"&&key!="wheels_wear_unfixable"||change.Field.Contains('['));
            if(unit.Type=="trailer"&&change.Field=="license_plate")throw new InvalidDataException(L.T("core.plate.trailer"));
            if(unit.Type=="vehicle"&&change.Field=="license_plate"&&(owned??=OwnedVehicleIds(Document)).Contains(unit.Id))
                allowed=PlateCountry(Original.Find(unit.Id).Get(key)) is {} country&&unit.Get(key) is {} plate&&plate.StartsWith('"')&&plate.EndsWith("|"+country+"\"",StringComparison.Ordinal)&&ValidPlate(plate[1..^(country.Length+2)]);
            if(unit.Type=="garage"&&key is "status" or "vehicles" or "drivers")
            {
                int slots=GarageSlots(unit.Number("status"));
                allowed=unit.Number("vehicles")==slots&&unit.Number("drivers")==slots;
            }
            if(!allowed)throw new InvalidDataException(L.F("core.protected",$"{unit.Id} / {change.Field}"));
        }
    }
    static bool SameValues(SiiDocument a,SiiDocument b)=>a.Units.Count==b.Units.Count&&a.Units.All(u=>
        b.Find(u.Id) is {} v&&u.Type==v.Type&&u.Fields.OrderBy(f=>f.Key,StringComparer.Ordinal).Select(f=>(f.Key,f.Value)).SequenceEqual(v.Fields.OrderBy(f=>f.Key,StringComparer.Ordinal).Select(f=>(f.Key,f.Value))));
    internal string SaveBytes(byte[] bytes,string text,bool requireClosedGame=false,SiiDocument? expected=null)
    {
        var candidate=new SiiDocument(text);
        if(LegacyCorruption(candidate) is {} warning)throw new InvalidDataException(warning);
        if(expected!=null&&!SameValues(expected,candidate))
        {
            var differences=expected.Units.SelectMany(u=>u.Fields.Where(f=>candidate.Find(u.Id).Get(f.Key)!=f.Value).Select(f=>$"{u.Id}/{f.Key}: {f.Value} → {candidate.Find(u.Id).Get(f.Key)}"));
            throw new InvalidDataException(L.F("core.verifyfailed",string.Join("; ",differences.Take(3))));
        }
        var errors = candidate.ValidateAgainst(Original);
        if(errors.Count>0) throw new InvalidDataException(string.Join("\n",errors));
        string folder=Persist(FilePath,OriginalHash,bytes,candidate,requireClosedGame);
        this.originalBytes=bytes;OriginalHash=Hash(bytes);Original=candidate;Document=Original.Clone();changes=null;undo.Clear();redo.Clear();integrityWarning=null;
        return folder;
    }
    static string Persist(string path,string expectedHash,byte[] bytes,SiiDocument candidate,bool requireClosedGame)
    {
        SteamCloud.Refuse(path);
        using var guard = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read | FileShare.Delete);
        var originalBytes = new byte[guard.Length]; guard.ReadExactly(originalBytes);
        if (Hash(originalBytes)!=expectedHash) throw new IOException(L.T("core.changedsince"));
        string folder = Path.Combine(BackupRoot,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff",System.Globalization.CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var info = new BackupInfo(folder,path,expectedHash,DateTime.UtcNow,Path.GetFileName(path));
        string backupFile=Path.Combine(folder,info.FileName);
        using(var backup=new FileStream(backupFile,FileMode.CreateNew,FileAccess.Write,FileShare.Read)){backup.Write(originalBytes);backup.Flush(true);}
        if(Hash(File.ReadAllBytes(backupFile))!=expectedHash)throw new IOException(L.T("core.backupunconfirmed"));
        foreach(var name in new[]{"info.sii","preview.tga"}.Where(n=>n!=info.FileName))
        {
            string companion=Path.Combine(Path.GetDirectoryName(path)!,name);
            if(File.Exists(companion)) File.Copy(companion,Path.Combine(folder,name));
        }
        SettingsBackup.Capture(path,folder);
        File.WriteAllText(Path.Combine(folder,"backup.json"),JsonSerializer.Serialize(info,new JsonSerializerOptions{WriteIndented=true}));
        if(requireClosedGame&&GameRunning)throw new IOException(L.T("core.started"));
        if(Hash(File.ReadAllBytes(path))!=expectedHash)throw new IOException(L.T("core.replacedduringbackup"));
        AtomicReplace(path,bytes,expectedHash);
        try
        {
            var written=File.ReadAllBytes(path);
            if(Hash(written)!=Hash(bytes) || new SiiDocument(DecodeBytes(written)).Fingerprint!=candidate.Fingerprint) throw new IOException("Re-check of the written file failed.");
        }
        catch
        {
            if(Hash(File.ReadAllBytes(path))!=Hash(bytes))throw new IOException(L.F("core.externalafterwrite",folder));
            AtomicReplace(path,originalBytes,Hash(bytes));throw new IOException(L.F("core.recheckrestored",folder));
        }
        return folder;
    }
    internal static void AtomicReplace(string path,byte[] bytes,string? expectedHash=null,int rollbackDepth=0)
    {
        string temp=path+".werkstatt-"+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var f=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){f.Write(bytes);f.Flush(true);}
            if(expectedHash==null)File.Replace(temp,path,null);
            else
            {
                string previous=temp+".previous";File.Replace(temp,path,previous);
                var replaced=File.ReadAllBytes(previous);
                if(Hash(replaced)!=expectedHash)
                {
                    if(rollbackDepth>=2)throw new IOException(L.F("core.concurrent",previous));
                    try{AtomicReplace(path,replaced,Hash(bytes),rollbackDepth+1);}
                    catch(Exception e){throw new IOException(L.F("core.externalkept",previous),e);}
                    File.Delete(previous);throw new IOException(L.T("core.concurrentrestored"));
                }
                File.Delete(previous);
            }
        }
        finally {if(File.Exists(temp)) File.Delete(temp);}
    }
    public static List<BackupInfo> Backups()
    {
        if(!Directory.Exists(BackupRoot)) return [];
        var list=new List<BackupInfo>();
        foreach(var f in Directory.EnumerateDirectories(BackupRoot).Select(d=>Path.Combine(d,"backup.json")))
        {try {if(JsonSerializer.Deserialize<BackupInfo>(File.ReadAllText(f)) is {} b&&b.Source!=null&&b.Hash!=null&&b.FileName!=null) list.Add(b with {Folder=Path.GetDirectoryName(f)!});}catch(Exception e) when(e is JsonException or IOException or UnauthorizedAccessException){} }
        return list.OrderByDescending(b=>b.Created).ToList();
    }
    public static void Restore(BackupInfo b)
    {
        SteamCloud.Refuse(b.Source);
        if(GameRunning) throw new IOException(L.T("core.running.restore"));
        if(!Path.GetFileName(b.Source).Equals("game.sii",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException(L.T("core.restoreonlygame"));
        var bytes=File.ReadAllBytes(Path.Combine(b.Folder,b.FileName));
        if(Hash(bytes)!=b.Hash) throw new InvalidDataException(L.T("core.backupdamaged"));
        var output=CompatibleBytes(bytes);
        var candidate=new SiiDocument(DecodeBytes(output));if(LegacyCorruption(candidate) is {} warning)throw new InvalidDataException(warning);
        if(new FileInfo(b.Source).Length>128*1024*1024)throw new InvalidDataException(L.T("core.sourcetoolarge"));
        string currentHash=Hash(File.ReadAllBytes(b.Source));if(currentHash==Hash(output))return;
        Persist(b.Source,currentHash,output,candidate,true);
    }
    public static string Hash(byte[] data)=>Convert.ToHexString(SHA256.HashData(data));
    public static int GarageSlots(long status)=>status switch {0=>0,6=>1,2=>3,3=>5,_=>throw new InvalidDataException(L.T("core.garagelevel"))};
    public void Garage(string id,int status)
    {
        Edit(doc=>
        {
            var u=doc.Find(id); int old=GarageSlots(u.Number("status")), next=GarageSlots(status);
            if(status==0 && doc.Player?.Get("hq_city")==id.Replace("garage.","")) throw new InvalidDataException(L.T("core.hqfirst"));
            if(status==0 && u.Number("trailers")>0) throw new InvalidDataException(L.T("core.trailers"));
            if(doc.Resolve(u,"profit_log")?.Type!="profit_log") throw new InvalidDataException(L.T("core.profitlog"));
            var arrays=new List<(string,string,IReadOnlyList<string>)>();
            foreach(string key in new[]{"vehicles","drivers"})
            {
                if(u.Number(key)!=old) throw new InvalidDataException(L.F("core.slotcount",key));
                for(int i=next;i<old;i++) if(u.Get($"{key}[{i}]") is not "null") throw new InvalidDataException(L.F("core.slotbusy",i+1));
                arrays.Add((id,key,Enumerable.Range(0,next).Select(i=>i<old ? u.Get($"{key}[{i}]")??throw new InvalidDataException(L.T("core.slotmissing")):"null").ToList()));
            }
            string level=status.ToString(System.Globalization.CultureInfo.InvariantCulture);
            try{doc.SetArrays(arrays,[(id,"status",level)]);}
            catch(InvalidDataException)
            {
                foreach(var (unit,key,values) in arrays)doc.SetArray(unit,key,values);doc.Set(id,"status",level);
            }
        });
    }
    static readonly HashSet<string> WearFields=["engine_wear","transmission_wear","cabin_wear","chassis_wear","wheels_wear","trailer_body_wear","engine_wear_unfixable","transmission_wear_unfixable","cabin_wear_unfixable","chassis_wear_unfixable","wheels_wear_unfixable","trailer_body_wear_unfixable"];
    static readonly HashSet<string> ServiceFields=[..WearFields,"fuel_relative","cargo_damage"];
    internal const string Zero="&00000000",Full="&3f800000";
    /// <summary>Parses SII float tokens: hex bit patterns (&amp;3f800000) or plain decimals.</summary>
    public static float? FloatValue(string? value)
    {
        if(value==null)return null;
        if(value.Length==9&&value[0]=='&'&&uint.TryParse(value.AsSpan(1),System.Globalization.NumberStyles.HexNumber,System.Globalization.CultureInfo.InvariantCulture,out uint bits))return BitConverter.UInt32BitsToSingle(bits);
        return float.TryParse(value,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out float f)?f:null;
    }
    /// <summary>Trucks and trailers that belong to the player or the player's garages; dealer offers are excluded.</summary>
    public static HashSet<string> OwnedVehicleIds(SiiDocument doc)
    {
        var ids=new HashSet<string>();if(doc.Player is not {} p)return ids;
        IEnumerable<string> Refs(SiiUnit u,string key)=>u.Fields.Where(f=>f.Key==key||f.Key.StartsWith(key+"[",StringComparison.Ordinal)&&f.Key.EndsWith(']')).Select(f=>f.Value).Where(v=>v!="null"&&!int.TryParse(v,out _));
        foreach(var key in new[]{"trucks","assigned_truck","my_truck","trailers","assigned_trailer","my_trailer"})ids.UnionWith(Refs(p,key));
        foreach(var id in Refs(p,"my_vehicles"))if(doc.Units.FirstOrDefault(u=>u.Id==id) is {Type:"player_vehicles"} pv){ids.UnionWith(Refs(pv,"vehicle"));ids.UnionWith(Refs(pv,"trailer"));}
        foreach(var g in doc.Units.Where(u=>u.Type=="garage"&&u.Number("status")>0))ids.UnionWith(Refs(g,"vehicles"));
        var known=doc.Units.Where(u=>u.Type is "vehicle" or "trailer").ToDictionary(u=>u.Id);
        var queue=new Queue<string>(ids.Where(known.ContainsKey));ids.Clear();
        while(queue.Count>0){var id=queue.Dequeue();if(!ids.Add(id))continue;if(known[id].Get("slave_trailer") is {} slave&&known.ContainsKey(slave))queue.Enqueue(slave);}
        return ids;
    }
    public List<SiiUnit> OwnedVehicles()=>OwnedVehicleIds(Document).Select(Document.Find).OrderBy(u=>u.Type=="trailer").ToList();
    /// <summary>Repairs, refuels or clears cargo damage on the given owned units in one undo step.</summary>
    public int Service(IEnumerable<string> ids,bool repair,bool refuel,bool cargo)
    {
        var edits=PlanService(ids,repair,refuel,cargo);if(edits.Count>0)Edit(d=>d.SetMany(edits));return edits.Count;
    }
    /// <summary>The owned units among <paramref name="ids"/> that <see cref="Service"/> would change; nothing is staged.</summary>
    public HashSet<string> ServicePending(IEnumerable<string> ids,bool repair,bool refuel,bool cargo)=>PlanService(ids,repair,refuel,cargo).Select(e=>e.Item1).ToHashSet();
    List<(string,string,string)> PlanService(IEnumerable<string> ids,bool repair,bool refuel,bool cargo)
    {
        var owned=OwnedVehicleIds(Document);var edits=new List<(string,string,string)>();
        foreach(var unit in ids.Where(owned.Contains).Distinct().Select(Document.Find))
            foreach(var field in unit.Fields)
            {
                string key=field.Key.Split('[')[0];bool array=field.Key.Contains('[');
                if(key is "wheels_wear" or "wheels_wear_unfixable"&&!array)continue;
                string? target=repair&&WearFields.Contains(key)?Zero:refuel&&key=="fuel_relative"&&unit.Type=="vehicle"?Full:cargo&&key=="cargo_damage"?Zero:null;
                if(target!=null&&FloatValue(field.Value)!=FloatValue(target))edits.Add((unit.Id,field.Key,target));
            }
        return edits;
    }
    /// <summary>Upgrades owned garages to <paramref name="size"/> (6 tiny, 2 small, 3 large), or also buys every missing garage at that size.
    /// Bigger garages are never shrunk, so no truck or driver loses its slot. <paramref name="only"/> limits the change to these garage ids (for example one country).</summary>
    public int UpgradeGarages(bool buyAll,IReadOnlySet<string>? only=null,int size=3)
    {
        var arrays=new List<(string,string,IReadOnlyList<string>)>();var values=new List<(string,string,string)>();
        int count=PlanGarages(buyAll,only,arrays,values,size);
        if(count>0)Edit(doc=>doc.SetArrays(arrays,values));return count;
    }
    /// <summary>How many garages <see cref="UpgradeGarages"/> would change for the whole map; nothing is staged.</summary>
    public int GaragesPending(bool buyAll,int size=3)=>PlanGarages(buyAll,null,[],[],size);
    int PlanGarages(bool buyAll,IReadOnlySet<string>? only,List<(string,string,IReadOnlyList<string>)> arrays,List<(string,string,string)> values,int size=3)
    {
        int slots=GarageSlots(size);if(slots==0)throw new InvalidDataException(L.T("core.garagelevel"));int count=0;
        foreach(var g in Document.Units.Where(u=>u.Type=="garage"&&(only==null||only.Contains(u.Id))))
        {
            long status=g.Number("status");if(status is not (0 or 2 or 3 or 6)||status==0&&!buyAll)continue;
            int old=GarageSlots(status);if(old>=slots)continue;
            if(Document.Resolve(g,"profit_log")?.Type!="profit_log"||g.Number("vehicles")!=old||g.Number("drivers")!=old)continue;
            foreach(string key in new[]{"vehicles","drivers"})arrays.Add((g.Id,key,Enumerable.Range(0,slots).Select(i=>i<old?g.Get($"{key}[{i}]")??"null":"null").ToList()));
            values.Add((g.Id,"status",size.ToString(System.Globalization.CultureInfo.InvariantCulture)));count++;
        }
        return count;
    }
    /// <summary>TruckersMP adds its own companies, cargo and a garage. Single-player ETS2 misses them, resets the economy and shows "Changes to the game detected".</summary>
    public static bool HasTruckersMpContent(SiiDocument doc)=>doc.Units.Any(u=>u.Id=="garage.truckersmp"||u.Type=="company"&&TruckersMpCompany(u.Id));
    static bool TruckersMpCompany(string id)=>id.EndsWith(".truckersmp",StringComparison.Ordinal)||id.StartsWith("company.volatile.tmp",StringComparison.Ordinal);
    public static readonly string[] SkillKeys=["long_dist","heavy","fragile","urgent","mechanical"];
    static long SkillMax(string key)=>key switch{"adr"=>63,"long_dist" or "heavy" or "fragile" or "urgent" or "mechanical"=>6,_=>-1};
    public sealed record MaxOutResult(bool Money,bool Level,int Skills,int Garages,int Cities,int Dealers,int Recruitments){public bool Any=>Money||Level||Skills>0||Garages>0||Cities>0||Dealers>0||Recruitments>0;}
    /// <summary>Maximum money, maximum level, every skill and ADR class, every garage bought as large, every known city visited and its truck dealer and recruitment agency unlocked, in one undo step.</summary>
    public MaxOutResult MaxOut()
    {
        var arrays=new List<(string,string,IReadOnlyList<string>)>();var values=new List<(string,string,string)>();
        var result=PlanMaxOut(arrays,values);if(result.Any)Edit(doc=>doc.SetArrays(arrays,values));return result;
    }
    /// <summary>What <see cref="MaxOut"/> would change; nothing is staged.</summary>
    public MaxOutResult MaxOutPending()=>PlanMaxOut([],[]);
    MaxOutResult PlanMaxOut(List<(string,string,IReadOnlyList<string>)> arrays,List<(string,string,string)> values)
    {
        bool money=PlanMoney(values),level=PlanLevel(values);int skills=PlanSkills(values);
        int garages=PlanGarages(true,null,arrays,values);int cities=PlanVisits(CitiesOf(Original),arrays,values);
        int dealers=PlanUnlock(UnlockKeys[0],arrays),recruitments=PlanUnlock(UnlockKeys[1],arrays);
        return new(money,level,skills,garages,cities,dealers,recruitments);
    }
    static bool Want(SiiUnit? unit,string key,string value,List<(string,string,string)> values){if(unit?.Get(key) is not {} current||current==value)return false;values.Add((unit.Id,key,value));return true;}
    bool PlanMoney(List<(string,string,string)> values)=>Want(Document.Bank,"money_account",long.MaxValue.ToString(CultureInfo.InvariantCulture),values);
    bool PlanLevel(List<(string,string,string)> values)=>Want(Document.Economy,"experience_points",int.MaxValue.ToString(CultureInfo.InvariantCulture),values);
    int PlanSkills(List<(string,string,string)> values)=>SkillKeys.Count(k=>Want(Document.Economy,k,"6",values))+(Want(Document.Economy,"adr","63",values)?1:0);
    int Apply(Func<List<(string,string,string)>,int> plan){var values=new List<(string,string,string)>();int n=plan(values);if(values.Count>0)Edit(d=>d.SetMany(values));return n;}
    /// <summary>Money at the save limit, as the Max chip next to the money field sets it; one undo step. False when it already is.</summary>
    public bool MaxMoney()=>Apply(v=>PlanMoney(v)?1:0)>0;
    /// <summary>XP at the storage limit (the top level), as the Max chip next to the level sets it; one undo step. False when it already is.</summary>
    public bool MaxLevel()=>Apply(v=>PlanLevel(v)?1:0)>0;
    /// <summary>Every skill at 6 and every ADR class, as "Max all" sets them; one undo step. Returns the number of values changed.</summary>
    public int MaxSkills()=>Apply(PlanSkills);
    public bool MoneyPending()=>PlanMoney([]);
    public bool LevelPending()=>PlanLevel([]);
    public int SkillsPending()=>PlanSkills([]);
    static readonly string[] UnlockKeys=["unlocked_dealers","unlocked_recruitments"];
    public IReadOnlyList<string> UnlockedDealers()=>ArrayValues(Document.Economy,UnlockKeys[0])??[];
    public IReadOnlyList<string> UnlockedRecruitments()=>ArrayValues(Document.Economy,UnlockKeys[1])??[];
    /// <summary>Appends every known city that has no unlocked truck dealer yet; returns the number added.</summary>
    public int UnlockDealers()=>Unlock(UnlockKeys[0])[0];
    /// <summary>Appends every known city that has no unlocked recruitment agency yet; returns the number added.</summary>
    public int UnlockRecruitments()=>Unlock(UnlockKeys[1])[0];
    /// <summary>Both unlocks in one undo step.</summary>
    public (int Dealers,int Recruitments) UnlockDealersAndRecruitments(){var n=Unlock(UnlockKeys);return(n[0],n[1]);}
    /// <summary>Known cities <see cref="UnlockDealers"/> would add; nothing is staged.</summary>
    public int DealersPending()=>PlanUnlock(UnlockKeys[0],[]);
    /// <summary>Known cities <see cref="UnlockRecruitments"/> would add; nothing is staged.</summary>
    public int RecruitmentsPending()=>PlanUnlock(UnlockKeys[1],[]);
    int[] Unlock(params string[] keys)
    {
        var arrays=new List<(string,string,IReadOnlyList<string>)>();var counts=keys.Select(k=>PlanUnlock(k,arrays)).ToArray();
        if(arrays.Count>0)Edit(doc=>doc.SetArrays(arrays,[]));return counts;
    }
    int PlanUnlock(string key,List<(string,string,IReadOnlyList<string>)> arrays)
    {
        var e=Document.Economy;if(e?.Get(key)==null)return 0;
        if(ArrayValues(e,key) is not {} list)throw new InvalidDataException(L.F("core.arrayedit",key));
        var seen=list.ToHashSet();var added=CitiesOf(Original).Where(seen.Add).ToList();
        if(added.Count>0)arrays.Add((e.Id,key,[..list,..added]));return added.Count;
    }
    /// <summary>True when the unlock list only gained new known cities at its end, each once.</summary>
    bool UnlocksAppended(string key)
    {
        if(ArrayValues(Original.Economy,key) is not {} old||ArrayValues(Document.Economy,key) is not {} now)return false;
        var added=now.Skip(old.Count).ToList();var known=CitiesOf(Original).ToHashSet();
        return now.Take(old.Count).SequenceEqual(old)&&added.All(known.Contains)&&added.Distinct().Count()==added.Count&&!added.Intersect(old).Any();
    }
    static readonly HashSet<string> OdometerFields=["odometer","odometer_float_part","integrity_odometer","integrity_odometer_float_part"];
    public const long MaxOdometer=9_999_999;
    static bool ValidOdometer(string? value)=>uint.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out uint km)&&km<=MaxOdometer;
    /// <summary>Sets the odometer of an owned truck or trailer to whole kilometres (0–9,999,999); the integrity copy follows, both fractions become 0. Trip counters stay.</summary>
    public void SetOdometer(string vehicleId,long km)
    {
        if(km is <0 or >MaxOdometer)throw new InvalidDataException(L.F("input.range",L.T("odometer.label"),CompactNumber.Full(0),CompactNumber.Full(MaxOdometer)));
        if(!OwnedVehicleIds(Document).Contains(vehicleId)||Document.Find(vehicleId).Get("odometer")==null)throw new InvalidDataException(L.F("core.protected",$"{vehicleId} / odometer"));
        var unit=Document.Find(vehicleId);string value=km.ToString(CultureInfo.InvariantCulture);
        var edits=new[]{("odometer",value),("integrity_odometer",value),("odometer_float_part",Zero),("integrity_odometer_float_part",Zero)}.Where(f=>unit.Get(f.Item1)!=null).Select(f=>(vehicleId,f.Item1,f.Item2)).ToList();
        Edit(d=>d.SetMany(edits));
    }
    static SiiUnit? OffenceLog(SiiDocument doc)=>doc.Resolve(doc.Economy,"police_offence_log") is {Type:"police_offence_log"} log?log:null;
    /// <summary>Offences on record: the sum of police_offence_log.offence_total_counts.</summary>
    public static long Offences(SiiDocument doc)=>(ArrayValues(OffenceLog(doc),"offence_total_counts")??[]).Sum(v=>long.TryParse(v,NumberStyles.None,CultureInfo.InvariantCulture,out long n)?n:0);
    public long Offences()=>Offences(Document);
    /// <summary>Zeroes the offence counts and fines (same array length) and the red light fine counter in one undo step; the detailed history and live police state stay. Returns the values changed.</summary>
    public int CleanRecord()
    {
        var values=PlanCleanRecord(Document);if(values.Count>0)Edit(d=>d.SetMany(values));return values.Count;
    }
    /// <summary>Values <see cref="CleanRecord"/> would reset in the staged document, or with <paramref name="original"/> in the file as opened; nothing is staged.</summary>
    public int CleanRecordPending(bool original=false)=>PlanCleanRecord(original?Original:Document).Count;
    static List<(string,string,string)> PlanCleanRecord(SiiDocument doc)
    {
        var values=new List<(string,string,string)>();
        if(OffenceLog(doc) is {} log)foreach(var key in new[]{"offence_total_counts","offence_total_fines"})
            values.AddRange((ArrayValues(log,key)??[]).Select((v,i)=>(v,i)).Where(p=>p.v!="0").Select(p=>(log.Id,$"{key}[{p.i}]","0")));
        if(doc.Economy is {} e&&e.Get("red_light_fine_count") is {} red&&red!="0")values.Add((e.Id,"red_light_fine_count","0"));
        return values;
    }
    public (int Discovered,int Total) BusStops()=>(Document.Units.Count(u=>u.Type=="bus_stop"&&u.Get("discovered")=="true"),Document.Units.Count(u=>u.Type=="bus_stop"));
    IEnumerable<SiiUnit> HiddenBusStops()=>Document.Units.Where(u=>u.Type=="bus_stop"&&u.Get("discovered")=="false");
    /// <summary>Bus stations <see cref="DiscoverBusStops"/> would mark; nothing is staged.</summary>
    public int BusStopsPending()=>HiddenBusStops().Count();
    /// <summary>Marks every undiscovered bus station as discovered in one undo step; its line offers stay. Returns the number changed.</summary>
    public int DiscoverBusStops()
    {
        var values=HiddenBusStops().Select(u=>(u.Id,"discovered","true")).ToList();
        if(values.Count>0)Edit(d=>d.SetMany(values));return values.Count;
    }
    /// <summary>Values of a numbered array, or null when its count and entries do not match.</summary>
    static List<string>? ArrayValues(SiiUnit? unit,string key)
    {
        if(unit==null||!int.TryParse(unit.Get(key),NumberStyles.None,CultureInfo.InvariantCulture,out int n))return null;
        var list=new List<string>();for(int i=0;i<n;i++){if(unit.Get($"{key}[{i}]") is not {} v)return null;list.Add(v);}
        return list;
    }
    static string? CityOf(string companyId)
    {
        var parts=companyId.Split('.');
        return parts is ["company","volatile",_,var city]&&city.Length is >0 and <=12&&city.All(c=>c is >='a' and <='z' or >='0' and <='9' or '_')?city:null;
    }
    static List<string> CitiesOf(SiiDocument doc)=>doc.Units.Where(u=>u.Type=="company"&&!TruckersMpCompany(u.Id)).Select(u=>CityOf(u.Id)).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToList();
    public IReadOnlyList<string> VisitedCities()=>ArrayValues(Document.Economy,"visited_cities")??[];
    /// <summary>Cities that have companies in this save, sorted by token.</summary>
    public IReadOnlyList<string> KnownCities()=>CitiesOf(Original);
    /// <summary>Adds the given cities to the visited list (count 1) and discovers their companies, in one undo step. Visited and unknown cities are skipped.</summary>
    public int VisitCities(IEnumerable<string> cities)
    {
        var arrays=new List<(string,string,IReadOnlyList<string>)>();var values=new List<(string,string,string)>();
        int count=PlanVisits(cities,arrays,values);if(count>0)Edit(doc=>doc.SetArrays(arrays,values));return count;
    }
    /// <summary>Known cities that are not visited yet, as <see cref="VisitCities"/> would add them; nothing is staged.</summary>
    public int CitiesPending()=>PlanVisits(CitiesOf(Original),[],[]);
    int PlanVisits(IEnumerable<string> cities,List<(string,string,IReadOnlyList<string>)> arrays,List<(string,string,string)> values)
    {
        var e=Document.Economy;if(e?.Get("visited_cities")==null)return 0;
        if(ArrayValues(e,"visited_cities") is not {} visited||ArrayValues(e,"visited_cities_count") is not {} counts||visited.Count!=counts.Count)throw new InvalidDataException(L.F("core.arrayedit","visited_cities"));
        var known=CitiesOf(Original).ToHashSet();var seen=visited.ToHashSet();var added=cities.Where(c=>known.Contains(c)&&seen.Add(c)).ToList();
        if(added.Count==0)return 0;
        arrays.Add((e.Id,"visited_cities",[..visited,..added]));arrays.Add((e.Id,"visited_cities_count",[..counts,..added.Select(_=>"1")]));
        var fresh=added.ToHashSet();
        values.AddRange(Document.Units.Where(u=>u.Type=="company"&&u.Get("discovered")=="false"&&CityOf(u.Id) is {} city&&fresh.Contains(city)).Select(u=>(u.Id,"discovered","true")));
        return added.Count;
    }
    /// <summary>True when the visited lists only gained new known cities at their end, each with a count of at least 1.</summary>
    bool VisitsAppended()
    {
        if(ArrayValues(Original.Economy,"visited_cities") is not {} oldCities||ArrayValues(Original.Economy,"visited_cities_count") is not {} oldCounts
            ||ArrayValues(Document.Economy,"visited_cities") is not {} cities||ArrayValues(Document.Economy,"visited_cities_count") is not {} counts)return false;
        var added=cities.Skip(oldCities.Count).ToList();var known=CitiesOf(Original).ToHashSet();
        return oldCities.Count==oldCounts.Count&&cities.Count==counts.Count&&cities.Take(oldCities.Count).SequenceEqual(oldCities)&&counts.Take(oldCounts.Count).SequenceEqual(oldCounts)
            &&added.All(known.Contains)&&added.Distinct().Count()==added.Count&&!added.Intersect(oldCities).Any()
            &&counts.Skip(oldCounts.Count).All(c=>uint.TryParse(c,NumberStyles.None,CultureInfo.InvariantCulture,out uint n)&&n>=1);
    }
    /// <summary>AI drivers the player has hired (player.drivers); the player's own driver and the recruitment pool are excluded.</summary>
    static List<string> HiredDriverIds(SiiDocument doc)=>(ArrayValues(doc.Player,"drivers")??[]).Distinct().Where(id=>doc.TryFind(id)?.Type=="driver_ai").ToList();
    public List<SiiUnit> HiredDrivers()=>HiredDriverIds(Document).Select(Document.Find).ToList();
    /// <summary>Sets ADR and every skill of every hired driver to the maximum in one undo step. Returns the number of drivers changed.</summary>
    public int MaxDriverSkills()=>Apply(PlanDriverSkills);
    /// <summary>Hired drivers <see cref="MaxDriverSkills"/> would change; nothing is staged.</summary>
    public int DriverSkillsPending()=>PlanDriverSkills([]);
    int PlanDriverSkills(List<(string,string,string)> values)
    {
        int drivers=0;
        foreach(var d in HiredDrivers())
        {
            int before=values.Count;
            foreach(var key in SkillKeys.Append("adr")){string max=SkillMax(key).ToString(CultureInfo.InvariantCulture);if(d.Get(key) is {} v&&v!=max)values.Add((d.Id,key,max));}
            if(values.Count>before)drivers++;
        }
        return drivers;
    }
    /// <summary>Visible plate text: markup removed, the region seal image shown as a space, the country after "|" dropped.</summary>
    public static string PlateText(string? raw)
    {
        if(raw==null)return "";
        string text=SiiDocument.Display(raw);int bar=text.LastIndexOf('|');if(bar>=0)text=text[..bar];
        return Regex.Replace(Regex.Replace(Regex.Replace(text,"<img[^>]*>"," "),"<[^>]*>","")," {2,}"," ").Trim();
    }
    static string? PlateCountry(string? raw)
    {
        if(raw==null)return null;string text=SiiDocument.Display(raw);int bar=text.LastIndexOf('|');
        return bar>=0&&text[(bar+1)..].All(c=>char.IsAsciiLetterOrDigit(c)||c=='_')?text[(bar+1)..]:null;
    }
    static bool ValidPlate(string text)=>text.Length is >=1 and <=12&&text==text.Trim()&&text.All(c=>char.IsAsciiLetterOrDigit(c)||c is ' ' or '-')&&text.Any(char.IsAsciiLetterOrDigit);
    /// <summary>Replaces the plate of an owned truck with plain text (1–12 letters, digits, spaces, hyphens); the plate country stays.
    /// Trailer plates are refused: ETS2 does not take over an edited trailer plate.</summary>
    public void SetLicensePlate(string vehicleId,string text)
    {
        if(Document.TryFind(vehicleId)?.Type=="trailer")throw new InvalidDataException(L.T("core.plate.trailer"));
        text=text.Trim();if(!ValidPlate(text))throw new InvalidDataException(L.T("core.plate"));
        if(!OwnedVehicleIds(Document).Contains(vehicleId)||Document.Find(vehicleId) is not {Type:"vehicle"} truck||PlateCountry(truck.Get("license_plate")) is not {} country)throw new InvalidDataException(L.F("core.protected",$"{vehicleId} / license_plate"));
        Edit(d=>d.Set(vehicleId,"license_plate",$"\"{text}|{country}\""));
    }
    public void Headquarters(string garage)
    {
        var g=Document.Find(garage);
        if(g.Number("status")==0) throw new InvalidDataException(L.T("core.buyfirst"));
        var p=Document.Player ?? throw new InvalidDataException(L.T("core.noplayer"));
        Edit(d=>d.Set(p.Id,"hq_city",garage.Replace("garage.","")));
    }
    public void MoveSlot(string source,string target,string kind,int index)
    {
        if(source==target) throw new InvalidDataException(L.T("core.othertarget"));
        Edit(d=>
        {
            var a=d.Find(source);var b=d.Find(target);
            if(a.Type!="garage"||b.Type!="garage"||b.Number("status")==0) throw new InvalidDataException(L.T("core.ownedtarget"));
            string value=a.Get($"{kind}[{index}]")??throw new InvalidDataException(L.T("core.slotmissing"));
            if(value=="null") throw new InvalidDataException(L.T("core.slotempty"));
            int free=Enumerable.Range(0,(int)b.Number(kind)).FirstOrDefault(i=>b.Get($"vehicles[{i}]")=="null"&&b.Get($"drivers[{i}]")=="null",-1);
            if(free<0) throw new InvalidDataException(L.T("core.targetfull"));
            var moves=new List<(string,string,string)>();
            foreach(var field in new[]{"vehicles","drivers"})
            {
                var occupant=a.Get($"{field}[{index}]")??throw new InvalidDataException(L.T("core.slotmissing"));
                moves.Add((source,$"{field}[{index}]","null"));moves.Add((target,$"{field}[{free}]",occupant));
            }
            d.SetMany(moves);
        });
    }
}
