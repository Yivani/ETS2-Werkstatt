using Microsoft.Win32;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Ellipse=System.Windows.Shapes.Ellipse;
using System.Windows.Threading;

namespace Werkstatt;

public sealed partial class MainWindow : Window
{
    Installation install=new();SaveSession? session;MapData? mapData;XpCurve xpCurve=XpCurve.Standard;Dictionary<string,CityInfo> cityIndex=[];List<CityInfo> cityDefinitions=[];List<string> cityWarnings=[];
    ContentControl content=new(),notice=new();Grid host=new(),guideLayer=new();
    TextBlock commitText=new(),switcherLabel=new(),indicatorText=new();Ellipse commitDot=new(),indicatorDot=new();
    Button reviewButton=new(),undoButton=new(),redoButton=new(),discardButton=new();ToggleButton switcher=new(),indicator=new(),languages=new();
    readonly List<ToggleButton> tabs=[];readonly CancellationTokenSource cancel=new();
    readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromSeconds(3)},messageTimer=new(){Interval=TimeSpan.FromSeconds(8)};
    internal static readonly string[] Pages=["driver","garages","trucks"];
    string currentPage="driver";bool loading,closing,formDirty,writing,gameRunning,checkingGame,scanned,wide;
    Func<bool>? applyForm;Action<bool>? adapt;
    (string Text,Brush Color,bool Ok)? message;
    string? openError;
    readonly string? initialFile;
    static string Version=>typeof(MainWindow).Assembly.GetName().Version?.ToString(3)??"";

    public MainWindow(string? path)
    {
        initialFile=path;Width=1380;Height=900;MinWidth=1040;MinHeight=680;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        Background=Ui.Bg;Foreground=Ui.Ink;FontSize=13;
        SourceInitialized+=(s,e)=>Ui.DarkTitleBar(this);
        BuildShell();
        gameRunning=SaveSession.GameRunning;
        timer.Tick+=async(s,e)=>{if(checkingGame)return;checkingGame=true;try{gameRunning=await Task.Run(()=>SaveSession.GameRunning);UpdateState();}catch(Exception){}finally{checkingGame=false;}};timer.Start();
        messageTimer.Tick+=(s,e)=>{ClearMessage();UpdateState();};
        recheckTimer.Tick+=async(s,e)=>await Recheck();Activated+=async(s,e)=>{if(recheckTimer.IsEnabled)await Recheck();};
        Closing+=OnClosing;
        Loaded+=async(s,e)=>await Initialize();
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Save,(s,e)=>ShowChanges()));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Save,Key.S,ModifierKeys.Control));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Open,(s,e)=>Browse()));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Open,Key.O,ModifierKeys.Control));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Undo,(s,e)=>History(false),(s,e)=>e.CanExecute=guideStep==0&&!writing&&(formDirty||session?.CanUndo==true)));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Redo,(s,e)=>History(true),(s,e)=>e.CanExecute=guideStep==0&&!writing&&!formDirty&&session?.CanRedo==true));
        PreviewKeyDown+=OnKey;
    }
    void OnKey(object sender,KeyEventArgs e)
    {
        bool control=Keyboard.Modifiers==ModifierKeys.Control;
        if(guideStep>0){if(e.Key==Key.Escape){CloseGuide();e.Handled=true;}else if(e.Key==Key.Enter&&Keyboard.FocusedElement is not ButtonBase){GuideNext();e.Handled=true;}return;}
        if(e.Key==Key.F1){ShowGuide(1);e.Handled=true;}
        else if(control&&e.Key==Key.B){ShowBackups();e.Handled=true;}
        else if(control&&e.Key is Key.D1 or Key.D2 or Key.D3 or Key.NumPad1 or Key.NumPad2 or Key.NumPad3&&Editable){ClearMessage();Navigate(Pages[e.Key is Key.D1 or Key.NumPad1?0:e.Key is Key.D2 or Key.NumPad2?1:2]);e.Handled=true;}
    }
    /// <summary>Builds the window chrome in the current language. Called again after a language switch.</summary>
    void BuildShell()
    {
        Title="ETS2 Werkstatt · "+Version;Language=System.Windows.Markup.XmlLanguage.GetLanguage(L.Culture.IetfLanguageTag);FontFamily=Ui.Font;
        var root=Ui.Grid("*","Auto,*,Auto");root.Background=Ui.Bg;
        Ui.At(root,TopBar());
        host=Ui.Grid("*","Auto,*");notice=Ui.At(host,new ContentControl());content=Ui.At(host,new ContentControl(),0,1);Ui.At(root,host,0,1);ApplyWidth(wide?1200:0);
        Ui.At(root,CommitBar(),0,2);
        guideLayer=new Grid{Visibility=Visibility.Collapsed};guidePanel=null;Grid.SetRowSpan(guideLayer,3);root.Children.Add(guideLayer);
        root.SizeChanged+=(s,e)=>ApplyWidth(e.NewSize.Width);
        Content=root;SyncTabs();
    }
    /// <summary>Below 1200 px the screen gets 24/20 padding and narrower gutters; pages adapt in place, so no input is lost.</summary>
    void ApplyWidth(double width)
    {
        bool next=width>=1200;host.Margin=next?new(32,24,32,24):new(24,20,24,20);if(guidePanel!=null)guidePanel.Margin=new(0,next?160:96,0,0);
        if(next!=wide){wide=next;adapt?.Invoke(wide);}
    }
    FrameworkElement TopBar()
    {
        var bar=new DockPanel{Height=48,LastChildFill=true};
        var mark=new Image{Source=Ui.Logo(true),Width=22,Height=22,Margin=new(15,0,11,0),ToolTip="ETS2 Werkstatt "+Version};RenderOptions.SetEdgeMode(mark,EdgeMode.Unspecified);
        DockPanel.SetDock(mark,Dock.Left);bar.Children.Add(mark);
        switcherLabel=new TextBlock{TextWrapping=TextWrapping.NoWrap,TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center};
        var chevron=new System.Windows.Shapes.Path{Data=Geometry.Parse("M0 0 L4 4 L8 0"),Stroke=Ui.Muted,StrokeThickness=1.5,Width=8,Height=4,Margin=new(10,0,0,0),VerticalAlignment=VerticalAlignment.Center};
        var face=new DockPanel();DockPanel.SetDock(chevron,Dock.Right);face.Children.Add(chevron);face.Children.Add(switcherLabel);
        switcher=Ui.Id(new ToggleButton{Style=(Style)FindResource("Switcher"),Content=face,MinWidth=240,MaxWidth=320,VerticalAlignment=VerticalAlignment.Center},"save-selector");AutomationProperties.SetName(switcher,L.T("saves.choose"));
        DockPanel.SetDock(switcher,Dock.Left);bar.Children.Add(switcher);
        var divider=new Border{Width=1,Height=20,Background=Ui.Line,Margin=new(16,0,16,0)};DockPanel.SetDock(divider,Dock.Left);bar.Children.Add(divider);
        var tabRow=new StackPanel{Orientation=Orientation.Horizontal};tabs.Clear();
        foreach(var page in Pages)
        {
            var tab=Ui.Id(new ToggleButton{Style=(Style)FindResource("Tab"),Content=L.T("tab."+page),Tag=page},"nav-"+page);AutomationProperties.SetName(tab,L.T("tab."+page));
            tab.Click+=(s,e)=>{ClearMessage();Navigate(page);};tabs.Add(tab);tabRow.Children.Add(tab);
        }
        DockPanel.SetDock(tabRow,Dock.Left);bar.Children.Add(tabRow);
        indicatorDot=Ui.Dot(null);indicatorDot.Margin=new(0,0,8,0);indicatorText=new TextBlock{TextWrapping=TextWrapping.NoWrap,TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center};
        indicator=Ui.Id(new ToggleButton{Style=(Style)FindResource("BarToggle"),Content=Ui.Row(indicatorDot,indicatorText),MaxWidth=220},"status-indicator");
        var backups=Ui.Id(Ui.IconButton(Ui.IconBackup,L.T("backup.open"),ShowBackups),"backups-open");backups.Margin=new(8,0,4,0);
        var help=Ui.Id(Ui.IconButton(Ui.IconHelp,L.T("help.open"),()=>ShowGuide(1)),"help-open");help.Margin=new(0,0,4,0);
        languages=Ui.Id(new ToggleButton{Style=(Style)FindResource("BarToggle"),Content=L.Code.ToUpperInvariant(),MinWidth=36,FontWeight=FontWeights.SemiBold,HorizontalContentAlignment=HorizontalAlignment.Center,ToolTip=L.T("language")},"language");AutomationProperties.SetName(languages,L.T("language"));
        var right=Ui.Row(indicator,backups,help,languages);right.Margin=new(16,0,12,0);right.VerticalAlignment=VerticalAlignment.Center;DockPanel.SetDock(right,Dock.Right);bar.Children.Add(right);
        bar.Children.Add(new Border());
        var layer=new Grid();layer.Children.Add(bar);
        foreach(var popup in new[]{Ui.Flyout(switcher,SwitcherPanel,360),Ui.Flyout(indicator,IndicatorPanel,320,true),Ui.Flyout(languages,LanguagePanel,200,true)})layer.Children.Add(popup);
        UpdateIndicator();
        return new Border{Background=Ui.Chrome,BorderBrush=Ui.Line,BorderThickness=new(0,0,0,1),Child=layer};
    }
    FrameworkElement CommitBar()
    {
        var bar=new DockPanel{Height=52};
        undoButton=Ui.Id(Ui.IconButton(Ui.IconUndo,L.T("action.undo"),()=>History(false)),"undo-edit");redoButton=Ui.Id(Ui.IconButton(Ui.IconRedo,L.T("action.redo"),()=>History(true)),"redo-edit");
        undoButton.Margin=new(0,0,4,0);redoButton.Margin=new(0,0,12,0);
        discardButton=Ui.Id(Ui.Ghost(L.T("action.reset"),Reset),"reset-edits");discardButton.Margin=new(0,0,8,0);
        reviewButton=Ui.Id(Ui.Button(L.T("action.review"),ShowChanges,true),"review-changes");reviewButton.MinWidth=160;
        var actions=Ui.Row(undoButton,redoButton,discardButton,reviewButton);actions.Margin=new(16,0,16,0);actions.VerticalAlignment=VerticalAlignment.Center;DockPanel.SetDock(actions,Dock.Right);bar.Children.Add(actions);
        commitDot=Ui.Dot(Ui.Ok);commitDot.Margin=new(0,0,8,0);commitDot.Visibility=Visibility.Collapsed;
        commitText=Ui.Text("",13,Ui.Muted);commitText.TextWrapping=TextWrapping.NoWrap;commitText.TextTrimming=TextTrimming.CharacterEllipsis;commitText.VerticalAlignment=VerticalAlignment.Center;Ui.Id(commitText,"commit-text");
        var left=new DockPanel{Margin=new(24,0,0,0)};DockPanel.SetDock(commitDot,Dock.Left);left.Children.Add(commitDot);left.Children.Add(commitText);bar.Children.Add(left);
        return new Border{Background=Ui.Chrome,BorderBrush=Ui.Line,BorderThickness=new(0,1,0,0),Child=bar};
    }
    FrameworkElement LanguagePanel()
    {
        var list=new StackPanel{Margin=new(6)};
        foreach(var language in L.Languages)
        {
            var row=Ui.RowButton(language.Name,null,AfterClosing(languages,()=>ChangeLanguage(language.Code)),language.Code==L.Code);
            if(language.Code==L.Code&&row.Content is DockPanel line){var check=Ui.Icon(Ui.IconCheck,14,Ui.Accent,2);DockPanel.SetDock(check,Dock.Right);line.Children.Insert(0,check);}
            list.Children.Add(row);
        }
        return list;
    }
    void ChangeLanguage(string code)
    {
        if(code==L.Code||!FlushForm())return;
        L.Use(code);L.Save();App.ApplyLanguage();titles.Clear();int step=guideStep;BuildShell();Navigate(currentPage);if(step>0)ShowGuide(step,false);
    }
    async Task Initialize()
    {
        try
        {
            install=await Task.Run(()=>Installation.Detect());
            var detected=install;var curve=Task.Run(()=>XpCurve.Load(detected.GamePath));var cities=LoadCities();
            var names=await Task.Run(()=>LocalSaves(detected).Select(s=>(s.Path,Title:TitleEntry(s.Path))).ToList());
            foreach(var (path,title) in names)titles[path]=title;
            xpCurve=await curve;await cities;scanned=true;
            string? file=initialFile??LocalSaves(install).FirstOrDefault()?.Path;
            if(session==null&&file!=null)await OpenFile(file);
            if(session==null)Navigate(currentPage);
        }
        catch(Exception e){scanned=true;Error(e);Navigate(currentPage);}
        finally{UpdateState();RefreshGuideLine();if(Settings.Number("guide")<GuideVersion&&guideStep==0)ShowGuide(1);}
    }
    /// <summary>Game saves on this PC, newest first. Steam Cloud saves are never listed.</summary>
    static IEnumerable<SaveFile> LocalSaves(Installation found)=>found.Saves.Where(s=>Path.GetFileName(s.Path).Equals("game.sii",StringComparison.OrdinalIgnoreCase));
    bool Editable=>session!=null&&Path.GetFileName(session.FilePath).Equals("game.sii",StringComparison.OrdinalIgnoreCase)&&session.Document.Economy!=null&&session.Document.Bank!=null;
    void Notify(string text,Brush? color=null,bool ok=false){message=(text,color??Ui.Ink,ok);messageTimer.Stop();if(ok)messageTimer.Start();UpdateState();}
    void ClearMessage(){message=null;messageTimer.Stop();}
    void Error(Exception e){Notify(e.Message,Ui.Danger);Ui.Ask(this,L.T("error.title"),e.Message,L.T("ok"));}
    void Run(Action action){ClearMessage();try{action();UpdateState();}catch(Exception e){Error(e);}}
    /// <summary>Answers confirmations without a dialog; only the offscreen UI check sets it.</summary>
    internal Func<bool>? ConfirmForVerification;
    bool Confirm(string title,string text,string ok)=>ConfirmForVerification?.Invoke()??Ui.Ask(this,title,text,ok,L.T("cancel"));
    void UpdateState()
    {
        int count=PendingCount;bool dirty=session?.Dirty==true||session!=null&&formDirty,pending=count>0||session!=null&&formDirty;
        reviewButton.IsEnabled=pending&&!loading&&!writing;discardButton.IsEnabled=dirty&&!loading&&!writing;
        undoButton.IsEnabled=!loading&&!writing&&(formDirty||session?.CanUndo==true);redoButton.IsEnabled=!loading&&!writing&&!formDirty&&session?.CanRedo==true;
        reviewButton.Content=count>0?L.F("action.review.count",count):L.T("action.review");
        commitText.Inlines.Clear();commitDot.Visibility=Visibility.Collapsed;
        void Add(string text,Brush color)=>commitText.Inlines.Add(new Run(text){Foreground=color});
        if(writing)Add(L.T("status.writing"),Ui.Muted);
        else if(loading&&session==null)Add(L.T("status.opening"),Ui.Muted);
        else if(message is {} m){Add(m.Text,m.Color);if(m.Ok)commitDot.Visibility=Visibility.Visible;}
        else if(count>0){Add(L.P("commit.pending",count),Ui.Ink);Add(" · "+string.Join(", ",ChangedAreas()),Ui.Muted);}
        else if(session!=null&&formDirty)Add(L.T("commit.input"),Ui.Ink);
        else if(session!=null)Add(L.T("commit.clean"),Ui.Muted);
        if(!writing&&message==null&&gameRunning&&pending)Add(" · "+L.T("commit.running"),Ui.Danger);
        commitText.ToolTip=commitText.Inlines.Count>0?string.Concat(commitText.Inlines.OfType<Run>().Select(r=>r.Text)):null;
        UpdateIndicator();UpdateSwitcher();
    }
    void UpdateIndicator()
    {
        Brush? dot=null;Brush color=Ui.Muted;string text=L.T("status.checking");string? tip=null;
        if(scanned&&install.GamePath==null){text=L.T("status.notfound");tip=L.T("status.notfound.tip");}
        else if(scanned&&gameRunning){dot=Ui.Danger;color=Ui.Ink;text=L.T("status.running");tip=L.T("status.running.tip");}
        else if(scanned){dot=Ui.Ok;text=GameLine();tip=L.T("status.closed.tip");}
        if(indicatorDot.Fill!=dot||indicatorText.Text!=text)
        {
            indicatorDot.Fill=dot;indicatorDot.Stroke=dot==null?Ui.Subtle:null;indicatorText.Text=text;indicatorText.Foreground=color;indicator.ToolTip=tip;
            AutomationProperties.SetName(indicator,scanned&&install.GamePath!=null&&!gameRunning?L.F("status.a11y",install.Version,install.Dlcs.Count):text);
        }
    }
    string GameLine()=>string.Join(" · ",new[]{("ETS2 "+install.Version).Trim(),L.F("status.dlc",install.Dlcs.Count)});
    void UpdateSwitcher()
    {
        switcherLabel.Inlines.Clear();
        if(session==null){switcherLabel.Inlines.Add(new Run(L.T("file.none")){Foreground=Ui.Muted});switcher.ToolTip=null;return;}
        switcherLabel.Inlines.Add(new Run(ProfileName(session.FilePath)+" · "){Foreground=Ui.Muted});switcherLabel.Inlines.Add(new Run(SaveTitle(session.FilePath)){Foreground=Ui.Ink});switcher.ToolTip=session.FilePath;
    }
    /// <summary>Order of save kinds in the switcher: 0 manual, 1 quick save, 2 autosaves (incl. job and drive), 3 multiplayer.</summary>
    internal static int SlotKind(string path)
    {
        string slot=Path.GetFileName(Path.GetDirectoryName(path))??"";
        return slot.Equals("quicksave",StringComparison.OrdinalIgnoreCase)?1:slot.StartsWith("autosave",StringComparison.OrdinalIgnoreCase)?2:slot.Equals("multi",StringComparison.OrdinalIgnoreCase)?3:0;
    }
    internal FrameworkElement SwitcherPanel()
    {
        var panel=new DockPanel();var groups=new StackPanel{Margin=new(6)};
        var saves=LocalSaves(install).ToList();
        if(session!=null&&!saves.Any(s=>Same(s.Path,session.FilePath)))saves.Insert(0,new SaveFile("",session.FilePath,File.GetLastWriteTime(session.FilePath)));
        foreach(var group in saves.GroupBy(s=>ProfileName(s.Path)))
        {
            var head=Ui.Label(group.Key);head.FontWeight=FontWeights.SemiBold;head.Margin=new(10,groups.Children.Count==0?4:12,10,4);groups.Children.Add(head);
            foreach(var kind in group.GroupBy(s=>SlotKind(s.Path)).OrderBy(k=>k.Key))
            {
                var sub=Ui.Caption(L.T("switcher.kind."+kind.Key));sub.Margin=new(10,6,10,2);groups.Children.Add(sub);
                foreach(var save in kind.OrderByDescending(s=>s.Modified))groups.Children.Add(Ui.RowButton(SaveTitle(save.Path),When(save.Modified),AfterClosing(switcher,async()=>await OpenFile(save.Path)),session!=null&&Same(save.Path,session.FilePath)));
            }
        }
        if(saves.Count==0)groups.Children.Add(new TextBlock{Text=L.T("file.none"),Foreground=Ui.Muted,Margin=new(10,8,10,8)});
        var footer=new StackPanel{Margin=new(6)};
        footer.Children.Add(Ui.RowButton(L.T("action.open"),L.T("key.open"),AfterClosing(switcher,Browse)));
        var copy=Ui.Id(Ui.RowButton(L.T("review.copy"),null,AfterClosing(switcher,()=>ExportCopy())),"export-copy");copy.IsEnabled=Editable&&!writing;footer.Children.Add(copy);
        if(install.CloudSaves>0){var link=Ui.Link(L.P("switcher.cloud",install.CloudSaves),AfterClosing(switcher,()=>ShowGuide(1)));link.Margin=new(10,8,10,6);footer.Children.Add(link);}
        var foot=new Border{BorderBrush=Ui.Line,BorderThickness=new(0,1,0,0),Child=footer};DockPanel.SetDock(foot,Dock.Bottom);panel.Children.Add(foot);
        panel.Children.Add(new ScrollViewer{Name="ScrollRegionSaves",Content=groups,MaxHeight=300,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        return panel;
    }
    /// <summary>Closes the popup of <paramref name="owner"/> before the action, so no flyout stays over a dialog or a rebuilt window.</summary>
    static Action AfterClosing(ToggleButton owner,Action action)=>()=>{owner.IsChecked=false;action();};
    static bool Same(string a,string b)=>string.Equals(a,b,StringComparison.OrdinalIgnoreCase);
    static string When(DateTime time)=>time.Date==DateTime.Today?L.F("date.today",time.ToString("t",L.Culture)):time.Date==DateTime.Today.AddDays(-1)?L.T("date.yesterday"):time.ToString(time.Year==DateTime.Today.Year?L.T("date.short"):L.T("date.long"),L.Culture);
    internal FrameworkElement IndicatorPanel()
    {
        var p=new StackPanel{Margin=new(16)};
        p.Children.Add(Ui.Section(install.GamePath==null?L.T("status.notfound"):("ETS2 "+install.Version).Trim()));
        string? path=install.GamePath==null?null:RealCase(install.GamePath);
        var folder=Ui.Caption(path==null?L.T("status.notfound.tip"):Ui.MiddleTrim(path,46));folder.ToolTip=path;folder.Margin=new(0,4,0,0);p.Children.Add(folder);
        if(install.GamePath!=null)p.Children.Add(Ui.Caption(L.P("flyout.dlcs",install.Dlcs.Count)));
        var warnings=install.Warnings.Concat(cityWarnings).Concat(mapData?.Warnings??[]).ToList();
        if(warnings.Count>0)
        {
            var list=new StackPanel{Visibility=Visibility.Collapsed,Margin=new(0,6,0,0)};foreach(var w in warnings.Take(6)){var line=Ui.Trim(Ui.Caption(w));list.Children.Add(line);}
            var toggle=Ui.Link(L.P("flyout.warnings",warnings.Count),()=>list.Visibility=list.Visibility==Visibility.Visible?Visibility.Collapsed:Visibility.Visible);toggle.Margin=new(0,8,0,0);p.Children.Add(toggle);p.Children.Add(list);
        }
        var pick=Ui.Button(L.T("flyout.pick"),AfterClosing(indicator,PickGameFolder));pick.Margin=new(0,0,8,0);var again=Ui.Button(L.T("flyout.rescan"),AfterClosing(indicator,async()=>await Rescan(install.GamePath,false)));
        var actions=Ui.Row(pick,again);actions.Margin=new(0,16,0,0);p.Children.Add(actions);
        return p;
    }
    /// <summary>The path as the file system spells it; Steam's registry value is often all lower case.</summary>
    static string RealCase(string path)
    {
        try
        {
            var dir=new DirectoryInfo(Path.GetFullPath(path));var parts=new List<string>();
            for(var d=dir;d.Parent!=null;d=d.Parent)parts.Insert(0,d.Parent.EnumerateDirectories(d.Name).FirstOrDefault()?.Name??d.Name);
            return Path.Combine([dir.Root.FullName.ToUpperInvariant(),..parts]);
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException){return path;}
    }
    async void PickGameFolder()
    {
        if(loading)return;
        var dialog=new OpenFolderDialog{Title=L.T("flyout.pick.title")};if(dialog.ShowDialog(this)!=true)return;
        if(!File.Exists(Path.Combine(dialog.FolderName,"def.scs"))){Error(new InvalidDataException(L.T("flyout.pick.invalid")));return;}
        await Rescan(dialog.FolderName,true);
    }
    async Task Rescan(string? folder,bool resetMap)
    {
        if(loading)return;loading=true;scanned=false;UpdateState();
        try
        {
            install=await Task.Run(()=>Installation.Detect(folder));xpCurve=await Task.Run(()=>XpCurve.Load(install.GamePath));
            if(resetMap)mapData=null;mapFailed=false;ClosePreviews();await LoadCities();
        }
        catch(Exception e){Error(e);}
        finally{loading=false;scanned=true;UpdateState();RefreshGuideLine();if(session!=null&&currentPage=="garages")Navigate("garages");}
        if(mapData!=null)await LoadMap();
    }
    readonly Dictionary<string,(DateTime Stamp,string Title)> titles=new(StringComparer.OrdinalIgnoreCase);
    string SaveTitle(string path)
    {
        if(!Path.GetFileName(path).Equals("game.sii",StringComparison.OrdinalIgnoreCase))return Path.GetFileName(path);
        string info=Path.Combine(Path.GetDirectoryName(path)!,"info.sii");DateTime stamp=File.Exists(info)?File.GetLastWriteTimeUtc(info):DateTime.MinValue;
        if(titles.TryGetValue(path,out var known)&&known.Stamp==stamp)return known.Title;
        var entry=TitleEntry(path);titles[path]=entry;return entry.Title;
    }
    static (DateTime Stamp,string Title) TitleEntry(string path)
    {
        string info=Path.Combine(Path.GetDirectoryName(path)!,"info.sii");
        return (File.Exists(info)?File.GetLastWriteTimeUtc(info):DateTime.MinValue,Installation.SaveTitle(path));
    }
    static string ProfileName(string path)
    {
        var dir=new DirectoryInfo(Path.GetDirectoryName(path)!);if(dir.Parent?.Name=="save")dir=dir.Parent.Parent!;
        try{return Encoding.UTF8.GetString(Convert.FromHexString(dir.Name));}catch(FormatException){return dir.Name;}
    }
    bool FlushForm(){if(!formDirty)return true;return applyForm?.Invoke()??true;}
    bool CanLeaveFile()
    {
        if(!FlushForm())return false;
        return session?.Dirty!=true||Confirm(L.T("dialog.unsaved"),L.T("dialog.discard.open"),L.T("dialog.discard"));
    }
    async Task OpenFile(string path)
    {
        if(loading||writing){Notify(L.T("status.busy"));return;}
        if(SteamCloud.IsCloud(path)){Ui.Ask(this,L.T("cloudfile.title"),L.T("cloudfile.body"),L.T("ok"));return;}
        if(!CanLeaveFile())return;
        loading=true;IsEnabled=false;ClearMessage();UpdateState();
        try{var opened=await Task.Run(()=>SaveSession.Open(path));session=opened;openError=null;formDirty=false;applyForm=null;loading=false;Navigate(currentPage);}
        catch(Exception e){loading=false;if(session==null){openError=e.Message;Navigate(currentPage);}else Error(e);}
        finally{loading=false;IsEnabled=true;UpdateState();}
    }
    async void Browse()
    {
        if(guideStep>0)return;
        var dialog=new OpenFileDialog{Title=L.T("dialog.open.title"),Filter=L.T("dialog.open.filter")+" (*.sii)|*.sii|"+L.T("dialog.open.all")+" (*.*)|*.*",FileName="game.sii"};
        if(dialog.ShowDialog(this)==true)await OpenFile(dialog.FileName);
    }
    void Navigate(string page)
    {
        if(!FlushForm()){SyncTabs();return;}
        currentPage=page;formDirty=false;applyForm=null;adapt=null;SyncTabs();notice.Content=NoticeLine();
        try
        {
            content.Content=session==null?Blocked():!Editable?State(L.T("noedit.title"),L.T("noedit.body")):page switch{"driver"=>DriverPage(),"garages"=>GaragesPage(),"trucks"=>TrucksPage(),_=>throw new InvalidOperationException("Unknown page: "+page)};
        }
        catch(Exception e)
        {
            formDirty=false;applyForm=null;adapt=null;content.Content=State(L.T("error.title"),e.Message);Notify(e.Message,Ui.Danger);
        }
        finally{SyncRecheck();UpdateState();}
    }
    void SyncTabs(){foreach(var tab in tabs){tab.IsChecked=(string)tab.Tag==currentPage&&Editable;tab.IsEnabled=Editable;}}
    /// <summary>The most severe save notice as one line; Details lists all of them.</summary>
    FrameworkElement? NoticeLine()
    {
        if(session==null)return null;
        var notices=new List<(Brush Dot,string Text,string Detail)>();
        if(session.IntegrityWarning is {} warning)notices.Add((Ui.Danger,warning,warning));
        if(session.NeedsFormatConversion)notices.Add((Ui.Accent,L.T("notice.bsii"),L.T("notice.bsii")));
        bool tmp=SaveSession.HasTruckersMpContent(session.Original);if(tmp)notices.Add((Ui.Subtle,L.T("notice.tmp"),L.T("notice.tmp.text")));
        if(notices.Count==0)return null;
        var line=Ui.Id(new DockPanel{Height=20,Margin=new(0,0,0,12)},"notice-line");
        var details=Ui.Id(Ui.Link(L.T("notice.details"),()=>Ui.Ask(this,notices.Count==1&&tmp?L.T("notice.tmp.title"):L.T("notice.title"),string.Join("\n\n",notices.Select(n=>n.Detail)),L.T("ok"))),"notice-details");
        details.Margin=new(12,0,0,0);DockPanel.SetDock(details,Dock.Right);line.Children.Add(details);
        var dot=Ui.Dot(notices[0].Dot,6);dot.Margin=new(0,0,10,0);DockPanel.SetDock(dot,Dock.Left);line.Children.Add(dot);
        var text=Ui.Trim(Ui.Text(notices[0].Text,12.5,notices[0].Dot==Ui.Danger?Ui.Danger:Ui.Ink));text.VerticalAlignment=VerticalAlignment.Center;line.Children.Add(text);
        return line;
    }
    void Reset()
    {
        if(session==null)return;
        if((session.Dirty||formDirty)&&!Confirm(L.T("reset.title"),L.T("reset.ask"),L.T("action.reset")))return;
        ClearMessage();formDirty=false;applyForm=null;session.Reset();Navigate(currentPage);
    }
    void OnClosing(object? sender,CancelEventArgs e)
    {
        if(writing||loading&&!IsEnabled){e.Cancel=true;Notify(L.T("status.wait"));return;}
        if(closing)return;
        if((session?.Dirty==true||formDirty)&&!Confirm(L.T("dialog.unsaved"),L.T("dialog.discard.close"),L.T("dialog.discard"))){e.Cancel=true;return;}
        if(guideStep>0)CloseGuide();
        closing=true;cancel.Cancel();timer.Stop();recheckTimer.Stop();ClosePreviews();
    }
    internal FrameworkElement PrepareForCapture(SaveSession? save,Installation installation,List<CityInfo> cities,string page,double width,int guide=0)
    {
        timer.Stop();if(!ReferenceEquals(install,installation))xpCurve=XpCurve.Load(installation.GamePath);
        capture=true;session=save;install=installation;if(!ReferenceEquals(cityDefinitions,cities)){cityDefinitions=cities;cityWarnings=[];IndexCities();}formDirty=false;applyForm=null;gameRunning=false;scanned=true;message=null;openError=null;guideStep=0;
        wide=width>=1200;BuildShell();currentPage=page;Navigate(page);if(guide>0)ShowGuide(guide,false);recheckTimer.Stop();UpdateState();
        return (FrameworkElement)Content;
    }
    internal void UseMapForCapture(MapData? map){mapData=map;garageMap=null;garageMapView=map!=null;}
    internal FrameworkElement CaptureCloudOnly(Installation installation,int count,double width){var root=PrepareForCapture(null,installation,cityDefinitions,"driver",width);content.Content=CloudOnly(count);return root;}
    internal bool ApplyForVerification()=>FlushForm();
}
