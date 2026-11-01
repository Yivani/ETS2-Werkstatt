using System.Buffers.Binary;
using System.Text;
using TruckLib.HashFs;

namespace Werkstatt;
internal static class IntegrityTest
{
    public static void Run(string root,Action<string,Action> check,string? sample,string? export)
    {
        void Assert(bool value,string message){if(!value)throw new InvalidDataException(message);}
        void Reject(Action action){try{action();}catch(InvalidDataException){return;}throw new Exception("Ungültige Datei oder Änderung akzeptiert.");}
        var oldRoot=SaveSession.DataRoot;SaveSession.DataRoot=Path.Combine(root,"integrity-backups");
        try
        {
            var (fixture,moneyOffset)=Fixture(3);
            string profile=Path.Combine(root,"profiles","INTEGRITY-TEST"),folder=Path.Combine(profile,"save","1");Directory.CreateDirectory(folder);
            string file=Path.Combine(folder,"game.sii");File.WriteAllBytes(file,fixture);
            string controls=Path.Combine(profile,"controls.sii"),config=Path.Combine(profile,"config_local.cfg");
            File.WriteAllText(controls,"wheel bindings + force feedback: keep exactly\r\n");File.WriteAllText(config,"steering sensitivity: keep exactly\r\n");
            var originals=new[]{controls,config}.ToDictionary(p=>p,p=>File.ReadAllBytes(p));
            check("Regression: ordinal tables belong to each field, not to their unit",()=>
            {
                var doc=SaveSession.Open(file).Document;var cargo=doc.Find("cargo.test");
                Assert(cargo.Get("lashing_method")=="none"&&cargo.Get("lashing_gear_type")=="strap"&&cargo.Get("cargo_area")=="area_cont_40ft","Enumwerte wurden mit der Tabelle eines anderen Felds dekodiert.");
                var unknown=Fixture(3,true).Bytes;Assert(new SiiDocument(SaveSession.DecodeBytes(unknown)).Find("cargo.test").Get("unrecognized_enum")=="99","Unbekannter Enumwert verloren.");Reject(()=>{_ = new BinarySii(unknown).CompatibleText;});
            });
            check("Binary versions 1, 2 and 3 retain wheel and cabin values",()=>
            {
                foreach(uint version in new uint[]{1,2,3})
                {
                    var (data,_)=Fixture(version);var doc=new SiiDocument(SaveSession.DecodeBytes(data));
                    Assert(doc.Find("wheel.test").Get("cabin_position")=="(&3f800000, &40000000, &40400000) (&3f800000; &00000000, &00000000, &00000000)","Kabinenposition falsch dekodiert.");
                    Assert(doc.Find("wheel.test").Get("steering_adjustment")=="&80000000","Float-Bitmuster verändert.");
                    Assert(SiiDocument.Display(doc.Find("wheel.test").Get("mod_data"))=="quote \" and slash \\ and ä","String-Escapes verändert.");
                }
            });
            check("Bare BSII backup restore uses compatible text, preserving all values",()=>
            {
                byte[] converted=SaveSession.CompatibleBytes(fixture);Assert(converted.AsSpan().StartsWith("SiiNunit"u8)&&SaveSession.DecodeBytes(converted)==SaveSession.DecodeBytes(fixture),"Backup-Ausgabe bleibt im abgelehnten Rohformat oder verändert Werte.");
                var text=Encoding.UTF8.GetBytes(SaveSession.DecodeBytes(fixture));Assert(SaveSession.CompatibleBytes(text).SequenceEqual(text),"Bereits kompatiblen Text verändert.");
                string path=Path.Combine(root,"no-edit-repair.sii");File.WriteAllBytes(path,fixture);var session=SaveSession.Open(path);Assert(session.NeedsFormatConversion&&!session.Dirty,"Formatreparatur nicht separat erkannt.");session.CommitEdits();Assert(!session.NeedsFormatConversion&&!session.Dirty&&File.ReadAllBytes(path).SequenceEqual(converted),"Formatreparatur ohne Wertänderung fehlgeschlagen.");
            });
            check("Money patch changes exactly eight internal binary bytes and writes compatible text",()=>
            {
                var session=SaveSession.Open(file);session.Edit(d=>d.Set(d.Bank!.Id,"money_account","12345678"));
                var expected=fixture.ToArray();BinaryPrimitives.WriteInt64LittleEndian(expected.AsSpan(moneyOffset),12345678);
                Assert(new BinarySii(fixture).Patch(session.Original,session.Document).SequenceEqual(expected),"Binärbytes außerhalb des Geldfeldes verändert.");
                string backup=session.CommitEdits();
                Assert(File.ReadAllBytes(file).AsSpan().StartsWith("SiiNunit"u8)&&SaveSession.Decode(file)==new BinarySii(expected).Text,"Ausgabe ist kein vollständiger kompatibler Textsave.");
                Assert(File.ReadAllBytes(Path.Combine(backup,"game.sii")).SequenceEqual(fixture),"Backup nicht bytegleich.");
                Assert(originals.All(p=>File.ReadAllBytes(p.Key).SequenceEqual(p.Value)),"Steuerungsdatei verändert.");
                var copies=Directory.GetFiles(Path.Combine(backup,"Einstellungen"));
                Assert(originals.All(p=>copies.Any(f=>File.ReadAllBytes(f).SequenceEqual(p.Value))),"Steuerung/FFB-Konfiguration nicht gesichert.");
            });
            check("Unselected binary field spans survive garage arrays and headquarters changes",()=>
            {
                File.WriteAllBytes(file,fixture);
                var session=SaveSession.Open(file);var before=File.ReadAllBytes(file);var prior=new BinarySii(before);
                session.Garage("garage.berlin",3);session.Headquarters("garage.berlin");session.Edit(d=>d.Set(d.Economy!.Id,"experience_points","21600"));
                var after=prior.Patch(session.Original,session.Document);var next=new BinarySii(after);var indexed=next.Fields.ToDictionary(f=>(f.Unit,f.Name));
                foreach(var f in prior.Fields.Where(f=>!(f.Unit=="garage.berlin"&&f.Name is "vehicles" or "drivers" or "status")&&!(f.Unit=="economy.test"&&f.Name=="experience_points")))
                {var n=indexed[(f.Unit,f.Name)];Assert(before.AsSpan(f.Start,f.Length).SequenceEqual(after.AsSpan(n.Start,n.Length)),"Unbeteiligtes Binärfeld verändert: "+f.Unit+"/"+f.Name);}
                session.CommitEdits();Assert(File.ReadAllBytes(file).AsSpan().StartsWith("SiiNunit"u8)&&SaveSession.Decode(file)==next.Text,"Textausgabe verändert Werte.");
                session.Garage("garage.berlin",6);session.CommitEdits();
                Assert(SaveSession.Open(file).Document.Find("garage.berlin").Get("vehicles[0]")=="wheel.test","Garagenbelegung verloren.");
            });
            check("Protected wheel, cargo and mod fields cannot be written",()=>
            {
                foreach(var (unit,key,value) in new[]{("wheel.test","steering_adjustment","&00000000"),("cargo.test","lashing_method","top_over"),("wheel.test","mod_data","changed")})
                {
                    var session=SaveSession.Open(file);var before=File.ReadAllBytes(file);session.Edit(d=>d.Set(unit,key,value));Reject(()=>session.CommitEdits());
                    Assert(File.ReadAllBytes(file).SequenceEqual(before),"Geschütztes Feld auf Platte geschrieben.");
                }
            });
            check("Known legacy cargo corruption is detected and cannot be saved again",()=>
            {
                string path=Path.Combine(root,"legacy-corrupt.sii");string text=SaveSession.DecodeBytes(fixture).Replace("lashing_method: none","lashing_method: area_cont_40ft");File.WriteAllText(path,text);
                var session=SaveSession.Open(path);Assert(session.IntegrityWarning!=null,"Bekannte Beschädigung nicht erkannt.");session.Edit(d=>d.Set(d.Bank!.Id,"money_account","42"));Reject(()=>session.CommitEdits());Assert(File.ReadAllText(path)==text,"Beschädigten Legacy-Save überschrieben.");
            });
            check("Unknown binary types, versions, truncation and trailing data fail closed",()=>
            {
                var badVersion=fixture.ToArray();BinaryPrimitives.WriteUInt32LittleEndian(badVersion.AsSpan(4),4);Reject(()=>SaveSession.DecodeBytes(badVersion));
                var badType=fixture.ToArray();int name=Encoding.ASCII.GetString(badType).IndexOf("steering_adjustment",StringComparison.Ordinal);BinaryPrimitives.WriteUInt32LittleEndian(badType.AsSpan(name-8),0xabcdef);Reject(()=>SaveSession.DecodeBytes(badType));
                foreach(int length in new[]{0,7,fixture.Length-1,fixture.Length-9})Reject(()=>SaveSession.DecodeBytes(fixture[..length]));
                Reject(()=>SaveSession.DecodeBytes(fixture.Concat(new byte[]{1}).ToArray()));
            });
            check("Plain text edits preserve BOM, line endings and unrelated comments",()=>
            {
                string text="SiiNunit\r\n{\r\nbank : bank.test {\r\n money_account: 1000 # keep comment\r\n mod_setting: \"preserve { wheel }\"\r\n}\r\n}\r\n";
                byte[] bytes=new byte[]{239,187,191}.Concat(Encoding.UTF8.GetBytes(text)).ToArray();string path=Path.Combine(root,"text-preservation.sii");File.WriteAllBytes(path,bytes);
                var session=SaveSession.Open(path);session.Edit(d=>d.Set("bank.test","money_account","2000"));session.CommitEdits();
                Assert(File.ReadAllBytes(path).SequenceEqual(new byte[]{239,187,191}.Concat(Encoding.UTF8.GetBytes(text.Replace("1000","2000")))),"Textformat oder unbeteiligte Bytes verändert.");
            });
            if(sample!=null)check("Original encrypted save: internal spans stay exact and final output is SiiNunit",()=>
            {
                byte[] original=File.ReadAllBytes(sample),payload=SaveSession.Unwrap(original);Assert(payload.AsSpan().StartsWith("BSII"u8),"Für diesen Integritätstest wird ein Original-BSII benötigt.");
                string path=Path.Combine(root,"binary-original-copy.sii");File.WriteAllBytes(path,original);var session=SaveSession.Open(path);var index=new BinarySii(payload);
                session.Edit(d=>d.SetMany([(d.Bank!.Id,"money_account","12345678"),(d.Economy!.Id,"experience_points","21600")]));
                var garage=session.Document.Units.First(g=>g.Type=="garage"&&g.Number("status")==0);session.Garage(garage.Id,3);session.Headquarters(garage.Id);
                var approved=session.Changes().Select(c=>(c.Unit,c.Field.Split('[')[0])).ToHashSet();byte[] result=index.Patch(session.Original,session.Document);session.CommitEdits();
                var output=new BinarySii(result);var spans=output.Fields.ToDictionary(f=>(f.Unit,f.Name));int preserved=0;
                foreach(var f in index.Fields.Where(f=>!approved.Contains((f.Unit,f.Name))))
                {var n=spans[(f.Unit,f.Name)];Assert(payload.AsSpan(f.Start,f.Length).SequenceEqual(result.AsSpan(n.Start,n.Length)),"Echtes unbeteiligtes Feld verändert: "+f.Unit+"/"+f.Name);preserved++;}
                Assert(preserved>10000&&output.Fields.Count==index.Fields.Count,"Unvollständige Binärprüfung.");
                Assert(File.ReadAllBytes(path).AsSpan().StartsWith("SiiNunit"u8)&&SaveSession.Decode(path)==output.CompatibleText,"Finale Datei ist kein äquivalenter SiiNunit-Text.");
                Assert(File.ReadAllBytes(sample).SequenceEqual(original),"Originaldatei wurde verändert.");
                if(export!=null){Directory.CreateDirectory(export);File.WriteAllBytes(Path.Combine(export,"patched.bsii"),result);File.Copy(path,Path.Combine(export,"game.sii"),true);File.WriteAllText(Path.Combine(export,"expected-changes.json"),System.Text.Json.JsonSerializer.Serialize(new{preservedFields=preserved,changes=approved.Select(c=>new{unit=c.Unit,field=c.Item2})}));}
            });
        }
        finally{SaveSession.DataRoot=oldRoot;}
    }
    const string FeatureText="""
SiiNunit
{
economy : _nameless.1 {
 bank: _nameless.2
 player: _nameless.3
 experience_points: 100
 adr: 0
 long_dist: 0
 heavy: 0
 fragile: 0
 urgent: 0
 mechanical: 0
 visited_cities: 1
 visited_cities[0]: berlin
 visited_cities_count: 1
 visited_cities_count[0]: 3
 last_visited_city: berlin
 unlocked_dealers: 1
 unlocked_dealers[0]: berlin
 unlocked_recruitments: 0
 emergency_call_count: 1
 ai_crash_count: 2
 red_light_fine_count: 2
 cancelled_job_count: 1
 police_offence_log: _nameless.20
 police_ctrl: _nameless.21
}
bank : _nameless.2 {
 money_account: 1000
}
player : _nameless.3 {
 hq_city: berlin
 trailers: 1
 trailers[0]: _nameless.9
 trucks: 1
 trucks[0]: _nameless.8
 drivers: 2
 drivers[0]: driver.me
 drivers[1]: driver.hired
}
driver_player : driver.me {
 profit_log: null
}
driver_ai : driver.hired {
 adr: 1
 long_dist: 2
 heavy: 0
 fragile: 6
 urgent: 0
 mechanical: 1
 hometown: berlin
 experience_points: 500
}
driver_ai : driver.pool {
 adr: 0
 long_dist: 0
 heavy: 0
 fragile: 0
 urgent: 0
 mechanical: 0
 hometown: paris
 experience_points: 10
}
vehicle : _nameless.8 {
 fuel_relative: &3f800000
 license_plate: "HB<offset hshift=4 vshift=-5><img src=/material/ui/lp/germany/b_2.mat color=FFFFFFFF><offset hshift=4 vshift=5>TU 37|germany"
 license_plate_type: ""
 odometer: 1534
 odometer_float_part: &3e738a3f
 integrity_odometer: 1534
 integrity_odometer_float_part: &3e738a3f
 trip_fuel_l: 276
 trip_distance_km: 1534
}
trailer : _nameless.9 {
 license_plate: "B DG 2003|_germany"
 slave_trailer: null
 odometer: 927
 odometer_float_part: &00000000
 integrity_odometer: 927
 integrity_odometer_float_part: &3f0b1145
}
vehicle : _nameless.10 {
 license_plate: "XX 1|france"
 odometer: 5
 odometer_float_part: &00000000
 integrity_odometer: 5
 integrity_odometer_float_part: &00000000
}
police_offence_log : _nameless.20 {
 detailed_history_entries: 0
 offence_total_counts: 3
 offence_total_counts[0]: 2
 offence_total_counts[1]: 0
 offence_total_counts[2]: 1
 offence_total_fines: 3
 offence_total_fines[0]: 500
 offence_total_fines[1]: 0
 offence_total_fines[2]: 150
}
police_ctrl : _nameless.21 {
 offence_counter: 1
 offence_counter[0]: 3
}
bus_stop : bus_stop.city.berlin {
 discovered: true
 lines_offer: 0
}
bus_stop : bus_stop.city.paris {
 discovered: false
 lines_offer: 0
}
bus_stop : bus_stop.city.lyon {
 discovered: false
 lines_offer: 0
}
company : company.volatile.acc.berlin {
 permanent_data: company.permanent.acc
 discovered: true
 state: 0
}
company : company.volatile.acc.paris {
 permanent_data: company.permanent.acc
 discovered: false
 state: 0
}
company : company.volatile.tradeaux.paris {
 permanent_data: company.permanent.tradeaux
 discovered: false
 state: 0
}
company : company.volatile.acc.lyon {
 permanent_data: company.permanent.acc
 discovered: false
 state: 0
}
company : company.volatile.acc.truckersmp {
 permanent_data: company.permanent.acc
 discovered: false
 state: 0
}
}

""";
    /// <summary>Visited cities, hired driver skills and license plates on a text fixture, a binary fixture and a copy of a real binary save.</summary>
    public static void Features(string root,Action<string,Action> check,string? sample)
    {
        void Assert(bool value,string message){if(!value)throw new InvalidDataException(message);}
        void Reject(Action action,string? message=null){try{action();}catch(InvalidDataException e){if(message==null||e.Message==message)return;throw new Exception("Falsche Fehlermeldung: "+e.Message);}throw new Exception("Ungültige Änderung akzeptiert.");}
        void Untouched(SaveSession s,params string[] changed)
        {
            foreach(var u in s.Original.Units.Where(u=>!changed.Contains(u.Id))){var n=s.Document.Find(u.Id);Assert(s.Document.Text.AsSpan(n.Start,n.Length).SequenceEqual(s.Original.Text.AsSpan(u.Start,u.Length)),"Unbeteiligter Eintrag verändert: "+u.Id);}
        }
        HashSet<(string,string)> Approved(SaveSession s)=>s.Changes().Select(c=>(c.Unit,c.Field.Split('[')[0])).ToHashSet();
        BinarySii PatchExactly(SaveSession s,byte[] payload,HashSet<(string,string)> approved,bool strict)
        {
            var index=new BinarySii(payload);var result=index.Patch(s.Original,s.Document);var output=new BinarySii(result);var spans=output.Fields.ToDictionary(f=>(f.Unit,f.Name));
            Assert(output.Fields.Count==index.Fields.Count,"Binärfelder verloren oder hinzugefügt.");
            foreach(var f in index.Fields)
            {
                var n=spans[(f.Unit,f.Name)];bool same=payload.AsSpan(f.Start,f.Length).SequenceEqual(result.AsSpan(n.Start,n.Length));
                if(!approved.Contains((f.Unit,f.Name)))Assert(same,"Unbeteiligtes Binärfeld verändert: "+f.Unit+"/"+f.Name);else if(strict)Assert(!same,"Freigegebenes Binärfeld nicht geschrieben: "+f.Unit+"/"+f.Name);
            }
            var decoded=new SiiDocument(output.Text);
            Assert(s.Document.Units.All(u=>decoded.Find(u.Id).Fields.Select(f=>(f.Key,f.Value)).SequenceEqual(u.Fields.Select(f=>(f.Key,f.Value)))),"Binärausgabe weicht von den vorgemerkten Werten ab.");
            return output;
        }
        var oldRoot=SaveSession.DataRoot;SaveSession.DataRoot=Path.Combine(root,"feature-backups");
        try
        {
            string folder=Path.Combine(root,"features");Directory.CreateDirectory(folder);int copies=0;
            string Copy(string text){string path=Path.Combine(folder,$"text-{++copies}.sii");File.WriteAllText(path,text,new UTF8Encoding(false));return path;}
            string visitedText=FeatureText.Replace(" visited_cities: 1\n visited_cities[0]: berlin\n visited_cities_count: 1\n visited_cities_count[0]: 3\n"," visited_cities: 2\n visited_cities[0]: berlin\n visited_cities[1]: paris\n visited_cities_count: 2\n visited_cities_count[0]: 3\n visited_cities_count[1]: 1\n")
                .Replace("acc.paris {\n permanent_data: company.permanent.acc\n discovered: false","acc.paris {\n permanent_data: company.permanent.acc\n discovered: true").Replace("tradeaux.paris {\n permanent_data: company.permanent.tradeaux\n discovered: false","tradeaux.paris {\n permanent_data: company.permanent.tradeaux\n discovered: true");
            check("Text save: visiting cities appends only new known cities with count 1 and discovers just their companies",()=>
            {
                foreach(string newline in new[]{"\n","\r\n"})
                {
                    string path=Copy(FeatureText.Replace("\n",newline));var s=SaveSession.Open(path);
                    Assert(s.KnownCities().SequenceEqual(["berlin","lyon","paris"])&&s.VisitedCities().SequenceEqual(["berlin"]),"Bekannte oder besuchte Städte falsch (TruckersMP-Städte gehören nicht dazu).");
                    Assert(s.VisitCities(["paris","berlin","atlantis","paris","truckersmp"])==1&&s.VisitedCities().SequenceEqual(["berlin","paris"]),"Stadtbesuch falsch angehängt.");
                    Untouched(s,"_nameless.1","company.volatile.acc.paris","company.volatile.tradeaux.paris");Assert(s.Document.Text==visitedText.Replace("\n",newline),"Text außerhalb der Stadtfelder verändert.");
                    Assert(s.VisitCities(["paris"])==0&&s.Undo()&&s.Document.Text==s.Original.Text&&!s.CanUndo&&s.Redo(),"Stadtbesuch ist nicht genau ein Rückgängig-Schritt.");
                    s.CommitEdits();Assert(File.ReadAllText(path)==visitedText.Replace("\n",newline),"Gespeicherte Datei weicht von den vorgemerkten Bytes ab.");
                }
            });
            check("Text save: visited city edits the game never makes are refused",()=>
            {
                (string,string,IReadOnlyList<string>)[] Visits(string[] cities,string[] counts)=>[("_nameless.1","visited_cities",cities),("_nameless.1","visited_cities_count",counts)];
                var cases=new List<Action<SiiDocument>>
                {
                    d=>d.SetArrays(Visits(["berlin","atlantis"],["3","1"]),[]),d=>d.SetArrays(Visits(["paris"],["3"]),[]),d=>d.SetArrays(Visits(["berlin","paris"],["3","0"]),[]),
                    d=>d.SetArrays(Visits(["berlin","paris"],["3"]),[]),d=>d.SetArrays(Visits(["berlin","paris","paris"],["3","1","1"]),[]),d=>d.SetArrays(Visits(["berlin","truckersmp"],["3","1"]),[]),
                    d=>d.SetArrays(Visits(["berlin"],["4"]),[]),d=>d.SetArrays(Visits([],[]),[]),d=>d.Set("company.volatile.acc.lyon","discovered","true"),d=>d.Set("company.volatile.acc.berlin","discovered","false"),
                    d=>{d.SetArrays(Visits(["berlin","paris"],["3","1"]),[]);d.Set("company.volatile.acc.paris","state","1");},d=>d.Set("_nameless.1","last_visited_city","paris"),
                };
                foreach(var edit in cases)
                {
                    string path=Copy(FeatureText);var s=SaveSession.Open(path);s.Edit(edit);Reject(()=>s.CommitEdits());Assert(File.ReadAllText(path)==FeatureText,"Abgelehnte Änderung geschrieben.");
                }
            });
            check("Text save: hired driver skills reach the maximum; the player's driver and the recruitment pool stay unchanged",()=>
            {
                string path=Copy(FeatureText);var s=SaveSession.Open(path);
                Assert(s.HiredDrivers().Select(d=>d.Id).SequenceEqual(["driver.hired"]),"Angestellte Fahrer falsch erkannt.");
                Assert(s.MaxDriverSkills()==1&&s.MaxDriverSkills()==0&&s.Undo()&&!s.CanUndo&&s.Redo(),"Fahrerfähigkeiten nicht idempotent oder nicht ein Rückgängig-Schritt.");
                var d=s.Document.Find("driver.hired");Assert(d.Number("adr")==63&&SaveSession.SkillKeys.All(k=>d.Number(k)==6)&&d.Number("experience_points")==500,"Fahrerfähigkeiten nicht am Maximum.");
                string expected=FeatureText.Replace(" adr: 1\n long_dist: 2\n heavy: 0\n fragile: 6\n urgent: 0\n mechanical: 1\n"," adr: 63\n long_dist: 6\n heavy: 6\n fragile: 6\n urgent: 6\n mechanical: 6\n");
                Untouched(s,"driver.hired");Assert(s.Document.Text==expected,"Text außerhalb der Fahrerfähigkeiten verändert.");
                s.CommitEdits();Assert(File.ReadAllText(path)==expected,"Gespeicherte Datei weicht von den vorgemerkten Bytes ab.");
                foreach(var (unit,key,value) in new[]{("driver.pool","adr","63"),("driver.pool","heavy","6"),("driver.hired","adr","64"),("driver.hired","heavy","7"),("driver.hired","hometown","paris"),("driver.hired","experience_points","9999"),("driver.me","profit_log","driver.pool")})
                {
                    var r=SaveSession.Open(path);byte[] before=File.ReadAllBytes(path);r.Edit(doc=>doc.Set(unit,key,value));Reject(()=>r.CommitEdits());Assert(File.ReadAllBytes(path).SequenceEqual(before),"Abgelehnte Fahreränderung geschrieben.");
                }
            });
            check("Text save: license plates of owned trucks take plain text and keep their country; trailer plates are refused",()=>
            {
                string path=Copy(FeatureText);var s=SaveSession.Open(path);
                Assert(SaveSession.PlateText(s.Document.Find("_nameless.8").Get("license_plate"))=="HB TU 37"&&SaveSession.PlateText("\"B DG 2003|_germany\"")=="B DG 2003"&&SaveSession.PlateText(null)=="","Sichtbarer Kennzeichentext falsch.");
                s.SetLicensePlate("_nameless.8","  WERK 2026 ");
                Assert(s.Document.Find("_nameless.8").Get("license_plate")=="\"WERK 2026|germany\"","Kennzeichen oder Land falsch.");
                Untouched(s,"_nameless.8");string staged=s.Document.Text;
                foreach(string plate in new[]{"AB-12","WERK 1","","ÄÖÜ 1"})Reject(()=>s.SetLicensePlate("_nameless.9",plate),L.T("core.plate.trailer"));
                Assert(s.Document.Text==staged&&s.Document.Find("_nameless.9").Get("license_plate")=="\"B DG 2003|_germany\"","Abgelehntes Anhängerkennzeichen trotzdem vorgemerkt.");
                foreach(string bad in new[]{"","   ","ABCDEFGHIJKLM","ÄÖÜ 1","A|B","A\"B","<b>X</b>","X\\x41","--"})Reject(()=>s.SetLicensePlate("_nameless.8",bad),L.T("core.plate"));
                Reject(()=>s.SetLicensePlate("_nameless.10","DEAL 1"));Reject(()=>s.SetLicensePlate("company.volatile.acc.berlin","X 1"));Reject(()=>s.SetLicensePlate("vehicle.missing","X 1"));
                string expected=FeatureText.Replace("\"HB<offset hshift=4 vshift=-5><img src=/material/ui/lp/germany/b_2.mat color=FFFFFFFF><offset hshift=4 vshift=5>TU 37|germany\"","\"WERK 2026|germany\"");
                Assert(s.Document.Text==expected,"Text außerhalb der Kennzeichen verändert.");s.CommitEdits();Assert(File.ReadAllText(path)==expected,"Gespeicherte Datei weicht von den vorgemerkten Bytes ab.");
                foreach(var (unit,value) in new[]{("_nameless.8","\"AB 1|france\""),("_nameless.8","\"<b>AB</b>|germany\""),("_nameless.8","\"ABCDEFGHIJKLM|germany\""),("_nameless.8","\"AB\\x41|germany\""),("_nameless.10","\"DEAL 1|france\""),("_nameless.9","\"AB 1\""),("_nameless.9","\"AB-12|_germany\"")})
                {
                    var r=SaveSession.Open(path);byte[] before=File.ReadAllBytes(path);r.Edit(doc=>doc.Set(unit,"license_plate",value));Reject(()=>r.CommitEdits(),unit=="_nameless.9"?L.T("core.plate.trailer"):null);Assert(File.ReadAllBytes(path).SequenceEqual(before),"Abgelehntes Kennzeichen geschrieben.");
                }
            });
            check("Text save: every shortcut's dry run predicts its action, and each action is one undo step",()=>
            {
                string path=Copy(FeatureText);var s=SaveSession.Open(path);
                var fresh=SaveSession.Open(Copy(FeatureText));Assert(fresh.MaxOutPending()==fresh.MaxOut()&&fresh.MaxOutPending() is {Any:false},"Max-Out-Vorschau weicht vom Max-Out ab.");
                var owned=SaveSession.OwnedVehicleIds(s.Document).ToList();var trucks=owned.Where(id=>s.Document.Find(id).Type=="vehicle").ToList();
                Assert(s.ServicePending(owned,true,false,true).Count==0&&s.ServicePending(trucks,false,true,false).Count==0&&s.Service(owned,true,true,true)==0&&!s.CanUndo,"Leere Reparatur oder Betankung meldet Arbeit.");
                Assert(s.GaragesPending(true)==0&&s.GaragesPending(false)==0,"Garagen ohne Garagen gemeldet.");
                var actions=new (string Name,Func<int> Pending,Action Act)[]
                {
                    ("money",()=>s.MoneyPending()?1:0,()=>s.MaxMoney()),("level",()=>s.LevelPending()?1:0,()=>s.MaxLevel()),("skills",s.SkillsPending,()=>s.MaxSkills()),
                    ("cities",s.CitiesPending,()=>s.VisitCities(s.KnownCities())),("dealers",s.DealersPending,()=>s.UnlockDealers()),("agencies",s.RecruitmentsPending,()=>s.UnlockRecruitments()),
                    ("drivers",s.DriverSkillsPending,()=>s.MaxDriverSkills()),("bus",s.BusStopsPending,()=>s.DiscoverBusStops()),("record",()=>s.CleanRecordPending(),()=>s.CleanRecord()),
                };
                foreach(var (name,pending,act) in actions)
                {
                    int before=pending();string text=s.Document.Text;Assert(before>0,"Keine offene Arbeit gemeldet: "+name);act();
                    Assert(pending()==0&&s.Document.Text!=text,"Aktion erledigt ihre Vorschau nicht: "+name);
                    Assert(s.Undo()&&s.Document.Text==text&&pending()==before&&s.Redo()&&pending()==0,"Aktion ist nicht genau ein Rückgängig-Schritt: "+name);
                }
                var e=s.Document.Economy!;
                Assert(s.Document.Bank!.Number("money_account")==long.MaxValue&&e.Number("experience_points")==int.MaxValue&&SaveSession.SkillKeys.All(k=>e.Number(k)==6)&&e.Number("adr")==63,"Geld, Level oder Fähigkeiten nicht am Maximum.");
                Assert(s.MaxOutPending() is {Any:false}&&s.MaxOut() is {Any:false}&&s.CleanRecordPending(true)==5&&s.CleanRecordPending()==0,"Max-Out oder Strafregister nach den Einzelaktionen nicht erledigt.");
                Assert(!s.MaxMoney()&&!s.MaxLevel()&&s.MaxSkills()==0&&s.UnlockDealers()==0&&s.UnlockRecruitments()==0,"Einzelaktionen nicht idempotent.");
                s.CommitEdits();var reopened=SaveSession.Open(path);Assert(reopened.Document.Text==s.Document.Text&&!reopened.MaxOutPending().Any,"Einzelaktionen nach dem Speichern verloren.");
            });
            check("Text save: max out also visits every known city in the same undo step",()=>
            {
                string path=Copy(FeatureText);var s=SaveSession.Open(path);var result=s.MaxOut();
                Assert(result.Dealers==2&&result.Recruitments==3&&s.UnlockedDealers().SequenceEqual(["berlin","lyon","paris"])&&s.UnlockedRecruitments().SequenceEqual(["berlin","lyon","paris"])&&s.BusStops()==(1,3)&&s.Offences()==3,"Max-Out schaltet Händler und Agenturen nicht frei oder ändert Busbahnhöfe oder Strafregister.");
                Assert(result.Cities==2&&s.VisitedCities().SequenceEqual(["berlin","lyon","paris"])&&s.Document.Units.Where(u=>u.Type=="company").All(u=>u.Get("discovered")==(u.Id.EndsWith(".truckersmp",StringComparison.Ordinal)?"false":"true")),"Max-Out hat nicht alle Städte besucht.");
                Assert(s.MaxOut() is {Any:false,Cities:0}&&s.Undo()&&s.Document.Text==s.Original.Text&&!s.CanUndo&&s.Redo(),"Max-Out nicht idempotent oder nicht ein Rückgängig-Schritt.");
                Untouched(s,"_nameless.1","_nameless.2","company.volatile.acc.paris","company.volatile.tradeaux.paris","company.volatile.acc.lyon");
                s.CommitEdits();var reopened=SaveSession.Open(path);Assert(reopened.VisitedCities().Count==3&&reopened.Document.Text==s.Document.Text,"Max-Out nach dem Speichern verloren.");
            });
            check("Text save: loading and saving without edits, or with every new edit undone, keeps every byte",()=>
            {
                foreach(bool bom in new[]{false,true})foreach(string newline in new[]{"\n","\r\n"})
                {
                    string path=Path.Combine(folder,$"no-edit-{bom}-{newline.Length}.sii");byte[] bytes=(bom?new byte[]{239,187,191}:[]).Concat(Encoding.UTF8.GetBytes(FeatureText.Replace("\n",newline))).ToArray();File.WriteAllBytes(path,bytes);
                    SaveSession.Open(path).CommitEdits();Assert(File.ReadAllBytes(path).SequenceEqual(bytes),"Speichern ohne Änderung verändert Bytes.");
                    var s=SaveSession.Open(path);s.VisitCities(["paris"]);s.MaxDriverSkills();s.SetLicensePlate("_nameless.8","X 1");
                    s.UnlockDealersAndRecruitments();s.SetOdometer("_nameless.8",7);s.CleanRecord();s.DiscoverBusStops();for(int i=0;i<7;i++)s.Undo();
                    Assert(!s.Dirty&&s.ChangeCount==0,"Rückgängig hinterlässt Änderungen.");s.CommitEdits();Assert(File.ReadAllBytes(path).SequenceEqual(bytes),"Rückgängig gemachte Änderungen verändern Bytes.");
                }
            });
            void Refused(string text,Action<SiiDocument> edit,string what)
            {
                string path=Copy(text);var s=SaveSession.Open(path);
                try{s.Edit(edit);s.CommitEdits();}catch(InvalidDataException){Assert(File.ReadAllText(path)==text,"Abgelehnte Änderung geschrieben: "+what);return;}
                throw new Exception("Ungültige Änderung akzeptiert: "+what);
            }
            string unlockedText=FeatureText.Replace(" unlocked_dealers: 1\n unlocked_dealers[0]: berlin\n unlocked_recruitments: 0\n",
                " unlocked_dealers: 3\n unlocked_dealers[0]: berlin\n unlocked_dealers[1]: lyon\n unlocked_dealers[2]: paris\n unlocked_recruitments: 3\n unlocked_recruitments[0]: berlin\n unlocked_recruitments[1]: lyon\n unlocked_recruitments[2]: paris\n");
            check("Text save: truck dealers and recruitment agencies append every known city once, each call one undo step",()=>
            {
                string path=Copy(FeatureText);var s=SaveSession.Open(path);
                Assert(s.UnlockedDealers().SequenceEqual(["berlin"])&&s.UnlockedRecruitments().Count==0,"Freigeschaltete Händler oder Agenturen falsch gelesen.");
                Assert(s.UnlockDealers()==2&&s.UnlockedDealers().SequenceEqual(["berlin","lyon","paris"])&&s.UnlockDealers()==0,"Händler nicht genau einmal angehängt.");
                Assert(s.UnlockRecruitments()==3&&s.UnlockedRecruitments().SequenceEqual(["berlin","lyon","paris"])&&s.UnlockRecruitments()==0,"Agenturen nicht an die leere Liste angehängt.");
                Untouched(s,"_nameless.1");Assert(s.Document.Text==unlockedText,"Text außerhalb der Freischaltlisten verändert.");
                Assert(s.Undo()&&s.UnlockedRecruitments().Count==0&&s.UnlockedDealers().Count==3&&s.Undo()&&s.Document.Text==s.Original.Text&&!s.CanUndo,"Freischalten ist nicht je ein Rückgängig-Schritt.");
                Assert(s.UnlockDealersAndRecruitments()==(2,3)&&s.Document.Text==unlockedText&&s.UnlockDealersAndRecruitments()==(0,0)&&s.Undo()&&!s.CanUndo&&s.Redo(),"Beide Listen nicht in einem Rückgängig-Schritt.");
                s.CommitEdits();Assert(File.ReadAllText(path)==unlockedText&&SaveSession.Open(path).UnlockedDealers().Count==3,"Gespeicherte Datei weicht von den vorgemerkten Bytes ab.");
            });
            check("Text save: dealer and agency lists only grow at the end by new known cities",()=>
            {
                foreach(var (key,values) in new (string,string[])[]{("unlocked_dealers",["paris","berlin"]),("unlocked_dealers",["berlin","atlantis"]),("unlocked_dealers",["berlin","paris","paris"]),("unlocked_dealers",["berlin","berlin"]),
                    ("unlocked_dealers",[]),("unlocked_dealers",["lyon"]),("unlocked_recruitments",["truckersmp"]),("unlocked_recruitments",["Paris"]),("unlocked_recruitments",["paris","paris"])})
                    Refused(FeatureText,d=>d.SetArray("_nameless.1",key,values),key+" = "+string.Join(",",values));
            });
            check("Text save: odometer sets whole kilometres on owned trucks and trailers; integrity copy follows, trips stay",()=>
            {
                string path=Copy(FeatureText);var s=SaveSession.Open(path);
                s.SetOdometer("_nameless.8",0);s.SetOdometer("_nameless.9",SaveSession.MaxOdometer);
                var truck=s.Document.Find("_nameless.8");var trailer=s.Document.Find("_nameless.9");
                Assert(truck.Get("odometer")=="0"&&truck.Get("integrity_odometer")=="0"&&truck.Get("odometer_float_part")==SaveSession.Zero&&truck.Get("integrity_odometer_float_part")==SaveSession.Zero&&truck.Get("trip_distance_km")=="1534"&&truck.Get("trip_fuel_l")=="276","Kilometerstand des Lkw falsch.");
                Assert(trailer.Get("odometer")=="9999999"&&trailer.Get("integrity_odometer")=="9999999"&&trailer.Get("integrity_odometer_float_part")==SaveSession.Zero,"Kilometerstand des Anhängers falsch.");
                Untouched(s,"_nameless.8","_nameless.9");string staged=s.Document.Text;
                string range=L.F("input.range",L.T("odometer.label"),CompactNumber.Full(0),CompactNumber.Full(SaveSession.MaxOdometer));
                Reject(()=>s.SetOdometer("_nameless.8",-1),range);Reject(()=>s.SetOdometer("_nameless.8",SaveSession.MaxOdometer+1),range);Reject(()=>s.SetOdometer("_nameless.10",5));Reject(()=>s.SetOdometer("company.volatile.acc.berlin",5));
                s.SetOdometer("_nameless.8",0);Assert(s.Document.Text==staged,"Zweiter gleicher Kilometerstand ändert etwas.");
                Assert(s.Undo()&&s.Document.Find("_nameless.9").Get("odometer")=="927"&&s.Undo()&&s.Document.Text==s.Original.Text&&!s.CanUndo&&s.Redo()&&s.Redo(),"Kilometerstand ist nicht je ein Rückgängig-Schritt.");
                string expected=FeatureText.Replace(" odometer: 1534\n odometer_float_part: &3e738a3f\n integrity_odometer: 1534\n integrity_odometer_float_part: &3e738a3f\n"," odometer: 0\n odometer_float_part: &00000000\n integrity_odometer: 0\n integrity_odometer_float_part: &00000000\n")
                    .Replace(" odometer: 927\n odometer_float_part: &00000000\n integrity_odometer: 927\n integrity_odometer_float_part: &3f0b1145\n"," odometer: 9999999\n odometer_float_part: &00000000\n integrity_odometer: 9999999\n integrity_odometer_float_part: &00000000\n");
                Assert(s.Document.Text==expected,"Text außerhalb der Kilometerstände verändert.");s.CommitEdits();Assert(File.ReadAllText(path)==expected,"Gespeicherte Datei weicht von den vorgemerkten Bytes ab.");
                foreach(var (unit,values) in new (string,(string,string)[])[]{("_nameless.8",[("odometer","9")]),("_nameless.8",[("odometer","10000000"),("integrity_odometer","10000000")]),("_nameless.8",[("odometer","-5"),("integrity_odometer","-5")]),
                    ("_nameless.8",[("odometer","1.5"),("integrity_odometer","1.5")]),("_nameless.8",[("odometer_float_part","&3f000000")]),("_nameless.10",[("odometer","9"),("integrity_odometer","9")]),("_nameless.8",[("trip_distance_km","0")])})
                    Refused(FeatureText,d=>d.SetMany(values.Select(v=>(unit,v.Item1,v.Item2))),unit+" "+string.Join(",",values));
            });
            check("Text save: clean record zeroes offence counts, fines and the red light counter at full length; history and live police state stay",()=>
            {
                string path=Copy(FeatureText);var s=SaveSession.Open(path);
                Assert(s.Offences()==3&&s.CleanRecord()==5&&s.Offences()==0&&s.CleanRecord()==0,"Strafregister nicht vollständig oder nicht idempotent geleert.");
                var log=s.Document.Find("_nameless.20");var e=s.Document.Economy!;
                Assert(log.Number("offence_total_counts")==3&&log.Number("offence_total_fines")==3&&log.Get("detailed_history_entries")=="0"&&e.Get("red_light_fine_count")=="0"&&e.Get("emergency_call_count")=="1"&&e.Get("ai_crash_count")=="2"&&e.Get("cancelled_job_count")=="1","Längen oder fremde Zähler verändert.");
                Untouched(s,"_nameless.1","_nameless.20");
                string expected=FeatureText.Replace(" red_light_fine_count: 2\n"," red_light_fine_count: 0\n").Replace(" offence_total_counts[0]: 2\n"," offence_total_counts[0]: 0\n").Replace(" offence_total_counts[2]: 1\n"," offence_total_counts[2]: 0\n")
                    .Replace(" offence_total_fines[0]: 500\n"," offence_total_fines[0]: 0\n").Replace(" offence_total_fines[2]: 150\n"," offence_total_fines[2]: 0\n");
                Assert(s.Document.Text==expected,"Text außerhalb des Strafregisters verändert.");
                Assert(s.Undo()&&s.Document.Text==s.Original.Text&&!s.CanUndo&&s.Redo(),"Strafregister ist nicht ein Rückgängig-Schritt.");
                s.CommitEdits();Assert(File.ReadAllText(path)==expected,"Gespeicherte Datei weicht von den vorgemerkten Bytes ab.");
                foreach(var (unit,key,value) in new[]{("_nameless.20","offence_total_counts[0]","1"),("_nameless.20","offence_total_fines[0]","-5"),("_nameless.20","detailed_history_entries","1"),("_nameless.21","offence_counter[0]","0"),
                    ("_nameless.1","red_light_fine_count","5"),("_nameless.1","emergency_call_count","0"),("_nameless.1","ai_crash_count","0"),("_nameless.1","cancelled_job_count","0")})
                    Refused(FeatureText,d=>d.Set(unit,key,value),unit+"/"+key);
                Refused(FeatureText,d=>d.SetArray("_nameless.20","offence_total_counts",["0","0"]),"gekürzte Zählerliste");Refused(FeatureText,d=>d.SetArray("_nameless.20","offence_total_fines",["0","0","0","0"]),"verlängerte Bußgeldliste");
            });
            check("Text save: bus stations become discovered (false to true only); line offers stay",()=>
            {
                string path=Copy(FeatureText);var s=SaveSession.Open(path);
                Assert(s.BusStops()==(1,3)&&s.DiscoverBusStops()==2&&s.BusStops()==(3,3)&&s.DiscoverBusStops()==0,"Busbahnhöfe nicht oder nicht idempotent entdeckt.");
                Untouched(s,"bus_stop.city.paris","bus_stop.city.lyon");
                string expected=FeatureText.Replace("bus_stop.city.paris {\n discovered: false","bus_stop.city.paris {\n discovered: true").Replace("bus_stop.city.lyon {\n discovered: false","bus_stop.city.lyon {\n discovered: true");
                Assert(s.Document.Text==expected&&s.Undo()&&s.Document.Text==s.Original.Text&&!s.CanUndo&&s.Redo(),"Busbahnhöfe nicht genau ein Rückgängig-Schritt oder fremder Text verändert.");
                s.CommitEdits();Assert(File.ReadAllText(path)==expected,"Gespeicherte Datei weicht von den vorgemerkten Bytes ab.");
                Refused(FeatureText,d=>d.Set("bus_stop.city.berlin","discovered","false"),"Busbahnhof verborgen");Refused(FeatureText,d=>d.Set("bus_stop.city.paris","lines_offer","1"),"Linienangebot");
                Refused(FeatureText,d=>d.Set("bus_stop.city.paris","discovered","1"),"Busbahnhof mit Zahl");
            });
            check("Binary save: visited cities, discovered flags, hired driver skills and plates patch only their own fields",()=>
            {
                byte[] bsii=FeatureFixture();string path=Path.Combine(folder,"feature-binary.sii");File.WriteAllBytes(path,bsii);
                var s=SaveSession.Open(path);Assert(new BinarySii(bsii).Patch(s.Original,s.Document).SequenceEqual(bsii),"Unveränderter Binärsave nicht bytegleich.");
                Assert(s.KnownCities().SequenceEqual(["berlin","lyon","paris"])&&s.VisitedCities().SequenceEqual(["berlin"])&&s.HiredDrivers().Select(d=>d.Id).SequenceEqual(["driver.hired"]),"Binärer Spielstand falsch gelesen.");
                Assert(s.VisitCities(["paris","lyon"])==2&&s.MaxDriverSkills()==1,"Städte oder Fahrer nicht geändert.");s.SetLicensePlate("vehicle.mine","WERK 1");
                Reject(()=>s.SetLicensePlate("trailer.mine","AB-12"),L.T("core.plate.trailer"));Reject(()=>s.SetLicensePlate("vehicle.dealer","X 1"));
                var approved=Approved(s);Assert(approved.Count==12&&!approved.Contains(("trailer.mine","license_plate")),"Unerwartete Zahl geänderter Felder: "+approved.Count);
                var output=PatchExactly(s,bsii,approved,true);
                Assert(output.Text.Contains("license_plate: \"WERK 1|germany\"")&&output.Text.Contains("license_plate: \"B DG 2003|_germany\"")&&output.Text.Contains("visited_cities_count[2]: 1")&&output.Text.Contains("license_plate_type: \"\""),"Binärwerte falsch geschrieben.");
                for(int i=0;i<3;i++)s.Undo();Assert(new BinarySii(bsii).Patch(s.Original,s.Document).SequenceEqual(bsii),"Rückgängig gemachte Binäränderungen nicht bytegleich.");
                for(int i=0;i<3;i++)s.Redo();s.CommitEdits();
                var reopened=SaveSession.Open(path);Assert(File.ReadAllBytes(path).AsSpan().StartsWith("SiiNunit"u8)&&reopened.Document.Text==output.Text&&reopened.VisitedCities().SequenceEqual(["berlin","paris","lyon"]),"Binärer Spielstand nach dem Speichern falsch.");
            });
            if(sample!=null)check("Real binary save: visiting cities, plates and driver skills patch only their own bytes and survive saving",()=>
            {
                byte[] original=File.ReadAllBytes(sample),payload=SaveSession.Unwrap(original);Assert(payload.AsSpan().StartsWith("BSII"u8),"Für diesen Test wird ein Original-BSII benötigt.");
                string path=Path.Combine(folder,"real-features.sii");File.WriteAllBytes(path,original);var s=SaveSession.Open(path);
                Assert(new BinarySii(payload).Patch(s.Original,s.Document).SequenceEqual(payload),"Unveränderter echter Binärsave nicht bytegleich.");
                var visited=s.VisitedCities().ToList();var known=s.KnownCities();var fresh=known.Except(visited).Take(3).ToList();
                Assert(visited.Count>0&&visited.All(known.Contains)&&fresh.Count==3,"Städte im echten Spielstand nicht erkannt.");
                Assert(s.VisitCities(fresh)==3&&s.VisitedCities().SequenceEqual(visited.Concat(fresh)),"Städte nicht angehängt.");
                Assert(s.Document.Units.Where(u=>u.Type=="company").All(u=>u.Get("discovered")==(fresh.Any(c=>u.Id.EndsWith("."+c,StringComparison.Ordinal))?"true":s.Original.Find(u.Id).Get("discovered"))),"Firmen falsch entdeckt.");
                s.MaxDriverSkills();Assert(s.HiredDrivers().All(d=>d.Number("adr")==63&&SaveSession.SkillKeys.All(k=>d.Number(k)==6)),"Fahrerfähigkeiten nicht am Maximum.");
                var own=SaveSession.OwnedVehicleIds(s.Document).Select(s.Document.Find).Where(u=>u.Get("license_plate") is {} p&&p.Contains('|')).ToList();
                var plates=own.Where(u=>u.Type=="vehicle").ToDictionary(u=>u.Id,u=>SiiDocument.Display(u.Get("license_plate"))[(SiiDocument.Display(u.Get("license_plate")).LastIndexOf('|')+1)..]);
                Assert(plates.Count>0,"Kein eigenes Kennzeichen gefunden.");foreach(var id in plates.Keys)s.SetLicensePlate(id,"WERK 18");
                foreach(var trailer in own.Where(u=>u.Type=="trailer"))Reject(()=>s.SetLicensePlate(trailer.Id,"WERK 18"),L.T("core.plate.trailer"));
                var approved=Approved(s);Assert(approved.All(a=>a.Item2 is "visited_cities" or "visited_cities_count" or "discovered" or "license_plate" or "adr" or "long_dist" or "heavy" or "fragile" or "urgent" or "mechanical"),"Unerwartetes Feld geändert.");
                var output=PatchExactly(s,payload,approved,false);Assert(output.Fields.Count>10000,"Unvollständige Binärprüfung.");
                s.CommitEdits();var reopened=SaveSession.Open(path);
                Assert(reopened.Document.Text==output.CompatibleText&&reopened.VisitedCities().SequenceEqual(visited.Concat(fresh))&&plates.All(p=>reopened.Document.Find(p.Key).Get("license_plate")==$"\"WERK 18|{p.Value}\"")
                    &&own.Where(t=>t.Type=="trailer").All(t=>reopened.Document.Find(t.Id).Get("license_plate")==t.Get("license_plate")),"Echter Spielstand nach dem Speichern falsch.");
                string again=Path.Combine(folder,"real-undo.sii");File.WriteAllBytes(again,original);var u=SaveSession.Open(again);u.VisitCities(fresh);u.SetLicensePlate(plates.Keys.First(),"X 1");u.Undo();u.Undo();
                Assert(new BinarySii(payload).Patch(u.Original,u.Document).SequenceEqual(payload)&&!u.Dirty,"Rückgängig gemachte Änderungen am echten Save nicht bytegleich.");
                Assert(File.ReadAllBytes(sample).SequenceEqual(original),"Originaldatei wurde verändert.");
            });
            if(sample!=null)check("Real binary save: dealers, agencies, bus stations and odometers patch only their own bytes and survive saving",()=>
            {
                byte[] original=File.ReadAllBytes(sample),payload=SaveSession.Unwrap(original);
                string path=Path.Combine(folder,"real-unlocks.sii");File.WriteAllBytes(path,original);var s=SaveSession.Open(path);
                var known=s.KnownCities();var dealers=s.UnlockedDealers().ToList();var agencies=s.UnlockedRecruitments().ToList();var (found,total)=s.BusStops();
                var lines=s.Document.Units.Where(u=>u.Type=="bus_stop").ToDictionary(u=>u.Id,u=>u.Fields.Where(f=>f.Key.StartsWith("lines_offer",StringComparison.Ordinal)).Select(f=>(f.Key,f.Value)).ToList());
                Assert(dealers.Count>0&&agencies.Count>0&&known.Count>dealers.Count&&total>found&&found>0,"Händler, Agenturen oder Busbahnhöfe im echten Spielstand nicht erkannt.");
                var missingDealers=known.Where(c=>!dealers.Contains(c)).ToList();var missingAgencies=known.Where(c=>!agencies.Contains(c)).ToList();
                Assert(s.UnlockDealersAndRecruitments()==(missingDealers.Count,missingAgencies.Count)&&s.UnlockedDealers().SequenceEqual(dealers.Concat(missingDealers))&&s.UnlockedRecruitments().SequenceEqual(agencies.Concat(missingAgencies))&&s.UnlockDealersAndRecruitments()==(0,0),"Händler oder Agenturen falsch angehängt oder nicht idempotent.");
                Assert(s.DiscoverBusStops()==total-found&&s.BusStops()==(total,total)&&s.DiscoverBusStops()==0,"Busbahnhöfe nicht vollständig entdeckt.");
                Assert(lines.All(l=>s.Document.Find(l.Key).Fields.Where(f=>f.Key.StartsWith("lines_offer",StringComparison.Ordinal)).Select(f=>(f.Key,f.Value)).SequenceEqual(l.Value)),"Linienangebote verändert.");
                var owned=SaveSession.OwnedVehicleIds(s.Document).Where(id=>s.Document.Find(id).Get("odometer")!=null).Order(StringComparer.Ordinal).ToList();Assert(owned.Count>0,"Kein eigener Kilometerstand gefunden.");
                var trips=owned.ToDictionary(id=>id,id=>s.Document.Find(id).Fields.Where(f=>f.Key.StartsWith("trip_",StringComparison.Ordinal)).Select(f=>(f.Key,f.Value)).ToList());
                for(int i=0;i<owned.Count;i++)s.SetOdometer(owned[i],123_456+i);
                string staged=s.Document.Text;s.SetOdometer(owned[0],123_456);Assert(s.Document.Text==staged,"Gleicher Kilometerstand ändert etwas.");
                if(s.Document.Units.FirstOrDefault(u=>u.Type=="vehicle"&&u.Get("odometer")!=null&&!owned.Contains(u.Id)) is {} offer)Reject(()=>s.SetOdometer(offer.Id,1));
                var approved=Approved(s);
                Assert(approved.All(a=>a.Item2 is "unlocked_dealers" or "unlocked_recruitments" or "discovered" or "odometer" or "integrity_odometer" or "odometer_float_part" or "integrity_odometer_float_part")&&approved.Count(a=>a.Item2=="odometer")==owned.Count,"Unerwartetes Feld geändert.");
                var output=PatchExactly(s,payload,approved,true);Assert(output.Fields.Count>10000,"Unvollständige Binärprüfung.");
                s.CommitEdits();var reopened=SaveSession.Open(path);
                Assert(reopened.Document.Text==output.CompatibleText&&reopened.UnlockedDealers().Count==dealers.Count+missingDealers.Count&&reopened.BusStops()==(total,total)
                    &&owned.Select((id,i)=>reopened.Document.Find(id)).All(u=>u.Get("odometer")==u.Get("integrity_odometer")&&u.Get("odometer_float_part")==SaveSession.Zero&&u.Fields.Where(f=>f.Key.StartsWith("trip_",StringComparison.Ordinal)).Select(f=>(f.Key,f.Value)).SequenceEqual(trips[u.Id]))
                    &&reopened.Document.Find(owned[0]).Get("odometer")=="123456","Echter Spielstand nach dem Speichern falsch.");
                string again=Path.Combine(folder,"real-unlocks-undo.sii");File.WriteAllBytes(again,original);var u=SaveSession.Open(again);
                u.UnlockDealersAndRecruitments();u.DiscoverBusStops();u.SetOdometer(owned[0],1);u.Undo();u.Undo();u.Undo();
                Assert(new BinarySii(payload).Patch(u.Original,u.Document).SequenceEqual(payload)&&!u.Dirty&&!u.CanUndo,"Rückgängig gemachte Änderungen am echten Save nicht bytegleich.");
                Assert(File.ReadAllBytes(sample).SequenceEqual(original),"Originaldatei wurde verändert.");
            });
            if(sample!=null)check("Real binary save: clean record zeroes uint counts and int64 fines at length 14 and restores the exact original bytes",()=>
            {
                byte[] original=File.ReadAllBytes(sample),payload=SaveSession.Unwrap(original);
                string seedPath=Path.Combine(folder,"real-record-seed.sii");File.WriteAllBytes(seedPath,original);var seed=SaveSession.Open(seedPath);
                var log=seed.Document.Resolve(seed.Document.Economy,"police_offence_log")??throw new InvalidDataException("Kein Strafregister im echten Spielstand.");
                Assert(log.Number("offence_total_counts")==14&&log.Number("offence_total_fines")==14&&seed.Offences()==0,"Strafregister hat nicht 14 leere Einträge.");
                seed.Edit(d=>d.SetMany([(log.Id,"offence_total_counts[0]","3"),(log.Id,"offence_total_counts[13]","2"),(log.Id,"offence_total_fines[0]","1500"),(log.Id,"offence_total_fines[13]","5000000000"),(d.Economy!.Id,"red_light_fine_count","2")]));
                byte[] dirty=new BinarySii(payload).Patch(seed.Original,seed.Document);var history=log.Get("detailed_history_entries");
                string path=Path.Combine(folder,"real-record.sii");File.WriteAllBytes(path,dirty);var s=SaveSession.Open(path);
                Assert(s.Offences()==5&&s.Document.Find(log.Id).Get("offence_total_fines[13]")=="5000000000","Int64-Bußgeld nicht exakt geschrieben oder gelesen.");
                Assert(s.CleanRecord()==5&&s.Offences()==0&&s.CleanRecord()==0,"Strafregister nicht vollständig oder nicht idempotent geleert.");
                var approved=Approved(s);Assert(approved.SetEquals([(log.Id,"offence_total_counts"),(log.Id,"offence_total_fines"),(s.Document.Economy!.Id,"red_light_fine_count")]),"Unerwartetes Feld geändert.");
                PatchExactly(s,dirty,approved,true);Assert(new BinarySii(dirty).Patch(s.Original,s.Document).SequenceEqual(payload),"Geleertes Strafregister ist nicht bytegleich zum Original.");
                string again=Path.Combine(folder,"real-record-undo.sii");File.WriteAllBytes(again,dirty);var u=SaveSession.Open(again);u.CleanRecord();
                Assert(u.Undo()&&!u.Dirty&&new BinarySii(dirty).Patch(u.Original,u.Document).SequenceEqual(dirty),"Rückgängig stellt das Strafregister nicht bytegleich wieder her.");
                u.Edit(d=>d.SetArray(log.Id,"offence_total_fines",Enumerable.Repeat("0",15).ToList()));Reject(()=>new BinarySii(dirty).Patch(u.Original,u.Document));Reject(()=>u.CommitEdits());
                s.CommitEdits();var reopened=SaveSession.Open(path).Document;var cleaned=reopened.Find(log.Id);
                Assert(cleaned.Number("offence_total_counts")==14&&cleaned.Number("offence_total_fines")==14&&Enumerable.Range(0,14).All(i=>cleaned.Get($"offence_total_counts[{i}]")=="0"&&cleaned.Get($"offence_total_fines[{i}]")=="0")
                    &&cleaned.Get("detailed_history_entries")==history&&reopened.Economy!.Get("red_light_fine_count")=="0","Strafregister nach dem Speichern falsch.");
                Assert(File.ReadAllBytes(sample).SequenceEqual(original),"Originaldatei wurde verändert.");
            });
        }
        finally{SaveSession.DataRoot=oldRoot;}
    }
    static byte[] FeatureFixture()
    {
        using var stream=new MemoryStream();using var w=new BinaryWriter(stream,Encoding.UTF8,true);
        void Str(string s){byte[] b=Encoding.UTF8.GetBytes(s);w.Write((uint)b.Length);w.Write(b);}
        void Token(string s){const string chars="0123456789abcdefghijklmnopqrstuvwxyz_";ulong n=0;foreach(char c in s.Reverse())n=n*38+(uint)chars.IndexOf(c)+1;w.Write(n);}
        void Id(string s){if(s=="null"){w.Write((byte)0);return;}var parts=s.Split('.');w.Write((byte)parts.Length);foreach(string part in parts)Token(part);}
        void Ids(params string[] ids){w.Write((uint)ids.Length);foreach(var id in ids)Id(id);}
        void Prototype(uint id,string name,params (uint Type,string Name)[] fields){w.Write(0u);w.Write((byte)1);w.Write(id);Str(name);foreach(var f in fields){w.Write(f.Type);Str(f.Name);}w.Write(0u);}
        w.Write("BSII"u8);w.Write(3u);
        Prototype(1,"economy",(0x39,"bank"),(0x39,"player"),(0x27,"experience_points"),(0x27,"adr"),(0x27,"long_dist"),(0x04,"visited_cities"),(0x28,"visited_cities_count"),(0x03,"last_visited_city"));
        Prototype(2,"bank",(0x31,"money_account"));
        Prototype(3,"player",(0x03,"hq_city"),(0x3a,"trucks"),(0x3a,"trailers"),(0x3a,"drivers"));
        Prototype(4,"driver_player",(0x39,"profit_log"));
        Prototype(5,"driver_ai",(0x27,"adr"),(0x27,"long_dist"),(0x27,"heavy"),(0x27,"fragile"),(0x27,"urgent"),(0x27,"mechanical"),(0x03,"hometown"),(0x27,"experience_points"));
        Prototype(6,"vehicle",(0x05,"fuel_relative"),(0x01,"license_plate"),(0x03,"license_plate_type"));
        Prototype(7,"trailer",(0x01,"license_plate"),(0x39,"slave_trailer"));
        Prototype(8,"company",(0x3d,"permanent_data"),(0x28,"cargo_offer_seeds"),(0x35,"discovered"),(0x27,"state"));
        w.Write(1u);Id("economy.test");Id("bank.test");Id("player.test");w.Write(100u);w.Write(0u);w.Write(0u);w.Write(1u);Token("berlin");w.Write(1u);w.Write(3u);Token("berlin");
        w.Write(2u);Id("bank.test");w.Write(1000L);
        w.Write(3u);Id("player.test");Token("berlin");Ids("vehicle.mine");Ids("trailer.mine");Ids("driver.me","driver.hired");
        w.Write(4u);Id("driver.me");Id("null");
        foreach(var (id,home) in new[]{("driver.hired","berlin"),("driver.pool","paris")}){w.Write(5u);Id(id);for(int i=0;i<6;i++)w.Write(id=="driver.hired"?1u:0u);Token(home);w.Write(500u);}
        foreach(var (id,plate) in new[]{("vehicle.mine","HB<img src=/material/ui/lp/germany/b_2.mat color=FFFFFFFF>TU 37|germany"),("vehicle.dealer","XX 1|france")}){w.Write(6u);Id(id);w.Write(1f);Str(plate);w.Write(0UL);}
        w.Write(7u);Id("trailer.mine");Str("B DG 2003|_germany");Id("null");
        foreach(var (id,discovered) in new[]{("company.volatile.acc.berlin",true),("company.volatile.acc.paris",false),("company.volatile.tradeaux.paris",false),("company.volatile.acc.lyon",false),("company.volatile.acc.truckersmp",false)})
        {w.Write(8u);Id(id);Id("company.permanent.acc");w.Write(2u);w.Write(7u);w.Write(8u);w.Write(discovered?(byte)1:(byte)0);w.Write(0u);}
        w.Write(0u);w.Write((byte)0);
        return stream.ToArray();
    }
    static (byte[] Bytes,int MoneyOffset) Fixture(uint version,bool unknownOrdinal=false)
    {
        using var stream=new MemoryStream();using var w=new BinaryWriter(stream,Encoding.UTF8,true);
        void Str(string s){byte[] b=Encoding.UTF8.GetBytes(s);w.Write((uint)b.Length);w.Write(b);}
        void Token(string s){const string chars="0123456789abcdefghijklmnopqrstuvwxyz_";ulong n=0;foreach(char c in s.Reverse())n=n*38+(uint)chars.IndexOf(c)+1;w.Write(n);}
        void Id(string s){var parts=s.Split('.');w.Write((byte)parts.Length);foreach(string part in parts)Token(part);}
        void Prototype(uint id,string name,params (uint Type,string Name,string? Enum)[] fields)
        {
            w.Write(0u);w.Write((byte)1);w.Write(id);Str(name);
            foreach(var f in fields){w.Write(f.Type);Str(f.Name);if(f.Type==0x37){w.Write(1u);w.Write(0u);Str(f.Enum!);}}w.Write(0u);
        }
        w.Write("BSII"u8);w.Write(version);
        Prototype(1,"economy",(0x39,"bank",null),(0x39,"player",null),(0x25,"experience_points",null));
        Prototype(2,"bank",(0x31,"money_account",null));
        Prototype(3,"player",(0x03,"hq_city",null));
        Prototype(4,"garage",(0x3a,"vehicles",null),(0x3c,"drivers",null),(0x25,"status",null),(0x39,"profit_log",null));
        Prototype(5,"profit_log",(0x26,"stats_data",null));
        Prototype(6,"vehicle",(0x19,"cabin_position",null),(0x05,"steering_adjustment",null),(0x01,"mod_data",null),(0x33,"unknown_counter",null));
        Prototype(7,"vehicle_cargo_accessory",(0x37,"lashing_method","none"),(0x37,"lashing_gear_type","strap"),(0x37,"cargo_area","area_cont_40ft"),(0x37,"unrecognized_enum","none"));
        w.Write(1u);Id("economy.test");Id("bank.test");Id("player.test");w.Write(2302);
        w.Write(2u);Id("bank.test");int money=checked((int)stream.Position);w.Write(31129L);
        w.Write(3u);Id("player.test");Token("berlin");
        w.Write(4u);Id("garage.berlin");w.Write(1u);Id("wheel.test");w.Write(1u);w.Write((byte)0);w.Write(6);Id("profit.test");
        w.Write(5u);Id("profit.test");w.Write(0u);
        w.Write(6u);Id("wheel.test");w.Write(1f);w.Write(2f);w.Write(3f);if(version!=1)w.Write((float)((2048<<12)|2048));w.Write(1f);w.Write(0f);w.Write(0f);w.Write(0f);w.Write(0x80000000u);Str("quote \" and slash \\ and ä");w.Write(ulong.MaxValue);
        w.Write(7u);Id("cargo.test");w.Write(0u);w.Write(0u);w.Write(0u);w.Write(unknownOrdinal?99u:0u);w.Write(0u);w.Write((byte)0);
        return(stream.ToArray(),money);
    }
    /// <summary>When ETS2 is installed: def.scs and the DLC archives open read-only, cities and the XP curve load, the city list is cached.</summary>
    public static void Archives(string root,Action<string,Action> check)
    {
        void Assert(bool value,string message){if(!value)throw new InvalidDataException(message);}
        var game=Installation.Detect();
        if(game.GamePath is not {} folder||!File.Exists(Path.Combine(folder,"def.scs")))return;
        check("Installed def.scs and DLC archives read-only: over 50 cities, city names and XP curve load",()=>
        {
            string def=Path.Combine(folder,"def.scs");var before=(new FileInfo(def).Length,File.GetLastWriteTimeUtc(def));
            using(var archive=HashFsReader.Open(def))
            {
                var cities=archive.GetDirectoryListing("def/city").Files;Assert(cities.Count>50,$"def/city lists only {cities.Count} files.");
                string file=cities.FirstOrDefault(f=>f.EndsWith("berlin.sui",StringComparison.Ordinal))??cities.First(f=>f.EndsWith(".sui",StringComparison.Ordinal));
                Assert(Encoding.UTF8.GetString(archive.Extract(file)[0]).Contains("city_data"),file+" has no city_data.");
            }
            Assert((new FileInfo(def).Length,File.GetLastWriteTimeUtc(def))==before,"def.scs was modified.");
            Assert(XpCurve.Load(folder).Source!=XpCurve.DefaultSource,"XP curve not read from def.scs.");
            var oldRoot=SaveSession.DataRoot;SaveSession.DataRoot=Path.Combine(root,"city-cache");
            try
            {
                var warnings=new List<string>();var list=GameMap.Cities(game,warnings);
                Assert(warnings.Count==0&&list.Count>50&&list.Any(c=>c.Id=="berlin"&&c.Name=="Berlin"&&c.Country=="germany"),"Cities incomplete: "+string.Join("; ",warnings.Take(3)));
                Assert(game.Dlcs.Count==0||list.Any(c=>c.Dlc!="Basisspiel"),"No city read from a DLC archive.");
                Assert(Directory.GetFiles(SaveSession.DataRoot,"cities-*.json").Length==1&&GameMap.Cities(game).Count==list.Count,"City list is not cached.");
            }
            finally{SaveSession.DataRoot=oldRoot;}
        });
    }
}
