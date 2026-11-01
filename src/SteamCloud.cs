using System.Diagnostics;

namespace Werkstatt;
/// <summary>
/// Steam Cloud profiles (Steam\userdata\&lt;id&gt;\227300\remote, or Documents\Euro Truck Simulator 2\steam_profiles) are read only.
/// Steam tracks them in remotecache.vdf and ETS2 rejects a file replaced behind Steam's back, so nothing is ever written there.
/// The player turns Steam Cloud off for the profile in ETS2; the game then keeps it in Documents\Euro Truck Simulator 2\profiles.
/// </summary>
internal static class SteamCloud
{
    internal static Func<bool> SteamProbe {get;set;}=()=>{var processes=Process.GetProcessesByName("steam");try{return processes.Length>0;}finally{foreach(var p in processes)p.Dispose();}};
    public static bool SteamRunning=>SteamProbe();
    public static bool IsCloud(string path)
    {
        for(var dir=new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))!);dir!=null;dir=dir.Parent)
        {
            if(dir.Name.Equals("steam_profiles",StringComparison.OrdinalIgnoreCase))return true;
            if(dir.Name.Equals("remote",StringComparison.OrdinalIgnoreCase)&&dir.Parent is {} app&&(app.Name=="227300"||File.Exists(Path.Combine(app.FullName,"remotecache.vdf"))))return true;
        }
        return false;
    }
    /// <summary>Throws before anything is written to a Steam Cloud path.</summary>
    internal static void Refuse(string path){if(IsCloud(path))throw new IOException(L.T("core.cloud"));}
}
