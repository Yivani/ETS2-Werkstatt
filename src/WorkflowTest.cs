using System.Text;
using System.Text.Json;

namespace Werkstatt;
internal static class WorkflowTest
{
    public static void Run(string root,string fixture,Action<string,Action> check)
    {
        void Assert(bool value,string message){if(!value)throw new Exception(message);}
        void Reject(Action action){try{action();}catch(Exception e) when(e is InvalidDataException or IOException){return;}throw new Exception("Unsichere Operation wurde akzeptiert.");}
        string profile=Path.Combine(root,"workflow","profiles","TEST"),folder=Path.Combine(profile,"save","1");Directory.CreateDirectory(folder);
        string file=Path.Combine(folder,"game.sii");File.WriteAllText(file,fixture);string info=Path.Combine(folder,"info.sii");
        File.WriteAllText(info,"SiiNunit\n{\nsave_container : save.test {\n name: \"Original\"\n file_time: 1\n time: 1000\n info_money_account: 1000\n info_players_experience: 100\n dependencies: 1\n dependencies[0]: \"mod|keep\"\n unknown_info: \"keep exactly\"\n}\n}\n");
        string controls=Path.Combine(profile,"controls.sii"),config=Path.Combine(profile,"config_local.cfg");File.WriteAllText(controls,"axis/button bindings; force feedback; never rewrite");File.WriteAllText(config,"steering sensitivity; never rewrite");File.WriteAllBytes(Path.Combine(folder,"preview.tga"),[1,2,3,4]);
        var protectedFiles=new[]{controls,config,info,Path.Combine(folder,"preview.tga")}.ToDictionary(p=>p,p=>File.ReadAllBytes(p));
        var originalRoot=SaveSession.DataRoot;SaveSession.DataRoot=Path.Combine(root,"workflow-backups");
        try
        {
            var session=SaveSession.Open(file);
            check("Fast staging copies cannot mutate the original unit or field collections",()=>
            {
                var baseline=new SiiDocument(fixture);var copy=baseline.Clone();int fields=baseline.Units[0].Fields.Count;copy.Units[0].Fields.Clear();copy.Units.Clear();Assert(baseline.Units.Count==9&&baseline.Units[0].Fields.Count==fields&&baseline.Text==fixture,"Bearbeitung teilt veränderbare Original-Listen.");
            });
            check("Only selected field bytes change in a text save",()=>
            {
                session.Edit(d=>d.Set(d.Bank!.Id,"money_account","9000"));
                Assert(session.Document.Text==fixture.Replace("money_account: 1000","money_account: 9000")&&session.ChangeCount==1,"Nicht ausgewählte Werte oder Bytes verändert.");
                session.Changes().Clear();Assert(session.ChangeCount==1,"Änderungsübersicht kann den Schutzcache verändern.");
            });
            check("Undo and redo preserve prior steps and never write the source",()=>
            {
                string money=session.Document.Text;session.Garage("garage.paris",3);string garage=session.Document.Text;
                Assert(session.Undo()&&session.Document.Text==money&&session.Undo()&&session.Document.Text==fixture,"Rückgängig verliert Daten.");
                Assert(session.Redo()&&session.Document.Text==money&&session.Redo()&&session.Document.Text==garage,"Wiederholen verliert Daten.");
                session.Undo();session.Edit(d=>d.Set(d.Economy!.Id,"experience_points","200"));Assert(!session.CanRedo&&File.ReadAllText(file)==fixture,"Neuer Schritt oder Quelldatei falsch.");
            });
            check("No-op edits and reset do not create phantom history",()=>
            {
                session.Reset();session.Edit(d=>d.Set(d.Bank!.Id,"money_account","1000"));Assert(!session.Dirty&&!session.CanUndo&&!session.CanRedo&&session.ChangeCount==0,"Leerer Schritt in Historie.");
            });
            check("Complete save copy has a distinct name, correct summaries and unchanged source settings",()=>
            {
                session.Edit(d=>d.Set(d.Bank!.Id,"money_account","30000"));string before=session.Document.Text;string copy=Path.Combine(root,"workflow-export");session.ExportCopy(copy);
                Assert(SaveSession.Open(Path.Combine(copy,"game.sii")).Document.Text==before,"Kopie verlor Werte.");var metadata=new SiiDocument(SaveSession.Decode(Path.Combine(copy,"info.sii"))).First("save_container")!;
                Assert(SiiDocument.Display(metadata.Get("name")).StartsWith("Werkstatt-Kopie")&&metadata.Number("info_money_account")==30000&&metadata.Get("dependencies[0]")=="\"mod|keep\""&&metadata.Get("unknown_info")=="\"keep exactly\"","Kopie beschädigt Metadaten.");
                Assert(File.ReadAllBytes(Path.Combine(copy,"preview.tga")).SequenceEqual(protectedFiles[Path.Combine(folder,"preview.tga")]),"Vorschau verändert.");
                Assert(File.ReadAllText(file)==fixture&&session.Dirty&&session.CanUndo&&protectedFiles.All(p=>File.ReadAllBytes(p.Key).SequenceEqual(p.Value)),"Export hat Quelle, Steuerung oder Historie verändert.");
                Reject(()=>session.ExportCopy(copy));Assert(SaveSession.Open(Path.Combine(copy,"game.sii")).Document.Text==before,"Bestehende Kopie überschrieben.");
            });
            check("Copy export rejects stale sources and missing metadata without creating a partial save",()=>
            {
                string dest=Path.Combine(root,"stale-export");File.AppendAllText(file,"\n");Reject(()=>session.ExportCopy(dest));Assert(!Directory.Exists(dest),"Unvollständige Kopie zurückgelassen.");File.WriteAllText(file,fixture);
                File.Move(info,info+".hold");try{Reject(()=>session.ExportCopy(dest));Assert(!Directory.Exists(dest),"Kopie ohne Metadaten angelegt.");}finally{File.Move(info+".hold",info);}
            });
            check("Atomic replacement preserves the actually displaced external revision",()=>
            {
                string path=Path.Combine(root,"concurrent-replace.txt");File.WriteAllText(path,"external revision");
                Reject(()=>SaveSession.AtomicReplace(path,Encoding.UTF8.GetBytes("ours"),SaveSession.Hash(Encoding.UTF8.GetBytes("stale revision"))));
                Assert(File.ReadAllText(path)=="external revision","Gleichzeitige externe Änderung verloren.");
                Assert(!Directory.GetFiles(root,"concurrent-replace.txt.werkstatt-*").Any(),"Atomare Prüfung hinterlässt temporäre Dateien.");
            });
            check("Successful persistence clears history and preserves wheel/config/metadata bytes",()=>
            {
                session.CommitEdits();Assert(!session.Dirty&&!session.CanUndo&&!session.CanRedo&&protectedFiles.All(p=>File.ReadAllBytes(p.Key).SequenceEqual(p.Value)),"Speichern verändert andere Dateien oder Historie.");
            });
            var backup=SaveSession.Backups().Single();
            if(!SaveSession.GameRunning)check("Backup restore recovers an unreadable save without changing controls or metadata",()=>
            {
                File.WriteAllBytes(file,[0,1,2,3]);SaveSession.Restore(backup);Assert(File.ReadAllText(file)==fixture&&protectedFiles.All(p=>File.ReadAllBytes(p.Key).SequenceEqual(p.Value)),"Wiederherstellung beschädigten Saves fehlgeschlagen.");
                Assert(SaveSession.Backups().Count==2,"Beschädigter aktueller Stand wurde vor Restore nicht gesichert.");
            });
            check("Unreadable backup manifests do not break the backup list",()=>
            {
                string invalid=Path.Combine(SaveSession.BackupRoot,"invalid");Directory.CreateDirectory(invalid);File.WriteAllText(Path.Combine(invalid,"backup.json"),"{broken");Assert(SaveSession.Backups().All(b=>b.Folder!=invalid),"Ungültiges Manifest akzeptiert.");
            });
        }
        finally{SaveSession.DataRoot=originalRoot;}
    }
}
