using System.Text.Json;

namespace Werkstatt;
internal static class SettingsBackup
{
    public static void Capture(string save,string backup)
    {
        var files=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var saveFolder=new DirectoryInfo(Path.GetDirectoryName(save)!);
        var profile=saveFolder.Parent?.Name=="save"?saveFolder.Parent.Parent:null;
        string documents=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"Euro Truck Simulator 2");
        var folders=new List<string>();
        if(profile!=null)
        {
            folders.Add(profile.FullName);
            folders.Add(Path.Combine(documents,"profiles",profile.Name));
            folders.Add(documents);
        }
        foreach(string folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
            if(Directory.Exists(folder))foreach(string file in Directory.EnumerateFiles(folder))
                if(Path.GetFileName(file) is "controls.sii" or "global_controls.sii" or "config.cfg" or "config_local.cfg" or "profile.sii" or "profile.bak.sii"||Path.GetFileName(file).StartsWith("gearbox_layout",StringComparison.OrdinalIgnoreCase))files.Add(file);
        if(files.Count==0)return;
        string target=Path.Combine(backup,"Einstellungen");Directory.CreateDirectory(target);var manifest=new List<object>();
        foreach(string file in files.Order(StringComparer.OrdinalIgnoreCase))
        {
            byte[] bytes=File.ReadAllBytes(file);string name=$"{manifest.Count:00}-"+Path.GetFileName(file);
            File.WriteAllBytes(Path.Combine(target,name),bytes);manifest.Add(new{source=file,copy=name,sha256=SaveSession.Hash(bytes)});
        }
        File.WriteAllText(Path.Combine(target,"Dateien.json"),JsonSerializer.Serialize(manifest,new JsonSerializerOptions{WriteIndented=true}));
    }
    sealed record Entry(string source,string copy,string sha256);
    static readonly string[] ControlFiles=["controls.sii","global_controls.sii"];
    /// <summary>Key binding files saved in a backup, or an empty list when that backup has none.</summary>
    public static List<(string Source,string Copy)> Controls(string backup)
    {
        string manifest=Path.Combine(backup,"Einstellungen","Dateien.json");if(!File.Exists(manifest))return [];
        try
        {
            return (JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(manifest))??[])
                .Where(e=>ControlFiles.Contains(Path.GetFileName(e.source),StringComparer.OrdinalIgnoreCase)&&File.Exists(Path.Combine(backup,"Einstellungen",e.copy))&&SaveSession.Hash(File.ReadAllBytes(Path.Combine(backup,"Einstellungen",e.copy)))==e.sha256)
                .Select(e=>(e.source,Path.Combine(backup,"Einstellungen",e.copy))).ToList();
        }
        catch(Exception e) when(e is JsonException or IOException or UnauthorizedAccessException){return [];}
    }
    /// <summary>Puts the key bindings of a backup back in place. The current files are copied to a new backup folder first.</summary>
    public static string RestoreControls(string backup)
    {
        if(SaveSession.GameRunning)throw new IOException(L.T("core.running.restore"));
        var files=Controls(backup);if(files.Count==0)throw new InvalidDataException(L.T("controls.none"));
        foreach(var (source,_) in files)SteamCloud.Refuse(source);
        string safety=Path.Combine(SaveSession.BackupRoot,"controls-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff",System.Globalization.CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N")[..8]);Directory.CreateDirectory(safety);
        var saved=new List<object>();
        foreach(var (source,_) in files)if(File.Exists(source)){byte[] bytes=File.ReadAllBytes(source);string name=$"{saved.Count:00}-"+Path.GetFileName(source);File.WriteAllBytes(Path.Combine(safety,name),bytes);saved.Add(new{source,copy=name,sha256=SaveSession.Hash(bytes)});}
        File.WriteAllText(Path.Combine(safety,"Dateien.json"),JsonSerializer.Serialize(saved,new JsonSerializerOptions{WriteIndented=true}));
        var previous=files.Where(f=>File.Exists(f.Source)).ToDictionary(f=>f.Source,f=>File.ReadAllBytes(f.Source),StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach(var (source,copy) in files)
            {
                byte[] bytes=File.ReadAllBytes(copy);Directory.CreateDirectory(Path.GetDirectoryName(source)!);
                if(File.Exists(source))SaveSession.AtomicReplace(source,bytes);else File.WriteAllBytes(source,bytes);
                if(SaveSession.Hash(File.ReadAllBytes(source))!=SaveSession.Hash(bytes))throw new IOException(L.F("controls.failed",safety));
            }
        }
        catch
        {
            foreach(var (source,_) in files)
                try{if(previous.TryGetValue(source,out var bytes))SaveSession.AtomicReplace(source,bytes);else if(File.Exists(source))File.Delete(source);}catch(IOException){}
            throw;
        }
        return safety;
    }
}
