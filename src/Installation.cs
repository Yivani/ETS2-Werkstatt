using Microsoft.Win32;
using System.Text;
using System.Text.RegularExpressions;

namespace Werkstatt;

public sealed record SaveFile(string Label,string Path,DateTime Modified,string Kind="")
{ public override string ToString()=>Label; }
public sealed record Dlc(string File,string Name,long Size);
public sealed class Installation
{
    public string? GamePath {get;set;}
    public string Version {get;private set;}="";
    public List<string> SteamRoots {get;}=[];
    public List<Dlc> Dlcs {get;private set;}=[];
    public List<SaveFile> Saves {get;private set;}=[];
    public List<string> Warnings {get;}=[];
    /// <summary>Saves found in Steam Cloud profiles. They are counted, not listed: they can only be edited after Steam Cloud is turned off for the profile.</summary>
    public int CloudSaves {get;private set;}
    public int LocalProfiles {get;private set;}
    internal static (string Documents,string[] SteamRoots)? TestRoots {get;set;}
    public bool SteamManifest => GamePath!=null && File.Exists(Path.GetFullPath(Path.Combine(GamePath,"..","..","appmanifest_227300.acf")));
    static readonly Dictionary<string,string> Names = new()
    {
        ["dlc_east"]="Going East!",["dlc_north"]="Scandinavia",["dlc_fr"]="Vive la France !",["dlc_it"]="Italia",
        ["dlc_balt"]="Beyond the Baltic Sea",["dlc_balkan_e"]="Road to the Black Sea",["dlc_iberia"]="Iberia",
        ["dlc_balkan_w"]="West Balkans",["dlc_greece"]="Greece",["dlc_nordic"]="Nordic Horizons",["dlc_nordic_horizons"]="Nordic Horizons",
        ["dlc_heavy_cargo"]="Heavy Cargo Pack",["dlc_oversize"]="Special Transport",["dlc_trailers"]="Schwarzmüller Trailer Pack"
    };
    /// <summary>The name the player gave the save in ETS2 (info.sii), or a readable label for unnamed slots.</summary>
    public static string SaveTitle(string gameSii)
    {
        string folder=Path.GetFileName(Path.GetDirectoryName(gameSii))??"";
        try
        {
            string info=Path.Combine(Path.GetDirectoryName(gameSii)!,"info.sii");
            if(File.Exists(info)&&new FileInfo(info).Length<1024*1024&&new SiiDocument(SaveSession.Decode(info)).First("save_container")?.Get("name") is {} raw&&SiiDocument.Display(raw).Trim() is {Length:>0} name)return name;
        }
        catch(Exception e) when(e is IOException or InvalidDataException or UnauthorizedAccessException or FormatException or ArgumentException){}
        return SlotLabel(folder);
    }
    internal static string SlotLabel(string folder)
    {
        var m=Regex.Match(folder,@"^(autosave(?:_drive|_job)?|quicksave|multi)(?:_(\d+))?$");
        if(m.Success)
        {
            string label=L.T("slot."+m.Groups[1].Value);
            return m.Groups[2].Success?$"{label} {m.Groups[2].Value}":label;
        }
        return int.TryParse(folder,out int number)?L.F("slot.number",number):folder;
    }
    internal static string Normalize(string path)=>Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
    public static Installation Detect(string? gameOverride=null)
    {
        var result=new Installation();
        var roots=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var test=TestRoots;
        if(test!=null)roots.UnionWith(test.Value.SteamRoots.Where(Directory.Exists).Select(Normalize));
        else
        {
            foreach(var key in new[]{@"HKEY_CURRENT_USER\Software\Valve\Steam",@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam",@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam"})
                foreach(var name in new[]{"SteamPath","InstallPath"}) if(Registry.GetValue(key,name,null) is string p && Directory.Exists(p)) roots.Add(Normalize(p));
            string fallback=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),"Steam");
            if(Directory.Exists(fallback)) roots.Add(Normalize(fallback));
        }
        result.SteamRoots.AddRange(roots);
        var libraries=new HashSet<string>(roots,StringComparer.OrdinalIgnoreCase);
        foreach(var root in roots)
        {
            string vdf=Path.Combine(root,"steamapps","libraryfolders.vdf");
            try
            {
                if(File.Exists(vdf))
                    foreach(Match m in Regex.Matches(File.ReadAllText(vdf),"\"path\"\\s*\"([^\"]+)\"")) libraries.Add(Normalize(m.Groups[1].Value.Replace("\\\\","\\")));
            }
            catch(Exception e) when(e is IOException or UnauthorizedAccessException or ArgumentException){result.Warnings.Add($"{vdf}: {e.Message}");}
        }
        if(gameOverride!=null && File.Exists(Path.Combine(gameOverride,"def.scs"))) result.GamePath=gameOverride;
        foreach(var lib in libraries)
        {
            var manifest=Path.Combine(lib,"steamapps","appmanifest_227300.acf");
            if(!File.Exists(manifest)) continue;
            var m=Regex.Match(File.ReadAllText(manifest),"\"installdir\"\\s*\"([^\"]+)\"");
            var path=Path.Combine(lib,"steamapps","common",m.Success?m.Groups[1].Value:"Euro Truck Simulator 2");
            if(result.GamePath==null && Directory.Exists(path)) result.GamePath=path;
        }
        if(result.GamePath!=null)
        {
            result.Dlcs=Directory.EnumerateFiles(result.GamePath,"dlc_*.scs").Select(f=>new Dlc(f,Names.GetValueOrDefault(Path.GetFileNameWithoutExtension(f),System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Path.GetFileNameWithoutExtension(f)[4..].Replace('_',' '))),new FileInfo(f).Length)).OrderBy(d=>d.Name).ToList();
            string exe=Path.Combine(result.GamePath,"bin","win_x64","eurotrucks2.exe");
            if(File.Exists(exe)) result.Version=System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).ProductVersion??"";
        }
        string documents=test?.Documents??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"Euro Truck Simulator 2");
        var cloudRoots=new HashSet<string>(StringComparer.OrdinalIgnoreCase){Path.Combine(documents,"steam_profiles")};
        foreach(var root in roots)
        {
            string userdata=Path.Combine(root,"userdata");
            if(!Directory.Exists(userdata)) continue;
            try{foreach(var account in Directory.EnumerateDirectories(userdata)) cloudRoots.Add(Path.Combine(account,"227300","remote","profiles"));}
            catch(Exception e) when(e is IOException or UnauthorizedAccessException){result.Warnings.Add($"{userdata}: {e.Message}");}
        }
        IEnumerable<string> SaveFiles(string profile)
        {
            string saveRoot=Path.Combine(profile,"save");
            return Directory.Exists(saveRoot)?Directory.EnumerateDirectories(saveRoot).Select(s=>Path.Combine(s,"game.sii")).Where(File.Exists):[];
        }
        var saves=new List<SaveFile>();string local=Path.Combine(documents,"profiles");
        if(Directory.Exists(local))
        {
            try
            {
                foreach(var profile in Directory.EnumerateDirectories(local))
                {
                    string name=Path.GetFileName(profile);
                    try { name=Encoding.UTF8.GetString(Convert.FromHexString(name)); } catch(FormatException){}
                    foreach(var f in SaveFiles(profile)) saves.Add(new($"{name} · {SaveTitle(f)}",f,File.GetLastWriteTime(f),"local"));
                    string prof=Path.Combine(profile,"profile.sii");
                    if(File.Exists(prof)) saves.Add(new(name,prof,File.GetLastWriteTime(prof),"profile"));
                    if(File.Exists(prof)||Directory.Exists(Path.Combine(profile,"save"))) result.LocalProfiles++;
                }
            }
            catch(Exception e) when(e is IOException or UnauthorizedAccessException){result.Warnings.Add($"{local}: {e.Message}");}
        }
        foreach(var root in cloudRoots.Where(Directory.Exists))
        {
            try{result.CloudSaves+=Directory.EnumerateDirectories(root).Sum(p=>SaveFiles(p).Count());}
            catch(Exception e) when(e is IOException or UnauthorizedAccessException){result.Warnings.Add($"{root}: {e.Message}");}
        }
        result.Saves=saves.DistinctBy(s=>Normalize(s.Path),StringComparer.OrdinalIgnoreCase).OrderByDescending(s=>s.Modified).ToList();
        return result;
    }
}
