using System.Diagnostics;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Werkstatt;
public static class SelfTest
{
    public static void Run(string[] args)
    {
        string? Option(string key){int i=Array.IndexOf(args,key);return i>=0&&i+1<args.Length?args[i+1]:null;}
        string report=Option("--report")??Path.Combine(AppContext.BaseDirectory,"self-test.json");
        if(Option("--dump-text") is {} dump){File.WriteAllText(Option("--output")??throw new InvalidDataException("--output fehlt."),SaveSession.Decode(dump));return;}
        if(Option("--convert-copy") is {} source){SaveSession.ConvertCopy(source,Option("--output")??throw new InvalidDataException("--output fehlt."));return;}
        if(Option("--preview") is {} previewSave){UiVerification.RenderPreviews(previewSave,Option("--output")??throw new InvalidDataException("--output fehlt."),report);return;}
        string root=Path.Combine(Path.GetTempPath(),"ETS2Werkstatt-Tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var checks=new List<object>();var watch=Stopwatch.StartNew();
        void Check(string name,Action test){test();checks.Add(new{name,passed=true});}
        void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
        void Reject(Action action){bool rejected=false;try{action();}catch(InvalidDataException){rejected=true;}Assert(rejected,"Ungültige Änderung wurde nicht abgelehnt.");}
        L.Use("de");
        Check("Compact money accepts German units and exact large integers",()=>
        {
            foreach(var (text,value) in new[]{("30k",30_000L),("100k",100_000L),("1,5 Mio",1_500_000L),("1.5m",1_500_000L),("1 Milliarde",1_000_000_000L),("1 Billion",1_000_000_000_000L),("1 Billiarde",1_000_000_000_000_000L),("1 Trillion",1_000_000_000_000_000_000L),("max",long.MaxValue)})Assert(CompactNumber.Parse(text)==value,"Kurzform falsch: "+text);
            foreach(long value in new[]{0L,30_000L,1_000_000_000_000_007L,long.MaxValue})Assert(CompactNumber.Parse(CompactNumber.Full(value))==value&&CompactNumber.Parse(CompactNumber.Editable(value))==value,"Ganzzahl-Präzision verloren.");
            Assert(CompactNumber.Editable(-500)=="-500","Vorhandener negativer Kontostand kann nicht angezeigt werden.");
        });
        Check("Money accepts a minus for debts; every other number stays positive",()=>
        {
            string before=L.Code;
            try
            {
                foreach(var language in L.Languages)
                {
                    L.Use(language.Code);long Money(string s)=>CompactNumber.Parse(s,long.MaxValue,CompactNumber.MinMoney);
                    Assert(Money("-50k")==-50_000&&Money("−300")==-300&&Money("-max")==CompactNumber.MinMoney&&Money("max")==long.MaxValue,"Negative Kurzform falsch: "+language.Code);
                    foreach(long value in new[]{-1L,-500L,-300_491L,-1_500_000L,-1_000_000_000_000_007L,CompactNumber.MinMoney})
                        Assert(Money(CompactNumber.Full(value))==value&&Money(CompactNumber.Editable(value))==value,$"{language.Code}: {value} kein Roundtrip");
                    Reject(()=>CompactNumber.Parse("-50k"));Reject(()=>CompactNumber.Parse("-1",int.MaxValue));Reject(()=>Money("--5"));Reject(()=>Money("-"));
                    Reject(()=>Money("-"+CompactNumber.Full(long.MaxValue)+"0"));
                }
            }
            finally{L.Use(before);}
        });
        Check("Invalid and overflowing compact values are rejected",()=>{foreach(var text in new[]{"-1","1..00","1,2,3","0,5","10Trio","30abc"})Reject(()=>CompactNumber.Parse(text));Reject(()=>CompactNumber.Parse("3Mrd",int.MaxValue));});
        Check("Every interface language parses its own units and number formats",()=>
        {
            var cases=new Dictionary<string,(string,long)[]>
            {
                ["en"]=[("1 billion",1_000_000_000L),("1,500",1_500L),("1.5m",1_500_000L),("2.25B",2_250_000_000L),("1,000,000",1_000_000L),("3 trillion",3_000_000_000_000L),("1,5 M",1_500_000L)],
                ["de"]=[("1 Billion",1_000_000_000_000L),("1.500",1_500L),("1,5 Mio",1_500_000L)],
                ["pl"]=[("1,5 mln",1_500_000L),("2 mld",2_000_000_000L),("30 tys.",30_000L),("1 bilion",1_000_000_000_000L),("1 500 000",1_500_000L)],
                ["ru"]=[("1,5 млн",1_500_000L),("2 млрд",2_000_000_000L),("30 тыс.",30_000L),("1 трлн",1_000_000_000_000L)],
                ["zh"]=[("3万",30_000L),("1.5亿",150_000_000L),("2万亿",2_000_000_000_000L),("1,000,000",1_000_000L)],
            };
            try
            {
                foreach(var (code,items) in cases)
                {
                    L.Use(code);foreach(var (text,value) in items)Assert(CompactNumber.Parse(text)==value,$"{code}: {text}");
                    foreach(long value in new[]{0L,30_000L,1_500_000L,1_000_000_000_000_007L,long.MaxValue})Assert(CompactNumber.Parse(CompactNumber.Full(value))==value&&CompactNumber.Parse(CompactNumber.Editable(value))==value&&CompactNumber.Parse(CompactNumber.Short(value))<=value,$"{code}: roundtrip {value}");
                    foreach(var text in new[]{"-1","0,5","30abc"})Reject(()=>CompactNumber.Parse(text));
                }
                Assert(L.Languages.Length==5&&L.Keys.All(k=>L.Languages.All(l=>{L.Use(l.Code);return L.T(k).Length>0;})),"Leere Übersetzung.");
            }
            finally{L.Use("de");}
        });
        var curve=XpCurve.Standard;
        Check("XP level boundaries match ETS2 thresholds",()=>{Assert(curve.Level(0)==0&&curve.Level(199)==0&&curve.Level(200)==1&&curve.Level(700)==2,"Frühe Levelgrenzen falsch.");Assert(curve.Level(19144)==13&&curve.Xp(14)==21600,"Level 13/14 falsch.");});
        Check("XP and level roundtrip remains exact through the storage limit",()=>{for(int level=0;level<=curve.MaxLevel;level++)Assert(curve.Level(curve.Xp(level))==level,"Level-Roundtrip falsch.");Assert(curve.Xp(curve.MaxLevel)<=int.MaxValue&&curve.Level(int.MaxValue)==curve.MaxLevel,"Max-Level falsch.");Reject(()=>curve.Xp(curve.MaxLevel+1));});
        const string fixture="""
SiiNunit
{
economy : _nameless.1 {
 bank: _nameless.2
 player: _nameless.3
 experience_points: 100
 game_time: 1000
 adr: 0
 long_dist: 0
 heavy: 0
 fragile: 0
 urgent: 0
 mechanical: 0
 garages: 2
 garages[0]: garage.berlin
 garages[1]: garage.paris
}
bank : _nameless.2 {
 money_account: 1000
 unknown_mod_field: "braces { } and \\"quoted\\""
}
player : _nameless.3 {
 hq_city: berlin
}
garage : garage.berlin {
 vehicles: 3
 vehicles[0]: null
 vehicles[1]: null
 vehicles[2]: null
 drivers: 3
 drivers[0]: null
 drivers[1]: null
 drivers[2]: null
 trailers: 0
 status: 2
 profit_log: _nameless.4
 productivity: 17
}
garage : garage.paris {
 vehicles: 0
 drivers: 0
 trailers: 0
 status: 0
 profit_log: _nameless.5
 productivity: 42
}
profit_log : _nameless.4 {
 stats_data: 0
}
profit_log : _nameless.5 {
 stats_data: 0
}
vehicle : _nameless.6 {
 fuel_relative: &3f800000
}
driver_ai : _nameless.7 {
 experience_points: 12
}
}
""";
        string file=Path.Combine(root,"game.sii");File.WriteAllText(file,fixture,new UTF8Encoding(false));
        WorkflowTest.Run(root,fixture,Check);
        byte[] Container(byte[] content,uint? declaredSize=null)
        {
            using var compressed=new MemoryStream();
            using(var zlib=new ZLibStream(compressed,CompressionLevel.Fastest,true))zlib.Write(content);
            using var aes=Aes.Create();aes.Key=SaveSession.SiiKey;aes.GenerateIV();
            var encrypted=aes.EncryptCbc(compressed.ToArray(),aes.IV);var container=new byte[56+encrypted.Length];
            "ScsC"u8.CopyTo(container);aes.IV.CopyTo(container,36);BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(52),declaredSize??(uint)content.Length);encrypted.CopyTo(container,56);return container;
        }
        var textBytes=Encoding.UTF8.GetBytes(fixture);
        Check("ScsC text decrypts with bundled compression and no external DLL",()=>Assert(SaveSession.DecodeBytes(Container(textBytes))==fixture,"ScsC-Text nicht entschlüsselt."));
        Check("Truncated encrypted container rejected",()=>Reject(()=>SaveSession.DecodeBytes("ScsC"u8.ToArray())));
        Check("Oversized decompression header rejected",()=>Reject(()=>SaveSession.DecodeBytes(Container(textBytes,128*1024*1024+1))));
        Check("Decompression output larger than header rejected",()=>Reject(()=>SaveSession.DecodeBytes(Container(textBytes,(uint)textBytes.Length-1))));
        Check("Decompression output smaller than header rejected",()=>Reject(()=>SaveSession.DecodeBytes(Container(textBytes,(uint)textBytes.Length+1))));
        Check("Unknown save format reports a readable error",()=>Reject(()=>SaveSession.DecodeBytes("UnknownSave"u8.ToArray())));
        IntegrityTest.Run(root,Check,Option("--binary-sample"),Option("--integrity-export"));
        IntegrityTest.Features(root,Check,Option("--binary-sample")??Option("--sample"));
        IntegrityTest.Archives(root,Check);
        var s=SaveSession.Open(file);
        Check("Lossless parse and unknown fields",()=>Assert(s.Document.Text==fixture&&s.Document.Units.Count==9,"Parser hat die Datei verändert."));
        Check("Money changes only the bank field",()=>{s.Edit(d=>d.Set(d.Bank!.Id,"money_account","9999999999"));Assert(s.Changes().Count==1&&s.Document.Bank!.Number("money_account")==9999999999,"Geldänderung außerhalb des Bankfelds.");});
        Check("Rejected transaction keeps earlier edits",()=>{string before=s.Document.Text;Reject(()=>s.Edit(d=>{d.Set(d.Bank!.Id,"money_account","1");d.Set(d.Bank.Id,"missing","2");}));Assert(before==s.Document.Text,"Fehlerhafte Transaktion teilweise übernommen.");});
        Check("Buy existing garage with consistent slots",()=>{s.Garage("garage.paris",2);var g=s.Document.Find("garage.paris");Assert(g.Number("vehicles")==3&&g.Number("drivers")==3&&g.Number("status")==2&&g.Number("productivity")==42&&g.Get("profit_log")=="_nameless.5","Garagenkauf beschädigt Felder.");Assert(s.Document.Economy!.Get("garages[1]")=="garage.paris","Garagenreferenzen geändert.");});
        Check("Upgrade and empty downgrade preserve data",()=>{s.Garage("garage.paris",3);Assert(s.Document.Find("garage.paris").Get("vehicles[4]")=="null","Upgrade ohne Slots.");s.Garage("garage.paris",2);Assert(s.Document.Find("garage.paris").Get("vehicles[3]")==null,"Downgrade mit verwaisten Slots.");});
        Check("Headquarters cannot be sold",()=>Reject(()=>s.Garage("garage.berlin",0)));
        Check("Occupied downgrade is rejected atomically",()=>{s.Garage("garage.paris",3);s.Edit(d=>d.Set("garage.paris","vehicles[4]","_nameless.6"));var before=s.Document.Text;Reject(()=>s.Garage("garage.paris",2));Assert(s.Document.Text==before,"Fahrzeug verloren.");});
        Check("Move transfers truck and driver together",()=>{s.Edit(d=>d.Set("garage.paris","drivers[4]","_nameless.7"));s.MoveSlot("garage.paris","garage.berlin","vehicles",4);Assert(s.Document.Find("garage.berlin").Get("vehicles[0]")=="_nameless.6"&&s.Document.Find("garage.berlin").Get("drivers[0]")=="_nameless.7"&&s.Document.Find("garage.paris").Get("drivers[4]")=="null","Fahrzeug und Fahrer getrennt.");});
        Check("New dangling references rejected",()=>Reject(()=>s.Edit(d=>d.Set("garage.berlin","vehicles[0]","_nameless.missing"))));
        Check("Broken arrays rejected",()=>Reject(()=>s.Edit(d=>d.Set("garage.berlin","vehicles","99"))));
        Check("Broken syntax rejected",()=>{Reject(()=>new SiiDocument("SiiNunit { bank : x { money: 2 }"));Reject(()=>s.Edit(d=>d.Set("garage.paris","status","2\nwrong: 1")));});
        Check("SCS float roundtrip",()=>Assert(SiiDocument.Float(SiiDocument.FloatValue(.75))==.75,"IEEE-754 falsch."));
        Check("Actual copied bytes never touched by staging",()=>Assert(File.ReadAllText(file)==fixture,"Vormerken hat auf Festplatte geschrieben."));
        Check("Atomic replacement writes expected bytes",()=>{string f=Path.Combine(root,"atomic.txt");File.WriteAllText(f,"before");SaveSession.AtomicReplace(f,Encoding.UTF8.GetBytes("after"));Assert(File.ReadAllText(f)=="after","Atomarer Schreibvorgang fehlgeschlagen.");});
        var storage=SaveSession.DataRoot;SaveSession.DataRoot=Path.Combine(root,"AppData");
        try
        {
            string? backup=null;
            if(SaveSession.GameRunning)Check("Running game blocks public writes",()=>{bool blocked=false;try{s.Save();}catch(IOException){blocked=true;}Assert(blocked,"Schreiben bei laufendem ETS2 erlaubt.");});
            Check("Persistence creates backup and reopens",()=>{backup=s.CommitEdits();Assert(File.Exists(Path.Combine(backup,"backup.json")),"Backup fehlt.");Assert(SaveSession.Open(file).Document.Text==s.Document.Text&&!s.Dirty,"Speichern nicht bestätigt.");});
            Check("Backup restore persistence is byte exact",()=>{var b=SaveSession.Backups().Single(b=>b.Folder==backup);var bytes=File.ReadAllBytes(Path.Combine(b.Folder,b.FileName));var current=SaveSession.Open(file);current.SaveBytes(bytes,SaveSession.Decode(Path.Combine(b.Folder,b.FileName)));Assert(File.ReadAllText(file)==fixture&&SaveSession.Hash(File.ReadAllBytes(file))==b.Hash,"Restore stimmt nicht mit Originalbytes überein.");});
            Check("Concurrent changes rejected by persistence",()=>{var c=SaveSession.Open(file);c.Edit(d=>d.Set(d.Bank!.Id,"money_account","12"));File.AppendAllText(file,"\n");bool rejected=false;try{c.CommitEdits();}catch(IOException){rejected=true;}Assert(rejected,"Externe Änderung überschrieben.");});
        }
        finally{SaveSession.DataRoot=storage;}
        Check("Steam Cloud saves are refused before anything is written and only counted, never listed",()=>
        {
            var probe=SaveSession.GameProbe;var oldRoot=SaveSession.DataRoot;SaveSession.DataRoot=Path.Combine(root,"CloudData");SaveSession.GameProbe=()=>false;
            try
            {
                string steam=Path.Combine(root,"fake-steam"),documents=Path.Combine(root,"fake-documents"),remote=Path.Combine(steam,"userdata","1","227300","remote","profiles");
                string Slot(string profiles,string profile,string slot)
                {
                    string dir=Path.Combine(profiles,profile,"save",slot);Directory.CreateDirectory(dir);string game=Path.Combine(dir,"game.sii");File.WriteAllText(game,fixture);
                    File.WriteAllText(Path.Combine(dir,"info.sii"),"SiiNunit\n{\nsave_container : _nameless.1 {\n name: \"Slot\"\n file_time: 1\n}\n}\n");return game;
                }
                string cloud=Slot(remote,"594956414E49","1");Slot(remote,"594956414E49","autosave");string mirror=Slot(Path.Combine(documents,"steam_profiles"),"414243","2");
                string local=Slot(Path.Combine(documents,"profiles"),"4C4F43414C","1");File.WriteAllText(Path.Combine(documents,"profiles","4C4F43414C","profile.sii"),"x");Directory.CreateDirectory(Path.Combine(documents,"profiles","empty-folder"));
                string cloudControls=Path.Combine(remote,"594956414E49","controls.sii");File.WriteAllText(cloudControls,"cloud bindings");
                Installation.TestRoots=(documents,[steam]);Installation found;try{found=Installation.Detect();}finally{Installation.TestRoots=null;}
                Assert(found.CloudSaves==3&&found.LocalProfiles==1,$"Steam-Cloud-Spielstände falsch gezählt: {found.CloudSaves} Cloud, {found.LocalProfiles} lokal.");
                Assert(found.Saves.Count==2&&found.Saves.Any(s=>s.Path==local&&s.Kind=="local")&&found.Saves.All(s=>!SteamCloud.IsCloud(s.Path)),"Steam-Cloud-Spielstand gelistet oder lokaler Spielstand fehlt.");
                Assert(SteamCloud.IsCloud(cloud)&&SteamCloud.IsCloud(mirror)&&SteamCloud.IsCloud(Path.Combine(remote,"X","save","9","game.sii"))&&!SteamCloud.IsCloud(local)&&!SteamCloud.IsCloud(file),"Steam-Cloud-Pfad falsch erkannt.");
                string other=Path.Combine(root,"other-steam","app","remote","p","game.sii");Directory.CreateDirectory(Path.GetDirectoryName(other)!);File.WriteAllText(Path.Combine(root,"other-steam","app","remotecache.vdf"),"x");
                Assert(SteamCloud.IsCloud(other),"Ordner mit remotecache.vdf nicht als Steam Cloud erkannt.");
                var before=new[]{steam,Path.Combine(documents,"steam_profiles")}.SelectMany(d=>Directory.GetFiles(d,"*",SearchOption.AllDirectories)).ToDictionary(f=>f,File.ReadAllBytes);
                void Refused(Action action){try{action();}catch(IOException e) when(e.Message==L.T("core.cloud")){return;}throw new Exception("Schreiben in die Steam Cloud nicht abgelehnt.");}
                foreach(string path in new[]{cloud,mirror})
                {
                    var session=SaveSession.Open(path);session.Edit(d=>d.Set(d.Bank!.Id,"money_account","123456789012"));
                    Refused(()=>session.Save());Refused(()=>session.CommitEdits());Refused(()=>session.SaveBytes(Encoding.UTF8.GetBytes(fixture),fixture));
                    Assert(session.Dirty&&session.CanUndo,"Abgelehnter Steam-Cloud-Schreibversuch hat die Bearbeitung verworfen.");
                }
                var localSession=SaveSession.Open(local);localSession.Edit(d=>d.Set(d.Bank!.Id,"money_account","4242"));
                string copyTarget=Path.Combine(remote,"594956414E49","save","werkstatt-copy");Refused(()=>localSession.ExportCopy(copyTarget));Assert(!Directory.Exists(copyTarget),"Kopie in der Steam Cloud angelegt.");
                string backup=localSession.CommitEdits();var info=SaveSession.Backups().Single(b=>b.Folder==backup);
                Refused(()=>SaveSession.Restore(info with{Source=cloud}));
                string controls=Path.Combine(root,"cloud-controls-backup");string settings=Path.Combine(controls,"Einstellungen");Directory.CreateDirectory(settings);byte[] mine=Encoding.UTF8.GetBytes("my bindings");
                File.WriteAllBytes(Path.Combine(settings,"06-controls.sii"),mine);File.WriteAllText(Path.Combine(settings,"Dateien.json"),JsonSerializer.Serialize(new[]{new{source=cloudControls,copy="06-controls.sii",sha256=SaveSession.Hash(mine)}}));
                Refused(()=>SettingsBackup.RestoreControls(controls));
                var after=new[]{steam,Path.Combine(documents,"steam_profiles")}.SelectMany(d=>Directory.GetFiles(d,"*",SearchOption.AllDirectories)).ToList();
                Assert(after.Count==before.Count&&before.All(f=>File.Exists(f.Key)&&File.ReadAllBytes(f.Key).SequenceEqual(f.Value)),"Datei in der Steam Cloud verändert oder angelegt.");
                Assert(SaveSession.Backups().All(b=>!SteamCloud.IsCloud(b.Source))&&!Directory.EnumerateDirectories(SaveSession.BackupRoot).Any(d=>Path.GetFileName(d).StartsWith("controls-",StringComparison.Ordinal)),"Für einen abgelehnten Steam-Cloud-Schreibversuch wurde ein Backup angelegt.");
            }
            finally{SaveSession.GameProbe=probe;SaveSession.DataRoot=oldRoot;Installation.TestRoots=null;}
        });
        Check("Key bindings restore from a backup and keep the current file as a safety copy",()=>
        {
            var probe=SaveSession.GameProbe;var oldRoot=SaveSession.DataRoot;SaveSession.DataRoot=Path.Combine(root,"ControlsData");
            try
            {
                string target=Path.Combine(root,"profile-controls","controls.sii");Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.WriteAllText(target,"SiiNunit\n{\n reset\n}\n");
                string backup=Path.Combine(root,"controls-backup");string settings=Path.Combine(backup,"Einstellungen");Directory.CreateDirectory(settings);
                byte[] good=Encoding.UTF8.GetBytes("SiiNunit\n{\n mine\n}\n");File.WriteAllBytes(Path.Combine(settings,"06-controls.sii"),good);
                File.WriteAllText(Path.Combine(settings,"Dateien.json"),JsonSerializer.Serialize(new[]{new{source=target,copy="06-controls.sii",sha256=SaveSession.Hash(good)}}));
                SaveSession.GameProbe=()=>true;bool blocked=false;try{SettingsBackup.RestoreControls(backup);}catch(IOException){blocked=true;}
                Assert(blocked&&File.ReadAllText(target).Contains("reset"),"Steuerung bei laufendem ETS2 ersetzt.");
                SaveSession.GameProbe=()=>false;string safety=SettingsBackup.RestoreControls(backup);
                Assert(File.ReadAllBytes(target).SequenceEqual(good)&&File.ReadAllText(Path.Combine(safety,"00-controls.sii")).Contains("reset"),"Steuerung nicht wiederhergestellt oder nicht gesichert.");
                File.WriteAllText(Path.Combine(settings,"06-controls.sii"),"tampered");Assert(SettingsBackup.Controls(backup).Count==0,"Beschädigte Steuerungssicherung akzeptiert.");
            }
            finally{SaveSession.GameProbe=probe;SaveSession.DataRoot=oldRoot;}
        });
        Check("Save names come from info.sii, unnamed slots get readable labels",()=>
        {
            string profile=Path.Combine(root,"named-saves","save");
            string Slot(string folder,string? name)
            {
                string dir=Path.Combine(profile,folder);Directory.CreateDirectory(dir);string game=Path.Combine(dir,"game.sii");File.WriteAllText(game,fixture);
                if(name!=null)File.WriteAllText(Path.Combine(dir,"info.sii"),"SiiNunit\n{\nsave_container : _nameless.1 {\n name: "+name+"\n file_time: 1\n}\n}\n");
                return Installation.SaveTitle(game);
            }
            Assert(Slot("1","\"Garage 2\"")=="Garage 2"&&Slot("7","Haupt")=="Haupt","Spielstandname nicht gelesen.");
            Assert(Slot("2","\"\"")==L.F("slot.number",2)&&Slot("autosave_job_3","\"\"")==L.T("slot.autosave_job")+" 3"&&Slot("quicksave",null)==L.T("slot.quicksave"),"Unbenannte Slots ohne lesbaren Namen.");
            File.WriteAllText(Path.Combine(profile,"1","info.sii"),"broken");Assert(Installation.SaveTitle(Path.Combine(profile,"1","game.sii"))==L.F("slot.number",1),"Beschädigte info.sii nicht abgefangen.");
            Assert(Countries.Name("germany").Length>0&&Countries.Name("uk")!="Uk"&&Countries.Name("new_land")=="New Land","Ländernamen falsch.");
        });
        Check("Steam folders written with / and \\ count once, so every save is listed once",()=>
        {
            Assert(string.Equals(Installation.Normalize("c:/program files (x86)/steam"),Installation.Normalize(@"C:\Program Files (x86)\Steam\"),StringComparison.OrdinalIgnoreCase),"Steam-Pfade werden nicht vereinheitlicht.");
            var saves=Installation.Detect().Saves;Assert(saves.Select(s=>Installation.Normalize(s.Path).ToUpperInvariant()).Distinct().Count()==saves.Count,"Spielstände doppelt gelistet.");
            Assert(!SaveSession.HasTruckersMpContent(new SiiDocument(fixture)),"TruckersMP-Inhalt fälschlich erkannt.");
        });
        Check("Broken text in a save or info.sii is reported, never thrown as a crash",()=>
        {
            var latin1=Encoding.Latin1.GetBytes("SiiNunit\n{\nsave_container : _nameless.1 {\n name: \"Müll\"\n}\n}\n");
            bool typed=false;try{SaveSession.DecodeBytes(latin1);}catch(InvalidDataException){typed=true;}Assert(typed,"Ungültiges UTF-8 nicht als Formatfehler gemeldet.");
            string dir=Path.Combine(root,"broken-info","save","3");Directory.CreateDirectory(dir);File.WriteAllText(Path.Combine(dir,"game.sii"),fixture);File.WriteAllBytes(Path.Combine(dir,"info.sii"),latin1);
            Assert(Installation.SaveTitle(Path.Combine(dir,"game.sii"))==L.F("slot.number",3),"Beschädigte info.sii bricht die Spielstandsuche ab.");
        });
        Check("Error messages follow the interface language",()=>
        {
            string before=L.Code;
            try
            {
                foreach(var language in L.Languages)
                {
                    L.Use(language.Code);
                    string Message(Action a){try{a();}catch(InvalidDataException e){return e.Message;}return "";}
                    Assert(Message(()=>new SiiDocument("nope"))==L.T("core.notsii")&&Message(()=>new SiiDocument(fixture).Find("x.y"))==L.F("core.missingentry","x.y")&&Message(()=>new SiiDocument("SiiNunit\n{\n a : b {\n k: 1\n k: 2\n}\n}\n")).StartsWith(L.T("core.parse")[..12],StringComparison.Ordinal),"Fehlermeldung nicht übersetzt: "+language.Code);
                }
            }
            finally{L.Use(before);}
        });
        Check("Save picker orders manual saves, quick save, autosaves, multiplayer",()=>
        {
            string Slot(string folder)=>Path.Combine(root,"p","save",folder,"game.sii");
            var order=new[]{"autosave_job_3","multi","quicksave","2","autosave","Haupt","autosave_drive_1"}.OrderBy(f=>MainWindow.SlotKind(Slot(f))).Select(f=>MainWindow.SlotKind(Slot(f))).ToArray();
            Assert(order.SequenceEqual(new[]{0,0,1,2,2,2,3})&&MainWindow.SlotKind(Slot("Quicksave"))==1,"Spielstandarten falsch sortiert.");
        });
        if(Option("--sample") is {} sample)
        {
            var bytes=File.ReadAllBytes(sample);var hash=SaveSession.Hash(bytes);string copy=Path.Combine(root,"real-copy.sii");File.WriteAllBytes(copy,bytes);
            Check("Real encrypted save decrypts losslessly",()=>{var real=SaveSession.Open(copy);Assert(real.Document.Text==real.Original.Text&&real.Document.Economy!=null,"Echter Save nicht lesbar.");real.Edit(d=>d.Set(d.Bank!.Id,"money_account","12345678"));Assert(real.Changes().Count==1,"Echte Geldänderung verändert mehr als ein Feld.");var garage=real.Document.Units.First(g=>g.Type=="garage"&&g.Number("status")==0);real.Garage(garage.Id,2);real.Garage(garage.Id,3);real.Garage(garage.Id,0);Assert(real.Document.Find(garage.Id).Number("vehicles")==0,"Garagen-Roundtrip fehlgeschlagen.");});
            Check("Source save remains byte exact",()=>Assert(SaveSession.Hash(File.ReadAllBytes(sample))==hash,"Echter Originalsave wurde verändert."));
            Check("Real status-6 starter garage retains assignments on upgrade and downgrade",()=>
            {
                var real=SaveSession.Open(copy);var starter=real.Document.Units.FirstOrDefault(g=>g.Type=="garage"&&g.Number("status")==6);
                if(starter==null)return;
                var fields=starter.Fields.ToDictionary(f=>f.Key,f=>f.Value);
                real.Garage(starter.Id,3);real.Garage(starter.Id,6);
                var restored=real.Document.Find(starter.Id);
                Assert(fields.Count==restored.Fields.Count&&fields.All(f=>restored.Get(f.Key)==f.Value),"Startgarage oder Belegung verändert.");
            });
            Check("Repair, refuel and garage bulk edits change only owned vehicles and garages in a binary save",()=>
            {
                string fresh=Path.Combine(root,"service-copy.sii");File.WriteAllBytes(fresh,bytes);var real=SaveSession.Open(fresh);
                var owned=SaveSession.OwnedVehicleIds(real.Document);Assert(owned.Count>0&&owned.All(id=>real.Document.Find(id).Type is "vehicle" or "trailer"),"Eigene Fahrzeuge nicht gefunden.");
                var others=real.Document.Units.Where(u=>u.Type is "vehicle" or "trailer"&&!owned.Contains(u.Id)).ToDictionary(u=>u.Id,u=>u.Fields.Select(f=>(f.Key,f.Value)).ToList());
                var seats=owned.ToDictionary(id=>id,id=>real.Document.Find(id).Fields.Where(f=>f.Key.StartsWith("user_")).Select(f=>(f.Key,f.Value)).ToList());
                Assert(real.Service(owned,true,true,true)>0&&real.Service(owned,true,true,true)==0,"Reparatur nicht idempotent.");
                int garages=real.UpgradeGarages(true);Assert(garages>0&&real.Document.Units.Where(u=>u.Type=="garage").All(g=>g.Number("status")==3?g.Number("vehicles")==5&&g.Number("drivers")==5:true),"Garagen nicht vollständig ausgebaut.");
                var oldRoot=SaveSession.DataRoot;SaveSession.DataRoot=Path.Combine(root,"ServiceData");
                try{real.CommitEdits();}finally{SaveSession.DataRoot=oldRoot;}
                var reopened=SaveSession.Open(fresh);
                foreach(var id in owned)
                {
                    var u=reopened.Document.Find(id);
                    Assert(u.Fields.Where(f=>f.Key.Contains("wear")&&(f.Key.Contains('[')||!f.Key.StartsWith("wheels_wear"))).All(f=>SaveSession.FloatValue(f.Value)==0)&&(u.Type!="vehicle"||SaveSession.FloatValue(u.Get("fuel_relative"))==1),"Verschleiß oder Tank nicht zurückgesetzt: "+id);
                    Assert(u.Fields.Where(f=>f.Key.StartsWith("user_")).Select(f=>(f.Key,f.Value)).SequenceEqual(seats[id]),"Sitz-, Spiegel- oder Lenkradwerte verändert.");
                }
                Assert(others.All(o=>reopened.Document.Find(o.Key).Fields.Select(f=>(f.Key,f.Value)).SequenceEqual(o.Value)),"Händlerfahrzeuge verändert.");
                Assert(reopened.Document.Units.Count==real.Original.Units.Count&&reopened.Document.Units.Count(u=>u.Type=="garage"&&u.Number("status")==3)>=garages,"Garagen nach dem Speichern verloren.");
            });
            Check("Max out sets money, level, skills, ADR, every garage, every known city, dealer and agency in one undo step and survives saving",()=>
            {
                string fresh=Path.Combine(root,"maxout-copy.sii");File.WriteAllBytes(fresh,bytes);var real=SaveSession.Open(fresh);
                var vehicles=real.Document.Units.Where(u=>u.Type is "vehicle" or "trailer").ToDictionary(u=>u.Id,u=>u.Fields.Select(f=>(f.Key,f.Value)).ToList());
                var slots=real.Document.Units.Where(u=>u.Type=="garage"&&u.Number("status")>0).ToDictionary(u=>u.Id,u=>u.Fields.Where(f=>f.Key.Contains('[')).Select(f=>(f.Key,f.Value)).ToList());
                var known=real.KnownCities();var unvisited=known.Except(real.VisitedCities()).ToHashSet();var stops=real.BusStops();long offences=real.Offences();
                int dealers=known.Except(real.UnlockedDealers()).Count(),agencies=known.Except(real.UnlockedRecruitments()).Count();
                var result=real.MaxOut();Assert(result.Any&&result.Garages>0&&result.Cities==unvisited.Count&&unvisited.Count>0,"Max-Out hat nichts geändert oder nicht alle Städte besucht.");
                Assert(dealers>0&&agencies>0&&result.Dealers==dealers&&result.Recruitments==agencies,"Max-Out hat Händler oder Agenturen nicht freigeschaltet.");
                void Verify(SaveSession s)
                {
                    var e=s.Document.Economy!;
                    Assert(s.Document.Bank!.Number("money_account")==long.MaxValue&&e.Number("experience_points")==int.MaxValue&&SaveSession.SkillKeys.All(k=>e.Get(k)==null||e.Number(k)==6)&&(e.Get("adr")==null||e.Number("adr")==63),"Geld, Level oder Fähigkeiten nicht am Maximum.");
                    Assert(s.Document.Units.Where(u=>u.Type=="garage").All(g=>g.Number("status")==3&&g.Number("vehicles")==5&&g.Number("drivers")==5),"Nicht jede Garage ist groß.");
                    Assert(slots.All(g=>g.Value.All(f=>f.Key.Split('[')[0] is not ("vehicles" or "drivers")||s.Document.Find(g.Key).Get(f.Key)==f.Value)),"Belegte Garagenplätze verschoben.");
                    Assert(vehicles.All(v=>s.Document.Find(v.Key).Fields.Select(f=>(f.Key,f.Value)).SequenceEqual(v.Value)),"Fahrzeuge verändert.");
                    Assert(known.All(s.VisitedCities().Contains)&&s.Document.Units.Where(u=>u.Type=="company"&&u.Id.Split('.') is [_,_,_,var city]&&unvisited.Contains(city)).All(u=>u.Get("discovered")=="true"),"Nicht jede Stadt besucht oder Firmen nicht entdeckt.");
                    Assert(known.All(s.UnlockedDealers().Contains)&&known.All(s.UnlockedRecruitments().Contains)&&s.BusStops()==stops&&s.Offences()==offences,"Händler oder Agenturen fehlen, oder Busbahnhöfe und Strafregister wurden verändert.");
                }
                Verify(real);Assert(real.MaxOut() is {Any:false},"Max-Out nicht idempotent.");
                real.Undo();Assert(real.Document.Text==real.Original.Text,"Max-Out ist nicht ein einzelner Rückgängig-Schritt.");real.Redo();
                var oldRoot=SaveSession.DataRoot;SaveSession.DataRoot=Path.Combine(root,"MaxOutData");
                try{real.CommitEdits();}finally{SaveSession.DataRoot=oldRoot;}
                Verify(SaveSession.Open(fresh));
            });
            Check("Country-scoped garage buying changes only the chosen garages",()=>
            {
                var real=SaveSession.Open(copy);var scope=real.Document.Units.Where(u=>u.Type=="garage"&&u.Number("status")==0).Take(4).Select(u=>u.Id).ToHashSet();
                var before=real.Document.Units.Where(u=>u.Type=="garage"&&!scope.Contains(u.Id)).ToDictionary(u=>u.Id,u=>u.Fields.Select(f=>(f.Key,f.Value)).ToList());
                Assert(scope.Count>0&&real.UpgradeGarages(true,scope)==scope.Count,"Ausgewählte Garagen nicht gekauft.");
                Assert(scope.All(id=>real.Document.Find(id).Number("status")==3)&&before.All(g=>real.Document.Find(g.Key).Fields.Select(f=>(f.Key,f.Value)).SequenceEqual(g.Value)),"Garagen außerhalb des Landes verändert.");
                Assert(real.UpgradeGarages(true,scope)==0,"Länderkauf nicht idempotent.");
            });
            Check("Buying by size never shrinks a garage and keeps every slot",()=>
            {
                var real=SaveSession.Open(copy);var before=real.Document.Units.Where(u=>u.Type=="garage").ToDictionary(u=>u.Id,u=>(Status:u.Number("status"),Slots:u.Fields.Where(f=>f.Key.Contains('[')).Select(f=>(f.Key,f.Value)).ToList()));
                int tiny=real.UpgradeGarages(true,null,6);
                Assert(tiny>0&&real.Document.Units.Where(u=>u.Type=="garage").All(g=>before[g.Id].Status==0?g.Number("status")==6&&g.Number("vehicles")==1&&g.Number("drivers")==1:g.Number("status")==before[g.Id].Status),"Mini-Kauf falsch oder bestehende Garage verändert.");
                int small=real.UpgradeGarages(false,null,2);
                Assert(small>0&&real.Document.Units.Where(u=>u.Type=="garage").All(g=>g.Number("status")==(before[g.Id].Status==3?3:2)&&g.Number("vehicles")==SaveSession.GarageSlots(g.Number("status"))),"Ausbau auf klein falsch oder große Garage verkleinert.");
                Assert(before.Where(b=>b.Value.Status>0).All(b=>b.Value.Slots.All(f=>f.Key.Split('[')[0] is not ("vehicles" or "drivers")||real.Document.Find(b.Key).Get(f.Key)==f.Value)),"Garagenbelegung verschoben.");
                Assert(real.UpgradeGarages(true,null,2)==0&&real.UpgradeGarages(true,null,6)==0,"Kleinere Stufe hat Garagen geändert.");
                Assert(SaveSession.HasTruckersMpContent(real.Original),"TruckersMP-Inhalt im echten Spielstand nicht erkannt.");
            });
            Check("Real save shows its in-game name",()=>{string title=Installation.SaveTitle(sample);Assert(title.Length>0&&!title.Contains('"'),"Spielstandname fehlt.");});
            Check("A negative balance (debt) is written to a binary save and read back",()=>
            {
                string fresh=Path.Combine(root,"debt-copy.sii");File.WriteAllBytes(fresh,bytes);var real=SaveSession.Open(fresh);
                real.Edit(d=>d.Set(d.Bank!.Id,"money_account","-250000"));Assert(real.Changes().Count==1,"Schulden ändern mehr als ein Feld.");
                var oldRoot=SaveSession.DataRoot;SaveSession.DataRoot=Path.Combine(root,"DebtData");
                try{real.CommitEdits();}finally{SaveSession.DataRoot=oldRoot;}
                Assert(SaveSession.Open(fresh).Document.Bank!.Number("money_account")==-250000,"Negativer Kontostand nicht gespeichert.");
            });
            Check("Real encrypted copy writes and reloads without losing unknown fields",()=>
            {
                var real=SaveSession.Open(copy);real.Edit(d=>d.Set(d.Bank!.Id,"money_account","12345678"));
                var oldRoot=SaveSession.DataRoot;SaveSession.DataRoot=Path.Combine(root,"AppData");
                try{real.CommitEdits();var reopened=SaveSession.Open(copy);Assert(reopened.Document.Text==real.Document.Text&&reopened.Document.Units.Count==real.Original.Units.Count,"Echter Save verlor Daten beim Schreiben.");}
                finally{SaveSession.DataRoot=oldRoot;}
            });
        }
        foreach(var kind in new[]{"profile","info"})if(Option("--"+kind) is {} metadata)
            Check($"Current encrypted {kind}.sii opens without external DLLs",()=>
            {
                var bytes=File.ReadAllBytes(metadata);string copy=Path.Combine(root,kind+".sii");File.WriteAllBytes(copy,bytes);
                var document=SaveSession.Open(copy);Assert(document.Document.Units.Count>0&&!document.Dirty,"Metadaten nicht vollständig lesbar.");
                Assert(SaveSession.Hash(File.ReadAllBytes(copy))==SaveSession.Hash(bytes),"Lesen hat Metadaten verändert.");
            });
        if(Option("--repair-sample") is {} rejectedSave)Check("Rejected raw BSII save converts to text without changing any field",()=>
        {
            byte[] before=File.ReadAllBytes(rejectedSave);string output=Path.Combine(root,"repaired-game.sii");var expected=SaveSession.DecodeBytes(before);
            SaveSession.ConvertCopy(rejectedSave,output);Assert(File.ReadAllBytes(output).AsSpan().StartsWith("SiiNunit"u8)&&SaveSession.Decode(output)==expected,"Reparatur hat Werte verändert.");
            Assert(SaveSession.Hash(File.ReadAllBytes(rejectedSave))==SaveSession.Hash(before),"Reparatur hat die Quelldatei überschrieben.");
            bool blocked=false;try{SaveSession.ConvertCopy(rejectedSave,output);}catch(IOException){blocked=true;}Assert(blocked,"Vorhandene Zieldatei überschrieben.");
        });
        if(Option("--render") is {} render && Option("--sample") is {} sampleForUi)
            Check("All UI pages render offscreen in two window sizes",()=>UiVerification.Render(render,sampleForUi,GameMap.Cities(Installation.Detect())));
        File.WriteAllText(report,JsonSerializer.Serialize(new{passed=true,checks,seconds=watch.Elapsed.TotalSeconds},new JsonSerializerOptions{WriteIndented=true}));
    }
}
