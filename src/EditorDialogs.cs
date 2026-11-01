using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace Werkstatt;
public sealed partial class MainWindow
{
    Window Dialog(string title,double width,double height,double minWidth,double minHeight)
    {
        var window=new Window{Owner=this,Language=Language,Title=title,Width=width,Height=height,MinWidth=minWidth,MinHeight=minHeight,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Ui.Surface,Foreground=Ui.Ink,FontFamily=Ui.Font,FontSize=13,ShowInTaskbar=false};
        window.SourceInitialized+=(s,e)=>Ui.DarkTitleBar(window);window.Closing+=(s,e)=>{if(writing)e.Cancel=true;};return window;
    }
    /// <summary>Title and subtitle on top, a 64/56 px footer with a hairline, the list in between.</summary>
    static DockPanel DialogFrame(FrameworkElement head,FrameworkElement footer,double footerHeight)
    {
        var panel=new DockPanel{Margin=new(24,24,24,0)};head.Margin=new(0,0,0,16);DockPanel.SetDock(head,Dock.Top);panel.Children.Add(head);
        var foot=new Border{BorderBrush=Ui.Line,BorderThickness=new(0,1,0,0),Height=footerHeight,Child=footer};DockPanel.SetDock(foot,Dock.Bottom);panel.Children.Add(foot);
        return panel;
    }
    void ShowChanges()
    {
        if(session==null||guideStep>0||writing||!FlushForm())return;
        if(PendingCount==0){Notify(L.T("status.nochanges"));return;}
        var dialog=Dialog(L.T("review.title"),720,560,600,420);dialog.Content=ReviewPanel(dialog);dialog.ShowDialog();
    }
    internal FrameworkElement ReviewPanel(Window dialog)
    {
        var active=session!;var rows=ReviewRows().Select(r=>r.Row).ToList();
        var head=new StackPanel();head.Children.Add(Ui.Title(L.P("review.count",rows.Count)));
        var file=Ui.Trim(Ui.Caption(ProfileName(active.FilePath)+" · "+SaveTitle(active.FilePath)));file.ToolTip=active.FilePath;file.Margin=new(0,2,0,0);head.Children.Add(file);
        var footer=new DockPanel();
        var copy=Ui.Id(Ui.Button(L.T("review.copy"),()=>ExportCopy(dialog)),"review-copy");copy.Margin=new(0,0,8,0);
        var save=Ui.Id(Ui.Button(L.T("review.save"),()=>_=FileAction(active.Save,_=>{Navigate(currentPage);Notify(L.F("commit.saved",DateTime.Now.ToString("t",L.Culture)),Ui.Ink,true);},dialog),true),"review-save");
        var buttons=Ui.Row(copy,save);buttons.VerticalAlignment=VerticalAlignment.Center;DockPanel.SetDock(buttons,Dock.Right);footer.Children.Add(buttons);
        var dot=Ui.Dot(Ui.Ok);dot.Margin=new(0,0,10,0);var state=Ui.Text("",12.5);state.LineHeight=18;state.MaxHeight=36;state.VerticalAlignment=VerticalAlignment.Center;state.Margin=new(0,0,16,0);
        var status=new DockPanel{VerticalAlignment=VerticalAlignment.Center};DockPanel.SetDock(dot,Dock.Left);status.Children.Add(dot);status.Children.Add(state);footer.Children.Add(status);
        var panel=DialogFrame(head,footer,64);
        var table=Ui.Tabular(Ui.Table("ScrollRegionReview",nameof(ChangeRow.Tip),(L.T("col.change"),nameof(ChangeRow.Label),-1.4,false),(L.T("col.before"),nameof(ChangeRow.Before),-1,false),(L.T("col.after"),nameof(ChangeRow.After),-1,false)));
        table.Columns[0].MinWidth=220;table.Columns[1].MinWidth=table.Columns[2].MinWidth=120;
        var view=new ListCollectionView(rows);view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ChangeRow.Area)));
        table.IsSynchronizedWithCurrentItem=false;table.ItemsSource=view;table.GroupStyle.Add(new GroupStyle{HeaderTemplate=(DataTemplate)FindResource("ReviewGroup")});VirtualizingPanel.SetIsVirtualizingWhenGrouping(table,true);panel.Children.Add(table);
        void Refresh(bool game)
        {
            bool healthy=active.IntegrityWarning==null,blocked=!healthy||game;
            copy.IsEnabled=healthy&&!writing;save.IsEnabled=!blocked&&!writing;
            state.Text=active.IntegrityWarning??(game?L.T("review.running"):L.T("review.ready"));state.ToolTip=state.Text;state.Foreground=blocked?Ui.Danger:Ui.Ink;dot.Fill=blocked?Ui.Danger:Ui.Ok;
        }
        Refresh(SaveSession.GameRunning);
        var watch=new DispatcherTimer{Interval=TimeSpan.FromSeconds(1.5)};bool probing=false;
        watch.Tick+=async(s,e)=>{if(probing||writing)return;probing=true;try{Refresh(await Task.Run(()=>SaveSession.GameRunning));}catch(Exception){}finally{probing=false;}};
        dialog.Loaded+=(s,e)=>watch.Start();dialog.Closed+=(s,e)=>watch.Stop();
        return panel;
    }
    void ShowBackups()
    {
        if(guideStep>0||writing)return;
        var dialog=Dialog(L.T("backup.title"),760,520,600,400);dialog.Content=BackupsPanel(dialog);dialog.ShowDialog();
    }
    sealed record BackupRow(BackupInfo Info,string Created,string Save,string Contains,bool Keys,bool Cloud){public string Source=>Info.Source;public string Tip=>Save+"\n"+Source;}
    internal FrameworkElement BackupsPanel(Window dialog)
    {
        var only=Ui.Id(new CheckBox{Content=L.T("backup.current"),IsChecked=session!=null,IsEnabled=session!=null,VerticalAlignment=VerticalAlignment.Center,Margin=new(16,0,0,0)},"backup-current");
        var title=new DockPanel();DockPanel.SetDock(only,Dock.Right);title.Children.Add(only);title.Children.Add(Ui.Title(L.T("backup.title")));
        var sub=Ui.Caption(L.T("backup.sub"));sub.Margin=new(0,2,0,0);var head=new StackPanel{Children={title,sub}};
        var footer=new DockPanel();
        var restore=Ui.Id(new Button{Content=L.T("backup.restore"),Style=(Style)FindResource("PrimaryButton")},"backup-restore");ToolTipService.SetShowOnDisabled(restore,true);
        var keys=Ui.Id(new Button{Content=L.T("controls.restore")},"controls-restore");keys.Margin=new(0,0,8,0);keys.ToolTip=L.T("controls.tip");
        var buttons=Ui.Row(keys,restore);buttons.VerticalAlignment=VerticalAlignment.Center;DockPanel.SetDock(buttons,Dock.Right);footer.Children.Add(buttons);
        var folder=Ui.Ghost(L.T("backup.folder"),()=>{Directory.CreateDirectory(SaveSession.BackupRoot);System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe",SaveSession.BackupRoot){UseShellExecute=true});});
        folder.Margin=new(-10,0,16,0);folder.HorizontalAlignment=HorizontalAlignment.Left;folder.VerticalAlignment=VerticalAlignment.Center;footer.Children.Add(folder);
        var panel=DialogFrame(head,footer,56);
        var table=Ui.Tabular(Ui.Table("ScrollRegionBackups",nameof(BackupRow.Tip),(L.T("col.created"),nameof(BackupRow.Created),180,false),(L.T("col.save"),nameof(BackupRow.Save),-1,false),(L.T("col.contains"),nameof(BackupRow.Contains),0,false)));
                var empty=new ContentControl{VerticalAlignment=VerticalAlignment.Top};var list=new Grid{Children={table,empty}};panel.Children.Add(list);
        List<BackupRow> rows=[];
        void Load()=>rows=SaveSession.Backups().Where(b=>b.FileName.Equals("game.sii",StringComparison.OrdinalIgnoreCase)).Select(b=>
        {
            bool bindings=SettingsBackup.Controls(b.Folder).Count>0;
            return new BackupRow(b,b.LocalCreated.ToString("g",L.Culture),ProfileName(b.Source)+" · "+SaveTitle(b.Source),L.T(bindings?"backup.contains.keys":"backup.contains.save"),bindings,SteamCloud.IsCloud(b.Source));
        }).ToList();
        void Fill()
        {
            var visible=rows.Where(r=>only.IsChecked!=true||Same(r.Source,session?.FilePath??"")).ToList();table.ItemsSource=visible;
            table.Visibility=visible.Count>0?Visibility.Visible:Visibility.Hidden;
            empty.Content=visible.Count>0?null:rows.Count==0?State(L.T("backup.empty"),L.T("backup.empty.text")):State(L.T("backup.none.save"),L.T("backup.none.save.text"));
            table.SelectedItem=visible.FirstOrDefault();Sync();
        }
        void Sync()
        {
            var row=table.SelectedItem as BackupRow;bool pending=session?.Dirty==true||formDirty;
            restore.IsEnabled=row!=null&&!pending&&!row.Cloud&&!writing;restore.ToolTip=row?.Cloud==true?L.T("backup.cloud"):pending?L.T("backup.pending"):null;
            keys.Visibility=row?.Keys==true?Visibility.Visible:Visibility.Collapsed;keys.IsEnabled=!writing;
        }
        void Restore()
        {
            if(table.SelectedItem is not BackupRow row||!Confirm(L.T("backup.restore.title"),L.F("backup.restore.ask",row.Created,row.Source),L.T("backup.restore.ok")))return;
            _=FileAction(()=>{SaveSession.Restore(row.Info);return row.Source;},source=>
            {
                if(Same(session?.FilePath??"",source))session=SaveSession.Open(source);
                Load();Fill();Navigate(currentPage);Notify(L.T("commit.restored"),Ui.Ink,true);
            },dialog,false);
        }
        void RestoreKeys()
        {
            if(table.SelectedItem is not BackupRow row)return;var files=SettingsBackup.Controls(row.Info.Folder);
            if(!Confirm(L.T("controls.restore"),L.F("controls.ask",row.Created,string.Join("\n",files.Select(f=>f.Source))),L.T("backup.restore.ok")))return;
            _=FileAction(()=>SettingsBackup.RestoreControls(row.Info.Folder),_=>{Load();Fill();Notify(L.T("commit.controls"),Ui.Ink,true);},dialog,false);
        }
        restore.Click+=(s,e)=>Restore();keys.Click+=(s,e)=>RestoreKeys();table.SelectionChanged+=(s,e)=>Sync();only.Checked+=(s,e)=>Fill();only.Unchecked+=(s,e)=>Fill();Load();Fill();
        return panel;
    }
}
