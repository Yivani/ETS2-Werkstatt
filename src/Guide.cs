using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Werkstatt;
public sealed partial class MainWindow
{
    const int GuideVersion=1;
    int guideStep;bool rechecking;TextBlock? guideLine;Ellipse? guideDot;Border? guidePanel;
    readonly DispatcherTimer recheckTimer=new(){Interval=TimeSpan.FromSeconds(5)};
    /// <summary>Onboarding overlay over the whole window; step 1 also tracks live whether the profiles left Steam Cloud.
    /// The panel top is pinned and its body is as tall as the tallest step, so Back and Next never move.</summary>
    void ShowGuide(int step,bool animate=true)
    {
        bool opening=guideStep==0;guideStep=step;guideLayer.Children.Clear();guideLine=null;guideDot=null;
        guideLayer.Children.Add(new Border{Background=Ui.Scrim});
        var body=GuideBody(step,true);
        foreach(var other in new[]{1,2,3}.Where(s=>s!=step)){var probe=GuideBody(other,false);TextElement.SetFontFamily(probe,Ui.Font);probe.Language=Language;probe.Measure(new Size(502,double.PositiveInfinity));body.MinHeight=Math.Max(body.MinHeight,probe.DesiredSize.Height);}
        var back=Ui.Id(Ui.Button(L.T("guide.back"),()=>ShowGuide(guideStep-1,false)),"guide-back");back.Margin=new(0,0,8,0);back.Visibility=step==1?Visibility.Collapsed:Visibility.Visible;
        var next=Ui.Id(Ui.Button(L.T(step==3?"guide.start":"guide.next"),GuideNext,true),"guide-next");
        var buttons=Ui.Row(back,next);buttons.HorizontalAlignment=HorizontalAlignment.Right;
        var footer=new StackPanel{Children={Ui.Rule(24,20),buttons}};var dock=new DockPanel();DockPanel.SetDock(footer,Dock.Bottom);dock.Children.Add(footer);dock.Children.Add(body);
        guidePanel=new Border{Width=560,Background=Ui.Surface,CornerRadius=new(10),BorderBrush=Ui.Line,BorderThickness=new(1),Padding=new(28,28,28,20),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Top,Margin=new(0,wide?160:96,0,0),Child=dock};
        KeyboardNavigation.SetTabNavigation(guidePanel,KeyboardNavigationMode.Cycle);KeyboardNavigation.SetDirectionalNavigation(guidePanel,KeyboardNavigationMode.Cycle);guideLayer.Children.Add(guidePanel);
        guideLayer.Visibility=Visibility.Visible;
        if(opening&&animate&&SystemParameters.ClientAreaAnimation)guideLayer.BeginAnimation(OpacityProperty,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(150)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}});
        else{guideLayer.BeginAnimation(OpacityProperty,null);guideLayer.Opacity=1;}
        if(IsVisible)Dispatcher.BeginInvoke(()=>next.Focus(),DispatcherPriority.Input);
        SyncRecheck();
    }
    /// <summary>Header, title, text and the step's details; <paramref name="live"/> wires step 1's live line.</summary>
    static TextBlock SteamClientNote(){var note=Ui.Caption(L.T("cloud.steamclient"));note.Margin=new(20,10,0,0);return note;}
    StackPanel GuideBody(int step,bool live)
    {
        var p=new StackPanel();
        var head=new DockPanel();
        if(live){var skip=Ui.Id(Ui.Link(L.T("guide.skip"),CloseGuide,true),"guide-skip");skip.FontSize=12;DockPanel.SetDock(skip,Dock.Right);head.Children.Add(skip);}
        head.Children.Add(Ui.Caption(L.F("guide.step",step,3)));p.Children.Add(head);
        var title=Ui.Title(L.T($"guide.{step}.title"),20);title.Margin=new(0,12,0,12);p.Children.Add(title);
        var text=Ui.Text(L.T($"guide.{step}.body"),13,Ui.Body);text.LineHeight=20;p.Children.Add(text);
        if(step!=2)p.Children.Add(Steps(L.T($"guide.{step}.s1"),L.T($"guide.{step}.s2"),L.T($"guide.{step}.s3")));
        if(step==1)
        {
            p.Children.Add(SteamClientNote());
            var dot=Ui.Dot(null);dot.Margin=new(0,0,10,0);var line=Ui.Text(L.T("guide.live.searching"),12.5);
            var row=new DockPanel{Margin=new(0,16,0,0)};DockPanel.SetDock(dot,Dock.Left);row.Children.Add(dot);row.Children.Add(line);p.Children.Add(row);
            if(live){guideDot=dot;guideLine=line;RefreshGuideLine();}
        }
        if(step==2)
        {
            FrameworkElement Sample(Brush? dot,string state,Brush color,string meaning)
            {var d=Ui.Dot(dot);d.Margin=new(0,0,8,0);var line=Ui.Row(d,Ui.Text(state,12.5,color),Ui.Text(" "+meaning,12.5,Ui.Muted));line.Margin=new(0,10,0,0);return line;}
            p.Children.Add(new StackPanel{Margin=new(0,6,0,0),Children={Sample(Ui.Ok,install.GamePath==null?"ETS2":GameLine(),Ui.Muted,L.T("guide.2.closed")),Sample(Ui.Danger,L.T("status.running"),Ui.Ink,L.T("guide.2.running"))}});
            var tip=Ui.Text(L.T("guide.2.tip"),12.5,Ui.Muted);tip.LineHeight=18;tip.Margin=new(0,16,0,0);p.Children.Add(tip);
        }
        return p;
    }
    void GuideNext(){if(guideStep>=3)CloseGuide();else ShowGuide(guideStep+1,false);}
    void CloseGuide()
    {
        guideStep=0;guideLayer.Visibility=Visibility.Collapsed;guideLayer.Children.Clear();guideLine=null;guideDot=null;guidePanel=null;
        Settings.Store("guide",GuideVersion);SyncRecheck();
    }
    void RefreshGuideLine()
    {
        if(guideLine==null||guideDot==null)return;
        Brush? dot=null;string text=L.T(scanned?"guide.live.none":"guide.live.searching");
        if(scanned&&install.CloudSaves>0){dot=Ui.Accent;text=L.P("guide.live.cloud",install.CloudSaves);}
        else if(scanned&&install.LocalProfiles>0){dot=Ui.Ok;text=L.P("guide.live.local",install.LocalProfiles);}
        guideDot.Fill=dot;guideDot.Stroke=dot==null?Ui.Subtle:null;guideLine.Text=text;
    }
    static Grid Steps(params string[] lines)
    {
        var grid=Ui.Grid("20,*","Auto");grid.RowDefinitions.Clear();grid.Margin=new(0,16,0,0);
        for(int i=0;i<lines.Length;i++)
        {
            grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
            var number=Ui.Text((i+1).ToString(L.Culture),13,Ui.Accent,FontWeights.SemiBold);number.LineHeight=20;var text=Ui.Text(lines[i],13,Ui.Body);text.LineHeight=20;
            number.Margin=text.Margin=new(0,i==0?0:8,0,0);Ui.At(grid,number,0,i);Ui.At(grid,text,1,i);
        }
        return grid;
    }
    /// <summary>Left-aligned empty or blocked state: title, body and optional actions.</summary>
    static StackPanel State(string title,string body,params Button[] actions)
    {
        var p=new StackPanel{MaxWidth=440,HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top};
        var t=Ui.Text(title,16,null,FontWeights.SemiBold);t.LineHeight=22;p.Children.Add(t);
        var b=Ui.Text(body,13,Ui.Muted);b.LineHeight=20;b.Margin=new(0,8,0,0);p.Children.Add(b);
        if(actions.Length>0){var row=Ui.Row(actions);row.Margin=new(0,16,0,0);foreach(var a in actions.SkipLast(1))a.Margin=new(0,0,8,0);p.Children.Add(row);}
        return p;
    }
    FrameworkElement Blocked()
    {
        if(!scanned||loading)return new Grid();
        if(openError!=null)return State(L.T("broken.title"),openError+" "+L.T("broken.hint"),Ui.Button(L.T("broken.pick"),()=>switcher.IsChecked=true),Ui.Button(L.T("backup.title"),ShowBackups));
        if(LocalSaves(install).Any())return new Grid();
        if(install.CloudSaves>0)return CloudOnly(install.CloudSaves);
        return State(L.T("nosaves.title"),L.T("nosaves.body"),Ui.Button(L.T("nosaves.check"),async()=>await Recheck()),Ui.Button(L.T("action.open"),Browse));
    }
    /// <summary>Shown instead of all three screens while the only saves are in Steam Cloud; it checks again on its own.</summary>
    internal FrameworkElement CloudOnly(int count)
    {
        var p=State(L.T("cloud.title"),L.P("cloud.body",count));
        p.Children.Add(Steps(L.T("guide.1.s1"),L.T("guide.1.s2"),L.T("guide.1.s3")));p.Children.Add(SteamClientNote());
        var auto=Ui.Caption(L.T("cloud.auto"));auto.Margin=new(0,16,0,0);p.Children.Add(auto);
        var check=Ui.Button(L.T("cloud.check"),async()=>await Recheck());check.Margin=new(0,0,8,0);var actions=Ui.Row(check,Ui.Button(L.T("action.open"),Browse));actions.Margin=new(0,16,0,0);p.Children.Add(actions);
        return p;
    }
    bool CloudOnlyVisible=>session==null&&scanned&&openError==null&&!LocalSaves(install).Any()&&install.CloudSaves>0;
    void SyncRecheck()
    {
        bool need=guideStep==1||CloudOnlyVisible;
        if(need&&!recheckTimer.IsEnabled)recheckTimer.Start();else if(!need)recheckTimer.Stop();
    }
    /// <summary>Scans again off the UI thread; once a profile is on this PC, its newest save opens.</summary>
    async Task Recheck()
    {
        if(rechecking||loading||writing||closing)return;rechecking=true;
        try
        {
            var found=await Task.Run(()=>Installation.Detect(install.GamePath));bool changed=found.CloudSaves!=install.CloudSaves||LocalSaves(found).Count()!=LocalSaves(install).Count();
            install=found;scanned=true;RefreshGuideLine();UpdateState();
            if(session==null&&openError==null&&LocalSaves(install).FirstOrDefault() is {} newest)
            {
                await OpenFile(newest.Path);
                if(session!=null)Notify(L.F("cloud.found",ProfileName(newest.Path)));
            }
            else if(session==null&&changed)Navigate(currentPage);
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException){}
        finally{rechecking=false;SyncRecheck();}
    }
}
