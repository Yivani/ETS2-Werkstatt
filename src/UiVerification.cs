using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Werkstatt;
internal static class UiVerification
{
    static readonly (int Width,int Height)[] Sizes=[(1024,641),(1364,861)];
    static readonly Regex RawKey=new(@"^[a-z]+(\.[a-z_0-9]+)+$");
    public static void Render(string folder,string sample,List<CityInfo> cities)
    {
        Directory.CreateDirectory(folder);string sampleHash=SaveSession.Hash(File.ReadAllBytes(sample));
        string data=SaveSession.DataRoot,temp=Path.Combine(Path.GetTempPath(),"ETS2Werkstatt-UI-"+Guid.NewGuid().ToString("N"));
        SaveSession.DataRoot=Path.Combine(temp,"AppData");
        try
        {
            var source=new DirectoryInfo(Path.GetDirectoryName(sample)!);string profile=source.Parent?.Parent?.Name??"594956414E49";
            string slot=Path.Combine(temp,"profiles",profile,"save",source.Name);Directory.CreateDirectory(slot);
            foreach(var file in source.GetFiles())file.CopyTo(Path.Combine(slot,file.Name));
            string copy=Path.Combine(slot,Path.GetFileName(sample));SeedBackups(copy,Path.Combine(temp,"profiles",profile));
            var install=Installation.Detect();var save=SaveSession.Open(copy);var window=new MainWindow(null);MapData? map=null;if(install.GamePath!=null){map=GameMap.Load(install);MapView.PrepareAsync(map).Wait();}var results=new List<object>();
            foreach(var language in L.Languages)
            {
                L.Use(language.Code);App.ApplyLanguage();
                foreach(var (width,height) in Sizes)
                {
                    void Shot(FrameworkElement root,string name)
                    {
                        string file=$"{language.Code}-{name}-{width}.png";Layout(root,width,height);
                        var trimmed=Check(root,file);
                        if(((SolidColorBrush)((Panel)root).Background).Color!=Color.FromRgb(15,17,19))throw new Exception("Hauptfläche ist nicht dunkel: "+file);
                        foreach(var tab in Descendants(root).OfType<ToggleButton>().Where(t=>AutomationProperties.GetAutomationId(t).StartsWith("nav-")))
                            if(tab.ActualWidth+.5<tab.DesiredSize.Width)throw new Exception($"Reiter abgeschnitten ({tab.Content}): {file}.");
                        if(!name.Contains("guide")&&Descendants(root).OfType<Button>().Count(b=>Shown(b)&&b.Style==Application.Current.FindResource("PrimaryButton"))!=1)throw new Exception("Mehr als eine orange Schaltfläche: "+file);
                        Save(root,width,height,Path.Combine(folder,file));results.Add(new{language=language.Code,width,height,file,trimmed});
                    }
                    for(int i=0;i<MainWindow.Pages.Length;i++)Shot(window.PrepareForCapture(save,install,cities,MainWindow.Pages[i],width),$"{i+1:00}-{MainWindow.Pages[i]}");
                    for(int step=1;step<=3;step++)Shot(window.PrepareForCapture(save,install,cities,"driver",width,step),$"{step+5:00}-guide{step}");
                    Shot(window.CaptureCloudOnly(install,2,width),"09-cloud");
                    save.MaxOut();save.Service(SaveSession.OwnedVehicleIds(save.Document),true,true,true);save.DiscoverBusStops();
                    Shot(window.PrepareForCapture(save,install,cities,"driver",width),"13-driver-done");save.Reset();
                    if(map!=null){window.UseMapForCapture(map);Shot(window.PrepareForCapture(save,install,cities,"garages",width),"15-garages-map");window.UseMapForCapture(null);}
                    var trucks=window.PrepareForCapture(save,install,cities,"trucks",width);Layout(trucks,width,height);var vehicleTable=Descendants(trucks).OfType<DataGrid>().Single(g=>g.Name=="ScrollRegionTrucks");
                    if(vehicleTable.Items.Cast<VehicleRow>().FirstOrDefault(r=>!r.Truck) is {} trailer){vehicleTable.SelectedItem=trailer;Shot(trucks,"14-trucks-trailer");vehicleTable.SelectedItem=vehicleTable.Items[0];}
                }
                window.PrepareForCapture(save,install,cities,"driver",1364);
                var garage=save.Document.Units.First(u=>u.Type=="garage"&&u.Number("status")==0).Id;
                save.Edit(d=>{d.Set(d.Bank!.Id,"money_account","1000000");d.Set(d.Economy!.Id,"adr","63");d.Set(d.Economy.Id,"long_dist","6");});save.Garage(garage,3);save.Service(SaveSession.OwnedVehicleIds(save.Document),true,true,true);
                save.UnlockDealersAndRecruitments();save.DiscoverBusStops();save.SetOdometer(save.OwnedVehicles().First(u=>u.Type=="vehicle").Id,0);
                foreach(var (width,height) in new[]{(720,560),(600,420)})Capture(window.ReviewPanel(new Window()),width,height,Path.Combine(folder,$"{language.Code}-04-review-{width}.png"));
                save.Reset();window.PrepareForCapture(save,install,cities,"driver",1364);
                foreach(var (width,height) in new[]{(760,520),(600,400)})Capture(window.BackupsPanel(new Window()),width,height,Path.Combine(folder,$"{language.Code}-05-backups-{width}.png"));
                Capture(window.SwitcherPanel(),360,0,Path.Combine(folder,$"{language.Code}-10-switcher-360.png"));
                Capture(window.IndicatorPanel(),320,0,Path.Combine(folder,$"{language.Code}-11-indicator-320.png"));
                var (ask,_)=Ui.CreateAsk(null,L.T("notice.tmp.title"),L.T("notice.tmp.text"),L.T("ok"));var body=(FrameworkElement)ask.Content;ask.Content=null;Capture(body,460,0,Path.Combine(folder,$"{language.Code}-12-ask-460.png"));
            }
            File.WriteAllText(Path.Combine(folder,"ui-checks.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));
            L.Use("de");App.ApplyLanguage();VerifyInteractions(window,save,install,cities);
            save.Reset();window.PrepareForCapture(save,install,cities,"driver",1364);window.Close();
        }
        finally{SaveSession.DataRoot=data;try{Directory.Delete(temp,true);}catch(Exception e) when(e is IOException or UnauthorizedAccessException){}}
        if(SaveSession.Hash(File.ReadAllBytes(sample))!=sampleHash)throw new Exception("UI-Prüfung hat den Beispiel-Spielstand verändert.");
    }
    /// <summary>Two backups in the scratch data folder: one of the opened save with key bindings, one of another profile.</summary>
    static void SeedBackups(string save,string profile)
    {
        void Seed(string source,DateTime created,bool bindings)
        {
            string folder=Path.Combine(SaveSession.BackupRoot,created.ToString("yyyyMMdd-HHmmss-fff",System.Globalization.CultureInfo.InvariantCulture)+"-ui");Directory.CreateDirectory(folder);
            byte[] bytes=File.ReadAllBytes(save);File.WriteAllBytes(Path.Combine(folder,"game.sii"),bytes);
            File.WriteAllText(Path.Combine(folder,"backup.json"),JsonSerializer.Serialize(new BackupInfo(folder,source,SaveSession.Hash(bytes),created,"game.sii")));
            if(!bindings)return;
            string settings=Path.Combine(folder,"Einstellungen");Directory.CreateDirectory(settings);byte[] controls="SiiNunit\n{\n}\n"u8.ToArray();File.WriteAllBytes(Path.Combine(settings,"00-controls.sii"),controls);
            File.WriteAllText(Path.Combine(settings,"Dateien.json"),JsonSerializer.Serialize(new[]{new{source=Path.Combine(profile,"controls.sii"),copy="00-controls.sii",sha256=SaveSession.Hash(controls)}}));
        }
        Seed(save,DateTime.UtcNow.AddHours(-2),true);Seed(Path.Combine(Path.GetDirectoryName(profile)!,"4F54484552","save","1","game.sii"),DateTime.UtcNow.AddDays(-3),false);
    }
    /// <summary>Fails on raw keys, plural markers, unexpected scrolling, narrow columns and cut-off text; returns the trimmed texts that carry a tooltip.</summary>
    static List<string> Check(FrameworkElement root,string where)
    {
        var trimmed=new List<string>();
        foreach(var t in Descendants(root).OfType<TextBlock>().Where(t=>Shown(t)&&t.ActualWidth>0))
        {
            string text=Plain(t);if(RawKey.IsMatch(text)||text.Contains('|'))throw new Exception($"Fehlende Übersetzung „{text}“: {where}.");
            if(t.TextWrapping==TextWrapping.NoWrap)
            {
                double available=LayoutInformation.GetLayoutSlot(t).Width;t.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));double natural=t.DesiredSize.Width;t.InvalidateMeasure();
                if(natural<=available+.5)continue;
                if(!Ancestors(t).Prepend(t).Take(6).Any(a=>a is FrameworkElement f&&f.ToolTip!=null||a is DataGridCell))throw new Exception($"Text abgeschnitten „{text}“ ({natural:0} > {available:0} px): {where}.");
                trimmed.Add(text);
            }
            else if(!double.IsInfinity(t.MaxHeight)){double limit=t.MaxHeight;t.MaxHeight=double.PositiveInfinity;t.Measure(new Size(t.ActualWidth,double.PositiveInfinity));double height=t.DesiredSize.Height;t.MaxHeight=limit;t.InvalidateMeasure();if(height>limit+.5)throw new Exception($"Text zu lang „{t.Text}“: {where}.");}
        }
        root.UpdateLayout();
        foreach(var viewer in Descendants(root).OfType<ScrollViewer>().Where(v=>v.TemplatedParent is not TextBox))
        {
            bool region=Ancestors(viewer).Prepend(viewer).Any(a=>a is FrameworkElement f&&f.Name.StartsWith("ScrollRegion",StringComparison.Ordinal));
            bool safetyNet=Ancestors(viewer).Prepend(viewer).Any(a=>a is FrameworkElement f&&f.Name is "ScrollRegionInspector" or "ScrollRegionVehicle");
            if((viewer.ScrollableHeight>.5||viewer.ScrollableWidth>.5)&&(!region||safetyNet))throw new Exception($"Seite scrollt ({viewer.Name}, {viewer.ScrollableHeight:0} px): {where}.");
        }
        foreach(var table in Descendants(root).OfType<DataGrid>())if(table.ActualWidth>100&&table.Columns.Any(c=>c.ActualWidth<40))throw new Exception($"Tabellenspalte zu schmal: {where}.");
        if(Descendants(root).OfType<TextBox>().Any(t=>!t.AcceptsReturn&&t.ActualHeight>44))throw new Exception($"Einzeiliges Eingabefeld zu hoch: {where}.");
        return trimmed;
    }
    static void VerifyInteractions(MainWindow window,SaveSession save,Installation install,List<CityInfo> cities)
    {
        void Assert(bool result,string message){if(!result)throw new Exception(message);}
        FrameworkElement Prepare(string page,int guide=0){var root=window.PrepareForCapture(save,install,cities,page,1364,guide);Layout(root,1364,861);return root;}
        T Find<T>(FrameworkElement root,string id) where T:DependencyObject=>Descendants(root).OfType<T>().FirstOrDefault(c=>AutomationProperties.GetAutomationId(c)==id)??throw new Exception("Element fehlt: "+id);
        bool Has(FrameworkElement root,string id)=>Descendants(root).OfType<FrameworkElement>().Any(c=>AutomationProperties.GetAutomationId(c)==id&&Shown(c));
        void Click(ButtonBase b)=>b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var root=Prepare("driver");
        Assert(Descendants(root).OfType<ToggleButton>().Select(AutomationProperties.GetAutomationId).Where(id=>id.StartsWith("nav-")).SequenceEqual(MainWindow.Pages.Select(p=>"nav-"+p)),"Navigation enthält andere als die drei Bereiche.");
        Assert(Find<ToggleButton>(root,"save-selector").ToolTip as string==save.FilePath&&TextOf(Find<ToggleButton>(root,"save-selector")).Contains(Installation.SaveTitle(save.FilePath)),"Spielstand und Name fehlen im Umschalter.");
        Assert(AutomationProperties.GetName(Find<ToggleButton>(root,"status-indicator")).Length>0&&new[]{"backups-open","help-open","language","notice-line","notice-details","shortcuts-progress","time-days","time-clock","time-warning"}.All(id=>Has(root,id)),"Statusanzeige, Kopfleiste, Hinweiszeile oder Schnellaktionen fehlen.");
        var money=Find<TextBox>(root,"money-input");money.Text="30k";Assert(window.ApplyForVerification()&&save.Document.Bank!.Number("money_account")==30_000&&Find<TextBlock>(root,"money-limit-hint").Text==L.F("money.exact",CompactNumber.Full(30_000)),"Geld-Kurzform wird nicht gespeichert oder nicht ausgeschrieben.");
        money.Text="-50k";Assert(window.ApplyForVerification()&&save.Document.Bank!.Number("money_account")==-50_000,"Negativer Betrag (Schulden) wird nicht übernommen.");money.Text="30k";Assert(window.ApplyForVerification()&&save.Document.Bank!.Number("money_account")==30_000,"Geld nach Schulden nicht zurückgesetzt.");
        var maxMoney=Find<ToggleButton>(root,"max-money");maxMoney.IsChecked=true;Assert(money.IsReadOnly&&Find<TextBlock>(root,"money-limit-hint").Text==L.T("money.limit")&&window.ApplyForVerification()&&save.Document.Bank!.Number("money_account")==long.MaxValue,"Max-Geld oder Warnung funktioniert nicht.");
        maxMoney.IsChecked=false;Assert(Find<TextBlock>(root,"money-limit-hint").Text!=L.T("money.limit")&&window.ApplyForVerification()&&save.Document.Bank!.Number("money_account")==30_000,"Max-Geld stellt Eingabe nicht wieder her.");
        money.Text="max";Assert(Find<TextBlock>(root,"money-limit-hint").Text==L.T("money.limit"),"Manuelle Maximaleingabe zeigt keine Warnung.");money.Text="30k";
        var xp=Find<TextBox>(root,"xp-input");var level=Find<TextBox>(root,"level-input");xp.Text="19.144";Assert(CompactNumber.Parse(level.Text)==13,"XP-zu-Level rechnet falsch.");level.Text="14";Assert(CompactNumber.Parse(xp.Text)==21600&&window.ApplyForVerification(),"Level-zu-XP rechnet falsch.");
        var maxLevel=Find<ToggleButton>(root,"max-level");maxLevel.IsChecked=true;Assert(Find<TextBlock>(root,"xp-limit-hint").Text==L.T("xp.limit")&&window.ApplyForVerification()&&save.Document.Economy!.Number("experience_points")==int.MaxValue,"Max-Level oder Warnung wird nicht vorgemerkt.");
        maxLevel.IsChecked=false;Assert(Find<TextBlock>(root,"xp-limit-hint").Text!=L.T("xp.limit")&&window.ApplyForVerification()&&save.Document.Economy!.Number("experience_points")==21600,"XP werden nicht wiederhergestellt.");
        Find<TextBox>(root,"time-days").Text="10";Find<TextBox>(root,"time-clock").Text="06:30";Assert(window.ApplyForVerification()&&save.Document.Economy!.Number("game_time")==10*1440+390,"Weltzeit aus Tagen und Uhrzeit falsch.");
        var warning=Find<TextBlock>(root,"time-warning");Assert(warning.Text==L.T("time.back")&&warning.Foreground==Ui.Accent,"Warnung zum Zurückstellen der Uhr fehlt oder ist nicht hervorgehoben.");
        Find<TextBox>(root,"time-days").Text=CompactNumber.Full(save.Original.Economy!.Number("game_time")/1440+1);Assert(warning.Foreground==Ui.Muted&&window.ApplyForVerification(),"Vorstellen der Uhr wird als Risiko markiert.");
        root=Prepare("driver");Click(Find<Button>(root,"skill-long_dist-6"));Click(Find<Button>(root,"skill-fragile-2"));
        for(int i=0;i<6;i++)Find<ToggleButton>(root,$"adr-{i}").IsChecked=i is 0 or 5;
        Assert(window.ApplyForVerification()&&save.Document.Economy!.Number("long_dist")==6&&save.Document.Economy.Number("fragile")==2&&save.Document.Economy.Number("adr")==33,"Skill-Felder oder ADR-Bits falsch.");
        for(int mask=0;mask<64;mask++){for(int i=0;i<6;i++)Find<ToggleButton>(root,$"adr-{i}").IsChecked=(mask&(1<<i))!=0;Assert(window.ApplyForVerification()&&save.Document.Economy!.Number("adr")==mask,"ADR-Kombination falsch: "+mask);}
        Click(Find<Button>(root,"skills-max"));Assert(window.ApplyForVerification()&&SaveSession.SkillKeys.All(key=>save.Document.Economy!.Number(key)==6)&&save.Document.Economy!.Number("adr")==63,"Alle maximal setzt nicht alle Fähigkeiten und ADR-Klassen.");
        Click(Find<Button>(root,"skill-long_dist-0"));Assert(window.ApplyForVerification()&&save.Document.Economy!.Number("long_dist")==0&&AutomationProperties.GetItemStatus(Find<Button>(root,"skill-long_dist-0"))==L.T("skills.status.current"),"Stufe 0 setzt die Fähigkeit nicht zurück oder zeigt keine Auswahl.");
        Click(Find<Button>(root,"undo-edit"));Assert(save.Document.Economy!.Number("long_dist")==6&&save.CanRedo,"Rückgängig über die Leiste fehlgeschlagen.");root=Prepare("driver");Click(Find<Button>(root,"redo-edit"));Assert(save.Document.Economy!.Number("long_dist")==0,"Wiederholen über die Leiste fehlgeschlagen.");
        save.Edit(d=>d.Set(d.Bank!.Id,"money_account","-500"));root=Prepare("driver");Find<TextBox>(root,"xp-input").Text="20.000";Assert(window.ApplyForVerification()&&save.Document.Bank!.Number("money_account")==-500&&save.Document.Economy!.Number("experience_points")==20000,"XP-Änderung hat unveränderten negativen Kontostand zurückgesetzt oder blockiert.");
        save.Reset();root=Prepare("garages");DataGrid Garages()=>Descendants(root).OfType<DataGrid>().Single(l=>l.Name=="ScrollRegionGarages");var list=Garages();
        string Inspector()=>TextOf(Descendants(root).OfType<ScrollViewer>().Single(v=>v.Name=="ScrollRegionInspector"));
        Assert(!Descendants(root).OfType<FrameworkElement>().Any(e=>AutomationProperties.GetAutomationId(e).StartsWith("map-",StringComparison.Ordinal))&&list.Columns.Select(c=>c.Header as string).SequenceEqual([L.T("col.city"),L.T("garage.country"),L.T("col.kind"),L.T("col.trucksdrivers")]),"Garagentabelle hat falsche Spalten oder Kartenreste.");
        var allGarages=list.Items.Cast<GarageRow>().ToList();var hq=allGarages.Single(g=>g.Hq.Length>0);
        Assert(ReferenceEquals(list.SelectedItem,hq)&&Inspector().Contains(hq.City)&&Inspector().Contains(L.P("count.trucks",hq.Trucks))&&hq.Trucks+hq.Drivers>0&&hq.Counts==hq.Trucks+" · "+hq.Drivers,"Hauptsitz ist nicht ausgewählt, oder Lkw und Fahrer fehlen.");
        Assert(allGarages.Any(g=>g.Status==0)&&allGarages.All(g=>g.Status==0?g.Type==L.T("garage.none")&&g.Counts=="—":g.Type==L.T("garage.short."+g.Status))&&TextOf(list).Contains(L.T("garage.none")),"Typspalte zeigt nicht Nicht gekauft / Mini / Klein / Groß.");
        Find<RadioButton>(root,"garage-owned-1").IsChecked=true;Assert(list.Items.Count>0&&list.Items.Cast<GarageRow>().All(g=>g.Status>0),"Besitzfilter funktioniert nicht.");Find<RadioButton>(root,"garage-owned-0").IsChecked=true;
        Find<TextBox>(root,"garage-search").Text="no-matching-city-test";Assert(list.Items.Count==0&&TextOf(root).Contains(L.F("garage.nomatch","no-matching-city-test")),"Leere Suche zeigt Einträge oder keinen Hinweis.");
        root=Prepare("driver");root=Prepare("garages");Assert(Find<TextBox>(root,"garage-search").Text=="no-matching-city-test","Filter gehen beim Seitenwechsel verloren.");
        Click(Descendants(root).OfType<Button>().Single(b=>b.Content as string==L.T("garage.clear")&&Shown(b)));Assert(Find<TextBox>(root,"garage-search").Text.Length==0&&Garages().Items.Count==allGarages.Count,"Filter zurücksetzen zeigt nicht wieder alle Garagen.");
        Find<TextBox>(root,"garage-search").Text=hq.City[..Math.Min(3,hq.City.Length)].ToUpperInvariant();Assert(Garages().Items.Cast<GarageRow>().Any(g=>g.Id==hq.Id)&&Garages().Items.Count<allGarages.Count,"Suche findet die Stadt nicht unabhängig von der Schreibweise.");Find<TextBox>(root,"garage-search").Text="";
        list=Garages();Assert(!Find<RadioButton>(root,"garage-level-0").IsEnabled&&!Has(root,"garage-hq")&&new[]{"garage-level-6","garage-level-2","garage-level-3"}.All(id=>Has(root,id))&&Has(root,"garage-move"),"Hauptsitz lässt sich verkaufen, oder Größen oder Verlegen fehlen.");
        var free=list.Items.Cast<GarageRow>().First(g=>g.Status==0);list.SelectedItem=free;Assert(Inspector().Contains(free.City)&&!Find<Button>(root,"garage-hq").IsEnabled,"Tabellenauswahl füllt den Inspektor nicht, oder Hauptsitz in fremder Garage möglich.");
        Find<RadioButton>(root,"garage-level-2").IsChecked=true;Layout(root,1364,861);var bought=Garages().Items.Cast<GarageRow>().Single(g=>g.Id==free.Id);
        Assert(save.Document.Find(free.Id).Number("status")==2&&bought.Type==L.T("garage.short.2")&&bought.Counts=="0 · 0"&&(Garages().SelectedItem as GarageRow)?.Id==free.Id,"Garagengröße im Inspektor wird nicht vorgemerkt oder nicht in der Tabelle gezeigt.");save.Reset();
        root=Prepare("garages");var other=Garages().Items.Cast<GarageRow>().FirstOrDefault(g=>g.Status>0&&g.Hq.Length==0);Assert(other!=null,"Beispiel braucht eine zweite eigene Garage.");
        Garages().SelectedItem=other;Assert(Find<Button>(root,"garage-hq").IsEnabled,"Eigene Garage lässt sich nicht als Hauptsitz wählen.");Click(Find<Button>(root,"garage-hq"));Layout(root,1364,861);
        Assert(save.Document.Player!.Get("hq_city")==other!.Id["garage.".Length..]&&Garages().Items.Cast<GarageRow>().Single(g=>g.Hq.Length>0).Id==other.Id&&Inspector().Contains(L.T("garage.hq.current")),"Hauptsitz wird nicht verlegt oder nicht in der Tabelle gezeigt.");save.Reset();
        root=Prepare("garages");Garages().SelectedItem=Garages().Items.Cast<GarageRow>().Single(g=>g.Id==hq.Id);var unit=save.Document.Find(hq.Id);int slot=Enumerable.Range(0,(int)unit.Number("vehicles")).First(i=>unit.Get($"vehicles[{i}]") is not (null or "null")||unit.Get($"drivers[{i}]") is not (null or "null"));
        var moveTarget=Garages().Items.Cast<GarageRow>().First(g=>g.Status>0&&g.Id!=hq.Id);SaveSession.Open(save.FilePath).MoveSlot(hq.Id,moveTarget.Id,unit.Get($"vehicles[{slot}]") is not (null or "null")?"vehicles":"drivers",slot);
        Click(Find<Button>(root,"garage-move"));Layout(root,1364,861);var source=Garages().Items.Cast<GarageRow>().Single(g=>g.Id==hq.Id);var arrived=Garages().Items.Cast<GarageRow>().Single(g=>g.Id==moveTarget.Id);
        Assert(save.Dirty&&source.Trucks+source.Drivers<hq.Trucks+hq.Drivers&&arrived.Trucks+arrived.Drivers>moveTarget.Trucks+moveTarget.Drivers&&source.Trucks+source.Drivers+arrived.Trucks+arrived.Drivers==hq.Trucks+hq.Drivers+moveTarget.Trucks+moveTarget.Drivers,"Verlegen wird nicht vorgemerkt oder nicht in der Tabelle gezeigt.");save.Reset();
        root=Prepare("garages");Click(Find<Button>(root,"garages-upgrade"));
        Assert(save.Document.Units.Where(u=>u.Type=="garage"&&u.Number("status")>0).All(g=>g.Number("status")==3),"Eigene Garagen nicht ausgebaut.");save.Reset();
        root=Prepare("garages");var country=Find<ComboBox>(root,"garage-country");Assert(country.Items.Count>2,"Länderauswahl ist leer.");country.SelectedIndex=1;string land=(string)country.SelectedItem;
        var outside=save.Document.Units.Where(u=>u.Type=="garage").ToDictionary(u=>u.Id,u=>u.Get("status"));var shown=Garages().Items.Cast<GarageRow>().ToList();
        Assert(shown.All(r=>r.Country==land),"Länderfilter zeigt fremde Garagen.");Click(Find<Button>(root,"garages-upgrade"));
        Assert(save.Document.Units.Where(u=>u.Type=="garage").All(g=>shown.Any(r=>r.Id==g.Id)?g.Number("status") is 0 or 3:g.Get("status")==outside[g.Id]),"Ausbau hat Garagen anderer Länder verändert.");save.Reset();
        root=Prepare("garages");Assert(Find<ComboBox>(root,"garage-country").SelectedItem as string==land,"Länderauswahl geht beim Seitenwechsel verloren.");Find<ComboBox>(root,"garage-size").SelectedIndex=1;
        var scope=Garages().Items.Cast<GarageRow>().ToList();var levels=save.Document.Units.Where(u=>u.Type=="garage").ToDictionary(u=>u.Id,u=>u.Number("status"));int grown=scope.Count(r=>levels[r.Id] is 0 or 6);
        Assert(scope.Count>0&&Descendants(root).OfType<TextBlock>().Any(t=>t.Text==L.P("garage.scope",scope.Count)),"Umfang der Sammelaktion fehlt.");
        window.ConfirmForVerification=()=>true;try{Click(Find<Button>(root,"garages-buy-all"));}finally{window.ConfirmForVerification=null;}Layout(root,1364,861);
        Assert(save.Document.Units.Where(u=>u.Type=="garage").All(g=>scope.Any(r=>r.Id==g.Id)?g.Number("status")==(levels[g.Id]==3?3:2):g.Number("status")==levels[g.Id])
            &&TextOf(Find<TextBlock>(root,"commit-text")).Contains(grown>0?L.P("garage.bought",grown):L.F("garage.already",L.T("garage.small"))),"Kauf nach Land und Größe ändert falsche Garagen oder meldet eine falsche Zahl.");
        save.Reset();root=Prepare("garages");Find<ComboBox>(root,"garage-size").SelectedIndex=2;Find<ComboBox>(root,"garage-country").SelectedIndex=0;
        root=Prepare("garages");var size=Find<ComboBox>(root,"garage-size");Assert(size.Items.Count==3&&size.SelectedIndex==2,"Größenauswahl fehlt oder steht nicht auf groß.");size.SelectedIndex=0;
        var statuses=save.Document.Units.Where(u=>u.Type=="garage").ToDictionary(u=>u.Id,u=>u.Get("status"));Click(Find<Button>(root,"garages-upgrade"));
        Assert(save.Document.Units.Where(u=>u.Type=="garage").All(g=>g.Get("status")==statuses[g.Id]),"Ausbau auf Mini hat Garagen verändert.");
        root=Prepare("garages");Assert(Find<ComboBox>(root,"garage-size").SelectedIndex==0,"Größenauswahl geht beim Seitenwechsel verloren.");Find<ComboBox>(root,"garage-size").SelectedIndex=2;save.Reset();
        root=Prepare("trucks");var owned=SaveSession.OwnedVehicleIds(save.Document);Click(Find<Button>(root,"vehicles-repair"));
        Assert(owned.Count>0&&owned.All(id=>save.Document.Find(id).Fields.Where(f=>(f.Key.EndsWith("_wear")||f.Key.EndsWith("_wear_unfixable"))&&!f.Key.StartsWith("wheels_")).All(f=>SaveSession.FloatValue(f.Value)==0)),"Reparieren über die Fahrzeugseite fehlgeschlagen.");
        Click(Find<Button>(root,"undo-edit"));Assert(save.Document.Text==save.Original.Text,"Rückgängig nach Reparatur fehlgeschlagen.");
        root=Prepare("trucks");var vehicles=Descendants(root).OfType<DataGrid>().Single(g=>g.Name=="ScrollRegionTrucks");
        var vehicleInspector=Descendants(root).OfType<ScrollViewer>().Single(v=>v.Name=="ScrollRegionVehicle");
        Assert(vehicles.Items.Cast<VehicleRow>().Any(r=>r.Truck)&&vehicles.Items.Cast<VehicleRow>().Any(r=>!r.Truck&&r.Plate.Length>0&&TextOf(vehicles).Contains(r.Plate)),"Beispiel braucht Lkw und Anhänger mit Kennzeichen in der Tabelle.");
        foreach(var row in vehicles.Items.Cast<VehicleRow>())
        {
            vehicles.SelectedItem=row;
            Assert(Find<Button>(root,"vehicle-refuel").IsEnabled==row.Truck&&Find<Button>(root,"vehicle-repair").IsEnabled,"Fahrzeug-Inspektor zeigt falsche Aktionen.");
            Assert(row.Truck?Has(root,"plate-input")&&Find<TextBox>(root,"plate-input").Text==row.Plate&&Has(root,"plate-change")
                :!Has(root,"plate-input")&&!Has(root,"plate-change")&&!TextOf(vehicleInspector).Contains(L.T("plate.label"))&&!TextOf(vehicleInspector).Contains(row.Plate),"Kennzeichenfeld bei Lkw fehlt oder bei Anhänger vorhanden: "+row.Kind);
        }
        var trailerRow=vehicles.Items.Cast<VehicleRow>().First(r=>!r.Truck);
        try{save.SetLicensePlate(trailerRow.Id,"WERK 18");throw new Exception("Anhängerkennzeichen angenommen.");}catch(InvalidDataException e){Assert(e.Message==L.T("core.plate.trailer")&&!save.Dirty,"Anhängerkennzeichen falsch abgelehnt.");}
        var truck=vehicles.Items.Cast<VehicleRow>().First(r=>r.Truck);vehicles.SelectedItem=truck;Click(Find<Button>(root,"vehicle-refuel"));Assert(SaveSession.FloatValue(save.Document.Find(truck.Id).Get("fuel_relative"))==1,"Einzelnes Betanken fehlgeschlagen.");save.Reset();
        root=Prepare("trucks");vehicles=Descendants(root).OfType<DataGrid>().Single(g=>g.Name=="ScrollRegionTrucks");var plated=(VehicleRow)vehicles.SelectedItem;Assert(plated.Truck,"Erste Zeile ist kein Lkw.");
        Find<TextBox>(root,"plate-input").Text="WERK 18";Click(Find<Button>(root,"plate-change"));Layout(root,1364,861);
        vehicles=Descendants(root).OfType<DataGrid>().Single(g=>g.Name=="ScrollRegionTrucks");
        Assert(SaveSession.PlateText(save.Document.Find(plated.Id).Get("license_plate"))=="WERK 18"&&vehicles.Items.Cast<VehicleRow>().Single(r=>r.Id==plated.Id).Plate=="WERK 18","Kennzeichen wird nicht vorgemerkt oder nicht in der Tabelle gezeigt.");
        var plateReview=window.ReviewPanel(new Window());Layout(plateReview,720,560);
        Assert(Descendants(plateReview).OfType<DataGrid>().Single().Items.Count==1&&TextOf(plateReview).Contains(L.T("plate.label"))&&TextOf(plateReview).Contains("WERK 18"),"Prüfdialog zeigt das neue Kennzeichen nicht.");save.Reset();
        root=Prepare("trucks");vehicles=Descendants(root).OfType<DataGrid>().Single(g=>g.Name=="ScrollRegionTrucks");var measured=(VehicleRow)vehicles.SelectedItem;var odometer=Find<TextBox>(root,"odometer-input");
        Assert(odometer.Text==CompactNumber.Full(save.Document.Find(measured.Id).Number("odometer"))&&!Find<Button>(root,"odometer-set").IsEnabled,"Kilometerstand fehlt im Inspektor oder Setzen ist ohne Änderung aktiv.");
        foreach(string bad in new[]{"10.000.000","abc","-5"})
        {
            odometer.Text=bad;Click(Find<Button>(root,"odometer-set"));var error=Find<TextBlock>(root,"odometer-error");
            Assert(Has(root,"odometer-error")&&error.Foreground==Ui.Danger&&error.Text==L.F("input.range",L.T("odometer.label"),CompactNumber.Full(0),CompactNumber.Full(SaveSession.MaxOdometer))&&!save.Dirty,"Ungültiger Kilometerstand ohne Fehlermeldung oder trotzdem vorgemerkt: "+bad);
        }
        odometer.Text=CompactNumber.Full(123_456);Assert(!Has(root,"odometer-error"),"Fehlermeldung bleibt nach neuer Eingabe stehen.");Click(Find<Button>(root,"odometer-set"));Layout(root,1364,861);
        var odometerUnit=save.Document.Find(measured.Id);vehicles=Descendants(root).OfType<DataGrid>().Single(g=>g.Name=="ScrollRegionTrucks");
        Assert(odometerUnit.Get("odometer")=="123456"&&odometerUnit.Get("integrity_odometer")=="123456"&&odometerUnit.Get("odometer_float_part")==SaveSession.Zero&&vehicles.Items.Cast<VehicleRow>().Single(r=>r.Id==measured.Id).Odometer==L.F("unit.km",CompactNumber.Full(123_456)),"Kilometerstand wird nicht vorgemerkt oder nicht in der Tabelle gezeigt.");
        var odometerReview=window.ReviewPanel(new Window());Layout(odometerReview,720,560);
        Assert(Descendants(odometerReview).OfType<DataGrid>().Single().Items.Count==1&&TextOf(odometerReview).Contains(L.T("odometer.label"))&&TextOf(odometerReview).Contains(L.F("unit.km",CompactNumber.Full(123_456)))&&TextOf(odometerReview).Contains(L.P("review.count",1)),"Prüfdialog zeigt den Kilometerstand nicht als eine Zeile.");save.Reset();
        string[] shortcuts=["shortcut-maxout","shortcut-money","shortcut-level","shortcut-skills","shortcut-garages-buy","shortcut-garages-upgrade","shortcut-cities","shortcut-dealers","shortcut-agencies","shortcut-repair","shortcut-refuel","shortcut-drivers","shortcut-bus","shortcut-clean"];
        root=Prepare("driver");
        Assert(Descendants(root).OfType<Button>().Select(AutomationProperties.GetAutomationId).Where(shortcuts.Contains).SequenceEqual(shortcuts),"Schnellaktionen fehlen oder stehen in falscher Reihenfolge.");
        bool Done(Button b)=>!b.IsEnabled&&AutomationProperties.GetItemStatus(b)==L.T("shortcut.done");
        void Progress(string where)
        {
            var rows=shortcuts.Select(id=>Find<Button>(root,id)).ToList();int done=rows.Count(Done),open=rows.Count(b=>b.IsEnabled);
            Assert(Find<TextBlock>(root,"shortcuts-progress").Text==L.F("shortcuts.progress",done,done+open),"Fortschritt der Schnellaktionen falsch: "+where);
        }
        Progress("Beispiel");int openAtStart=shortcuts.Count(id=>Find<Button>(root,id).IsEnabled);
        Assert(!Find<Button>(root,"shortcut-drivers").IsEnabled&&AutomationProperties.GetItemStatus(Find<Button>(root,"shortcut-drivers"))==L.T("drivers.none")&&(Find<Button>(root,"shortcut-drivers").ToolTip as string)!.StartsWith(L.T("drivers.none"),StringComparison.Ordinal)
            &&!Find<Button>(root,"shortcut-clean").IsEnabled&&AutomationProperties.GetItemStatus(Find<Button>(root,"shortcut-clean"))==L.T("shortcut.why.record"),"Nicht zutreffende Schnellaktionen nennen keinen Grund.");
        var ownedIds=SaveSession.OwnedVehicleIds(save.Document).ToList();var truckIds=ownedIds.Where(id=>save.Document.Find(id).Type=="vehicle").ToList();
        var dealersBefore=save.UnlockedDealers().ToList();var agenciesBefore=save.UnlockedRecruitments().ToList();int largeBefore=save.Document.Units.Count(u=>u.Type=="garage"&&u.Number("status")==3);
        var expected=new Dictionary<string,Func<bool>>
        {
            ["shortcut-maxout"]=()=>!save.MaxOutPending().Any&&save.Document.Bank!.Number("money_account")==long.MaxValue&&save.KnownCities().All(save.VisitedCities().Contains),
            ["shortcut-money"]=()=>save.Document.Bank!.Number("money_account")==long.MaxValue&&Find<TextBox>(root,"money-input").Text==CompactNumber.Full(long.MaxValue)&&Find<TextBox>(root,"money-input").IsReadOnly
                &&Find<ToggleButton>(root,"max-money").IsChecked==true&&Find<TextBlock>(root,"money-limit-hint").Text==L.T("money.limit")&&TextOf(Find<TextBlock>(root,"commit-text")).Contains(L.T("shortcut.money.done")),
            ["shortcut-level"]=()=>save.Document.Economy!.Number("experience_points")==int.MaxValue&&Find<ToggleButton>(root,"max-level").IsChecked==true&&Find<TextBox>(root,"xp-input").IsReadOnly&&Find<TextBlock>(root,"xp-limit-hint").Text==L.T("xp.limit"),
            ["shortcut-skills"]=()=>SaveSession.SkillKeys.All(k=>save.Document.Economy!.Number(k)==6)&&save.Document.Economy!.Number("adr")==63
                &&SaveSession.SkillKeys.All(k=>AutomationProperties.GetItemStatus(Find<Button>(root,$"skill-{k}-6"))==L.T("skills.status.current"))&&Enumerable.Range(0,6).All(i=>Find<ToggleButton>(root,$"adr-{i}").IsChecked==true),
            ["shortcut-garages-buy"]=()=>save.GaragesPending(true)==0&&save.Document.Units.Count(u=>u.Type=="garage"&&u.Number("status")==3)>largeBefore,
            ["shortcut-garages-upgrade"]=()=>save.GaragesPending(false)==0&&save.Document.Units.Where(u=>u.Type=="garage"&&u.Number("status")>0).All(g=>g.Number("status")==3)&&save.Document.Units.Count(u=>u.Type=="garage"&&u.Number("status")==0)==save.Original.Units.Count(u=>u.Type=="garage"&&u.Number("status")==0),
            ["shortcut-cities"]=()=>save.KnownCities().All(save.VisitedCities().Contains)&&save.UnlockedDealers().SequenceEqual(dealersBefore),
            ["shortcut-dealers"]=()=>save.KnownCities().All(save.UnlockedDealers().Contains)&&save.UnlockedRecruitments().SequenceEqual(agenciesBefore),
            ["shortcut-agencies"]=()=>save.KnownCities().All(save.UnlockedRecruitments().Contains)&&save.UnlockedDealers().SequenceEqual(dealersBefore),
            ["shortcut-repair"]=()=>save.ServicePending(ownedIds,true,false,true).Count==0&&save.ServicePending(truckIds,false,true,false).Count>0,
            ["shortcut-refuel"]=()=>truckIds.All(id=>SaveSession.FloatValue(save.Document.Find(id).Get("fuel_relative"))==1)&&save.ServicePending(ownedIds,true,false,true).Count>0,
            ["shortcut-drivers"]=()=>save.DriverSkillsPending()==0,
            ["shortcut-bus"]=()=>save.BusStopsPending()==0,
            ["shortcut-clean"]=()=>save.CleanRecordPending()==0,
        };
        window.ConfirmForVerification=()=>true;int acted=0;
        try
        {
            foreach(string id in shortcuts)
            {
                root=Prepare("driver");var row=Find<Button>(root,id);string help=AutomationProperties.GetHelpText(row);
                Assert(help.Length>0&&row.ToolTip is string tip&&tip.EndsWith(help,StringComparison.Ordinal)&&AutomationProperties.GetName(row).Length>0,"Schnellaktion ohne Titel oder Beschreibung: "+id);
                if(!row.IsEnabled)continue;
                string before=save.Document.Text;Click(row);Layout(root,1364,861);acted++;
                Assert(save.Document.Text!=before&&expected[id](),"Schnellaktion merkt nicht das Erwartete vor: "+id);
                Assert(Done(Find<Button>(root,id)),"Schnellaktion zeigt nach dem Ausführen nicht „erledigt“: "+id);Progress(id);
                Click(Find<Button>(root,"undo-edit"));Layout(root,1364,861);
                Assert(save.Document.Text==before&&!save.Dirty&&Find<Button>(root,id).IsEnabled,"Rückgängig stellt den Stand vor der Schnellaktion nicht her: "+id);
            }
        }
        finally{window.ConfirmForVerification=null;}
        Assert(acted==openAtStart&&acted>=10,"Unerwartete Zahl offener Schnellaktionen im Beispiel: "+acted);
        window.ConfirmForVerification=()=>true;try{root=Prepare("driver");Click(Find<Button>(root,"shortcut-maxout"));Layout(root,1364,861);}finally{window.ConfirmForVerification=null;}
        Assert(shortcuts.Take(9).All(id=>Done(Find<Button>(root,id)))&&Find<Button>(root,"shortcut-repair").IsEnabled,"Max-Out markiert seine Zeilen nicht als erledigt.");save.Reset();
        root=Prepare("driver");Click(Find<Button>(root,"shortcut-cities"));
        var cityReview=window.ReviewPanel(new Window());Layout(cityReview,720,560);
        Assert(Descendants(cityReview).OfType<DataGrid>().Single().Items.Count==1&&TextOf(cityReview).Contains(L.T("review.visited"))&&TextOf(cityReview).Contains(CompactNumber.Full(save.VisitedCities().Count))&&TextOf(cityReview).Contains(L.P("review.count",1)),"Prüfdialog zeigt die besuchten Städte nicht als eine Zeile.");save.Reset();
        root=Prepare("driver");Click(Find<Button>(root,"shortcut-dealers"));Layout(root,1364,861);Click(Find<Button>(root,"shortcut-agencies"));
        var unlockReview=window.ReviewPanel(new Window());Layout(unlockReview,720,560);
        Assert(Descendants(unlockReview).OfType<DataGrid>().Single().Items.Count==2&&TextOf(unlockReview).Contains(L.T("review.dealers"))&&TextOf(unlockReview).Contains(L.T("review.agencies"))&&TextOf(unlockReview).Contains(CompactNumber.Full(save.UnlockedDealers().Count))&&TextOf(unlockReview).Contains(L.P("review.count",2)),"Prüfdialog zeigt Händler und Agenturen nicht als zwei Zeilen.");save.Reset();
        root=Prepare("driver");Click(Find<Button>(root,"shortcut-bus"));var (found,stops)=save.BusStops();Assert(stops>0&&found==stops,"Busbahnhöfe werden nicht entdeckt.");
        var busReview=window.ReviewPanel(new Window());Layout(busReview,720,560);
        Assert(Descendants(busReview).OfType<DataGrid>().Single().Items.Count==1&&TextOf(busReview).Contains(L.T("review.busstops"))&&TextOf(busReview).Contains(L.F("review.busstops.value",CompactNumber.Full(stops))),"Prüfdialog zeigt die Busbahnhöfe nicht als eine Zeile.");save.Reset();
        var log=save.Document.Resolve(save.Document.Economy,"police_offence_log")!;save.Edit(d=>d.Set(log.Id,"offence_total_counts[2]","4"));
        string recordFile=Path.Combine(Path.GetDirectoryName(save.FilePath)!,"record-test","game.sii");Directory.CreateDirectory(Path.GetDirectoryName(recordFile)!);File.WriteAllText(recordFile,save.Document.Text);save.Reset();
        var record=SaveSession.Open(recordFile);root=window.PrepareForCapture(record,install,cities,"driver",1364);Layout(root,1364,861);
        Assert(Find<Button>(root,"shortcut-clean").IsEnabled&&AutomationProperties.GetItemStatus(Find<Button>(root,"shortcut-clean"))==L.P("count.offences",4),"Strafregister-Aktion zeigt Verstöße nicht.");
        Click(Find<Button>(root,"shortcut-clean"));Layout(root,1364,861);Assert(record.Offences()==0&&record.Dirty&&Done(Find<Button>(root,"shortcut-clean")),"Strafregister wird nicht geleert oder nicht als erledigt gezeigt.");
        var recordReview=window.ReviewPanel(new Window());Layout(recordReview,720,560);
        Assert(Descendants(recordReview).OfType<DataGrid>().Single().Items.Count==1&&TextOf(recordReview).Contains(L.T("review.record"))&&TextOf(recordReview).Contains(L.P("count.offences",4))&&TextOf(recordReview).Contains(L.P("count.offences",0)),"Prüfdialog zeigt das Strafregister nicht als eine Zeile.");
        Directory.Delete(Path.GetDirectoryName(recordFile)!,true);Prepare("driver");
        save.Edit(d=>d.Set(d.Bank!.Id,"money_account","1000000"));var review=window.ReviewPanel(new Window());Layout(review,720,560);var changes=Descendants(review).OfType<DataGrid>().Single();
        Assert(Has(review,"review-save")&&Has(review,"review-copy")&&changes.Items.Count==1&&TextOf(review).Contains(L.F("money.value",CompactNumber.Full(1_000_000))),"Prüfdialog zeigt die Änderung oder die Schaltflächen nicht.");save.Reset();
        window.PrepareForCapture(save,install,cities,"driver",1364);var backups=window.BackupsPanel(new Window());Layout(backups,760,520);var table=Descendants(backups).OfType<DataGrid>().Single();
        Assert(table.Items.Count==1&&Find<Button>(backups,"backup-restore").IsEnabled&&Has(backups,"controls-restore"),"Backups dieses Spielstands oder die Steuerungswiederherstellung fehlen.");
        Find<CheckBox>(backups,"backup-current").IsChecked=false;Assert(table.Items.Count==2,"Alle Backups werden nicht gezeigt.");
        root=Prepare("driver",1);Click(Find<Button>(root,"guide-next"));Assert(Has(root,"guide-back")&&TextOf(root).Contains(L.T("guide.2.title")),"Anleitung blättert nicht weiter.");
        Click(Find<Button>(root,"guide-back"));Assert(!Has(root,"guide-back")&&TextOf(root).Contains(L.T("guide.1.title")),"Anleitung blättert nicht zurück.");
        Click(Find<Button>(root,"guide-skip"));Assert(!Has(root,"guide-next")&&Settings.Number("guide")==1,"Überspringen schließt die Anleitung nicht oder merkt sie nicht.");
        Assert(SaveSession.Hash(File.ReadAllBytes(save.FilePath))==save.OriginalHash,"UI-Prüfung hat die Spielstandkopie verändert.");
    }
    static bool Shown(UIElement e)=>e.Visibility==Visibility.Visible&&Ancestors(e).OfType<UIElement>().All(a=>a.Visibility==Visibility.Visible);
    static string TextOf(DependencyObject root)=>string.Join(" ",Descendants(root).OfType<TextBlock>().Where(Shown).Select(Plain));
    static string Plain(TextBlock t)=>t.Inlines.Count>0?string.Concat(t.Inlines.OfType<System.Windows.Documents.Run>().Select(r=>r.Text)):t.Text;
    static void Layout(FrameworkElement root,double width,double height)
    {
        void Pass(){root.Measure(new Size(width,height));root.Arrange(new Rect(0,0,width,double.IsInfinity(height)?root.DesiredSize.Height:height));root.UpdateLayout();}
        Pass();var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);Pass();
    }
    static void Save(Visual visual,int width,int height,string file)
    {
        var image=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);image.Render(visual);
        var pixels=new byte[width*height*4];image.CopyPixels(pixels,width*4,0);int bright=0;for(int i=0;i<pixels.Length;i+=4)if(pixels[i]>235&&pixels[i+1]>235&&pixels[i+2]>235)bright++;
        if((double)bright/(width*height)>.15)throw new Exception("Unerwartet große weiße Fläche: "+Path.GetFileName(file));
        using var stream=File.Create(file);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));encoder.Save(stream);
    }
    /// <summary>Builds the 3D preview of every owned truck and trailer in <paramref name="savePath"/> and renders each from three angles.</summary>
    public static void RenderPreviews(string savePath,string folder,string report)
    {
        Directory.CreateDirectory(folder);var save=SaveSession.Open(savePath);var install=Installation.Detect();var results=new List<object>();
        using var files=GameFiles.Open(install);
        foreach(var unit in save.OwnedVehicles())
        {
            var watch=System.Diagnostics.Stopwatch.StartNew();var spec=VehiclePreview.Describe(save.Document,unit);var built=VehiclePreview.Build(files,spec);
            var stage=new VehicleStage();var host=new Border{Child=stage,Width=1000,Height=520};
            Layout(host,1000,520);stage.Show(built);
            foreach(var (name,yaw) in new[]{("front",-140.0),("side",-90.0),("rear",40.0)})
            {
                stage.Turn(yaw);Layout(host,1000,520);
                string file=Path.Combine(folder,$"{unit.Id.Replace('.','_')}-{name}.png");
                var image=new RenderTargetBitmap(1000,520,96,96,PixelFormats.Pbgra32);image.Render(host);
                using var stream=File.Create(file);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));encoder.Save(stream);
            }
            results.Add(new{unit.Id,parts=spec.Parts.Count,built.Missing,meshes=built.Model.Children.Count,ms=watch.ElapsedMilliseconds});
        }
        File.WriteAllText(report,JsonSerializer.Serialize(new{results,files.Warnings},new JsonSerializerOptions{WriteIndented=true}));
    }
    /// <summary>Renders a dialog or popup body at its size; height 0 sizes to content.</summary>
    static void Capture(FrameworkElement element,int width,int height,string file)
    {
        var host=new Border{Child=element,Background=Ui.Surface,Width=width};if(height>0)host.Height=height;
        Layout(host,width,height>0?height:double.PositiveInfinity);
        Check(host,Path.GetFileName(file));Save(host,width,(int)Math.Ceiling(host.ActualHeight),file);host.Child=null;
    }
    static IEnumerable<DependencyObject> Ancestors(DependencyObject node){for(var parent=VisualTreeHelper.GetParent(node);parent!=null;parent=VisualTreeHelper.GetParent(parent))yield return parent;}
    static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        yield return node;for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)foreach(var child in Descendants(VisualTreeHelper.GetChild(node,i)))yield return child;
    }
}
