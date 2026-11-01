using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Werkstatt;
public sealed partial class MainWindow
{
    sealed record EditField(string Unit,string Key,TextBox Box,Func<string,string> Validate,Func<bool>? RelatedChanged=null,Action? CommitRelated=null)
    { public string AppliedText {get;set;}=Box.Text; }
    /// <summary>Inputs are validated and staged as one undo step when the page is left, reviewed or undone; <paramref name="extra"/> adds edits that are not text fields.</summary>
    List<EditField> BeginForm(Func<IEnumerable<(string,string,string)>>? extra=null)
    {
        var fields=new List<EditField>();
        applyForm=()=>
        {
            if(!formDirty)return true;
            try
            {
                var values=fields.Where(f=>f.Box.Text!=f.AppliedText||f.RelatedChanged?.Invoke()==true).Select(f=>(f.Unit,f.Key,f.Validate(f.Box.Text))).ToList();
                if(extra!=null)values.AddRange(extra());
                if(values.Count>0)session!.Edit(d=>d.SetMany(values));foreach(var field in fields){field.AppliedText=field.Box.Text;field.CommitRelated?.Invoke();}
                formDirty=false;UpdateState();return true;
            }
            catch(Exception e){Error(e);return false;}
        };
        return fields;
    }
    void Dirty(){formDirty=true;ClearMessage();UpdateState();}
    FrameworkElement DriverPage()
    {
        var economy=session!.Document.Economy!;var levels=new Dictionary<string,int>();var adr=new List<ToggleButton>();
        var fields=BeginForm(()=>
        {
            var current=session.Document.Find(economy.Id);
            var edits=levels.Where(p=>p.Value!=current.Number(p.Key)).Select(p=>(economy.Id,p.Key,p.Value.ToString(CultureInfo.InvariantCulture))).ToList();
            int mask=adr.Select((t,i)=>t.IsChecked==true?1<<i:0).Sum();if(adr.Count>0&&mask!=current.Number("adr"))edits.Add((economy.Id,"adr",mask.ToString(CultureInfo.InvariantCulture)));
            return edits;
        });
        var grid=Ui.Grid("1*,24,1*,24,1.1*");grid.VerticalAlignment=VerticalAlignment.Top;
        grid.ColumnDefinitions[0].MinWidth=292;grid.ColumnDefinitions[2].MinWidth=360;grid.ColumnDefinitions[4].MinWidth=276;
        AccountColumn(Ui.At(grid,new StackPanel()),fields,economy);
        var skillRows=SkillsColumn(Ui.At(grid,new StackPanel(),2),economy,levels,adr);
        var shortcuts=ShortcutsColumn(Ui.At(grid,new StackPanel(),4));
        adapt=w=>{grid.ColumnDefinitions[1].Width=grid.ColumnDefinitions[3].Width=new(w?48:24);foreach(var row in skillRows)row.Height=new(w?44:40);foreach(var row in shortcuts)row.Height=w?34:30;};adapt(wide);
        return new ScrollViewer{Name="Page",Content=grid,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Focusable=false};
    }
    void AccountColumn(StackPanel p,List<EditField> fields,SiiUnit economy)
    {
        var bank=session!.Document.Bank!;
        p.Children.Add(Ui.Section(L.T("money.title")));
        long balance=bank.Number("money_account");
        var money=Ui.Id(Ui.Tabular(Ui.Input(CompactNumber.Editable(balance),L.T("money.balance"))),"money-input");money.Height=36;money.FontSize=16;money.FontWeight=FontWeights.SemiBold;money.MaxLength=64;money.Padding=new(7,0,26,0);
        money.ToolTip=L.F("money.units",CompactNumber.UnitList)+Environment.NewLine+L.F("money.negative",CompactNumber.Short(-50_000));string original=money.Text;
        var symbol=Ui.Text(L.T("money.symbol"),13,Ui.Muted);symbol.HorizontalAlignment=HorizontalAlignment.Right;symbol.VerticalAlignment=VerticalAlignment.Center;symbol.Margin=new(0,0,10,0);symbol.IsHitTestVisible=false;
        var field=new Grid{Children={money,symbol}};
        var caption=Ui.Id(Ui.Caption(""),"money-limit-hint");caption.Margin=new(0,6,0,0);caption.MinHeight=17;p.Children.Add(caption);
        fields.Add(new(bank.Id,"money_account",money,text=>CompactNumber.Parse(text,long.MaxValue,CompactNumber.MinMoney).ToString(CultureInfo.InvariantCulture)));
        var max=Ui.Id(Ui.ToggleChip(L.T("chip.max")),"max-money");max.Height=36;max.FontSize=13;max.Padding=new(14,0,14,0);max.IsChecked=balance==long.MaxValue;
        var row=Ui.Grid("*,8,Auto");row.ColumnDefinitions[0].MaxWidth=360;row.Margin=new(0,8,0,0);Ui.At(row,field);Ui.At(row,max,2);p.Children.Insert(1,row);max.ToolTip=L.F("money.max.tip",CompactNumber.Full(long.MaxValue));AutomationProperties.SetName(max,L.F("money.max.tip",CompactNumber.Full(long.MaxValue)));
        void Describe()
        {
            money.FontSize=max.IsChecked==true?14:16;
            try
            {
                long n=money.Text==original?balance:CompactNumber.Parse(money.Text,long.MaxValue,CompactNumber.MinMoney);string full=CompactNumber.Full(n);bool limit=max.IsChecked==true||n==long.MaxValue;
                caption.Text=limit?L.T("money.limit"):money.Text.Trim()==full?"":L.F("money.exact",full);caption.Foreground=limit?Ui.Accent:Ui.Muted;money.ClearValue(Control.BorderBrushProperty);
            }
            catch(InvalidDataException e){caption.Text=e.Message;caption.Foreground=Ui.Danger;money.BorderBrush=Ui.Danger;}
        }
        var chips=new StackPanel{Orientation=Orientation.Horizontal,Margin=new(0,8,0,0)};
        foreach(long value in new[]{30_000L,100_000L,1_000_000L,1_000_000_000L}){string label=CompactNumber.Short(value);var chip=Ui.Chip(label,()=>{max.IsChecked=false;money.Text=label;});chip.Margin=new(0,0,6,0);chips.Children.Add(chip);}
        p.Children.Add(chips);
        string beforeMax=money.Text;money.IsReadOnly=max.IsChecked==true;
        max.Checked+=(s,e)=>{beforeMax=money.Text;money.Text=CompactNumber.Full(long.MaxValue);money.IsReadOnly=true;Describe();};
        max.Unchecked+=(s,e)=>{money.IsReadOnly=false;money.Text=beforeMax;Describe();if(money.IsLoaded){money.Focus();money.SelectAll();}};
        money.TextChanged+=(s,e)=>{Dirty();Describe();};Describe();
        if(economy.Get("experience_points")!=null)ExperienceGroup(p,fields,economy);
        if(economy.Get("game_time")!=null)TimeGroup(p,fields,economy);
    }
    void ExperienceGroup(StackPanel p,List<EditField> fields,SiiUnit economy)
    {
        var title=Ui.Section(L.T("xp.title"));title.Margin=new(0,28,0,8);p.Children.Add(title);
        int stored=(int)Math.Clamp(economy.Number("experience_points"),0,int.MaxValue);
        var grid=Ui.Grid("*,8,72,8,Auto","Auto,6,Auto,10,Auto");grid.ColumnDefinitions[0].MaxWidth=220;
        Ui.At(grid,Ui.Label(L.T("xp.xp")));Ui.At(grid,Ui.Label(L.T("xp.level")),2);
        var xp=Ui.At(grid,Ui.Id(Ui.Tabular(Ui.Input(CompactNumber.Full(stored),L.T("xp.xp"))),"xp-input"),0,2);
        var level=Ui.At(grid,Ui.Id(Ui.Tabular(Ui.Input(CompactNumber.Full(xpCurve.Level(stored)),L.T("xp.level"))),"level-input"),2,2);
        var max=Ui.At(grid,Ui.Id(Ui.ToggleChip(L.T("chip.max")),"max-level"),4,2);max.Height=32;max.MinWidth=56;max.FontSize=13;max.Padding=new(14,0,14,0);max.IsChecked=economy.Number("experience_points")==int.MaxValue;max.ToolTip=L.F("xp.max.tip",CompactNumber.Full(int.MaxValue));AutomationProperties.SetName(max,L.F("xp.max.tip",CompactNumber.Full(int.MaxValue)));
        p.Children.Add(grid);string appliedLevel=level.Text;
        var bar=new Grid{Height=4};var done=new ColumnDefinition();var rest=new ColumnDefinition();bar.ColumnDefinitions.Add(done);bar.ColumnDefinitions.Add(rest);
        var track=new Border{Background=Ui.Raised,CornerRadius=new(2)};Grid.SetColumnSpan(track,2);bar.Children.Add(track);bar.Children.Add(new Border{Background=Ui.Accent,CornerRadius=new(2)});Ui.At(grid,bar,0,4,5);
        var caption=Ui.Id(Ui.Caption(""),"xp-limit-hint");caption.Margin=new(0,6,0,0);caption.ToolTip=L.T("xp.hint")+"\n"+L.F("xp.curve",xpCurve.Source==XpCurve.DefaultSource?L.T("xp.curve.default"):xpCurve.Source);p.Children.Add(caption);
        fields.Add(new(economy.Id,"experience_points",xp,text=>{CompactNumber.Parse(level.Text,xpCurve.MaxLevel);return CompactNumber.Parse(text,int.MaxValue).ToString(CultureInfo.InvariantCulture);},()=>level.Text!=appliedLevel,()=>appliedLevel=level.Text));
        void Describe(int value)
        {
            int current=xpCurve.Level(value);long next=current<xpCurve.MaxLevel?xpCurve.Xp(current+1):int.MaxValue;long floor=xpCurve.Xp(current);
            double ratio=current==xpCurve.MaxLevel||next<=floor?1:Math.Clamp((value-floor)/(double)(next-floor),0,1);done.Width=new(Math.Max(ratio,.0001),GridUnitType.Star);rest.Width=new(Math.Max(1-ratio,.0001),GridUnitType.Star);
            bool limit=max.IsChecked==true||value==int.MaxValue;
            caption.Text=limit?L.T("xp.limit"):current==xpCurve.MaxLevel?L.F("xp.maxlevel",CompactNumber.Full(current)):L.F("xp.next",CompactNumber.Full(current),CompactNumber.Full(next-value),CompactNumber.Full(current+1));
            caption.Foreground=limit?Ui.Accent:Ui.Muted;xp.ClearValue(Control.BorderBrushProperty);level.ClearValue(Control.BorderBrushProperty);
        }
        bool synchronizing=false;
        void Changed(bool fromLevel)
        {
            if(synchronizing)return;synchronizing=true;
            try
            {
                int value=fromLevel?xpCurve.Xp((int)CompactNumber.Parse(level.Text,xpCurve.MaxLevel)):(int)CompactNumber.Parse(xp.Text,int.MaxValue);
                if(fromLevel)xp.Text=CompactNumber.Full(value);else level.Text=CompactNumber.Full(xpCurve.Level(value));Describe(value);
            }
            catch(InvalidDataException e){caption.Text=e.Message;caption.Foreground=Ui.Danger;(fromLevel?level:xp).BorderBrush=Ui.Danger;}
            finally{synchronizing=false;Dirty();}
        }
        xp.TextChanged+=(s,e)=>Changed(false);level.TextChanged+=(s,e)=>Changed(true);Describe(stored);
        string beforeMax=xp.Text;xp.IsReadOnly=level.IsReadOnly=max.IsChecked==true;
        max.Checked+=(s,e)=>{beforeMax=xp.Text;xp.IsReadOnly=level.IsReadOnly=true;xp.Text=CompactNumber.Full(int.MaxValue);};
        max.Unchecked+=(s,e)=>{xp.IsReadOnly=level.IsReadOnly=false;xp.Text=beforeMax;if(xp.IsLoaded){xp.Focus();xp.SelectAll();}};
    }
    void TimeGroup(StackPanel p,List<EditField> fields,SiiUnit economy)
    {
        var title=Ui.Section(L.T("time.title"));title.Margin=new(0,28,0,8);p.Children.Add(title);
        long stored=economy.Number("game_time");const int maxDays=int.MaxValue/1440;
        var grid=Ui.Grid("72,12,72","Auto,6,Auto");Ui.At(grid,Ui.Label(L.T("time.days")));Ui.At(grid,Ui.Label(L.T("time.clock")),2);
        var days=Ui.At(grid,Ui.Id(Ui.Tabular(Ui.Input(CompactNumber.Full(stored/1440),L.T("time.days"))),"time-days"),0,2);
        var clock=Ui.At(grid,Ui.Id(Ui.Tabular(Ui.Input($"{stored/60%24:00}:{stored%60:00}",L.T("time.clock"))),"time-clock"),2,2);p.Children.Add(grid);
        var caption=Ui.Caption(L.T("time.sub"));caption.Margin=new(0,6,0,0);p.Children.Add(caption);string appliedClock=clock.Text;
        var warning=Ui.Id(Ui.Caption(L.T("time.back")),"time-warning");warning.Margin=new(0,4,0,0);p.Children.Add(warning);long saved=session!.Original.Economy?.Number("game_time")??stored;
        long Minutes()
        {
            long d;try{d=CompactNumber.Parse(days.Text,maxDays);}catch(InvalidDataException){throw new InvalidDataException(L.F("input.range",L.T("time.days"),0,CompactNumber.Full(maxDays)));}
            var m=Regex.Match(clock.Text.Trim(),@"^(\d{1,2})[:.](\d{2})$");
            if(!m.Success||int.Parse(m.Groups[1].Value,CultureInfo.InvariantCulture)>23||int.Parse(m.Groups[2].Value,CultureInfo.InvariantCulture)>59)throw new InvalidDataException(L.T("time.bad"));
            long total=d*1440+int.Parse(m.Groups[1].Value,CultureInfo.InvariantCulture)*60+int.Parse(m.Groups[2].Value,CultureInfo.InvariantCulture);
            return total<=int.MaxValue?total:throw new InvalidDataException(L.F("input.range",L.T("time.days"),0,CompactNumber.Full(maxDays)));
        }
        fields.Add(new(economy.Id,"game_time",days,_=>Minutes().ToString(CultureInfo.InvariantCulture),()=>clock.Text!=appliedClock,()=>appliedClock=clock.Text));
        void Describe()
        {
            try{warning.Foreground=Minutes()<saved?Ui.Accent:Ui.Muted;caption.Text=L.T("time.sub");caption.Foreground=Ui.Muted;days.ClearValue(Control.BorderBrushProperty);clock.ClearValue(Control.BorderBrushProperty);}
            catch(InvalidDataException e){caption.Text=e.Message;caption.Foreground=Ui.Danger;(e.Message==L.T("time.bad")?clock:days).BorderBrush=Ui.Danger;}
        }
        days.TextChanged+=(s,e)=>{Dirty();Describe();};clock.TextChanged+=(s,e)=>{Dirty();Describe();};Describe();
    }
    List<RowDefinition> SkillsColumn(StackPanel p,SiiUnit economy,Dictionary<string,int> levels,List<ToggleButton> adr)
    {
        var setters=new List<Action<int>>();var rows=new List<RowDefinition>();
        var head=new DockPanel();var max=Ui.Id(Ui.Ghost(L.T("skills.max"),()=>{foreach(var set in setters)set(6);foreach(var t in adr)t.IsChecked=true;}),"skills-max");
        max.Height=28;max.Margin=new(12,-4,-10,-4);DockPanel.SetDock(max,Dock.Right);head.Children.Add(max);head.Children.Add(Ui.Section(L.T("skills.title")));p.Children.Add(head);
        var table=Ui.Grid("*,200","Auto");table.RowDefinitions.Clear();table.Margin=new(0,16,0,0);p.Children.Add(table);
        FrameworkElement Name(string text,string? tip){var t=Ui.Trim(Ui.Text(text,13,null,FontWeights.SemiBold));t.ToolTip=tip??text;t.VerticalAlignment=VerticalAlignment.Center;t.Margin=new(0,0,8,0);return t;}
        foreach(var key in SaveSession.SkillKeys.Where(k=>economy.Get(k)!=null))
        {
            var row=new RowDefinition();rows.Add(row);table.RowDefinitions.Add(row);int index=table.RowDefinitions.Count-1;
            levels[key]=(int)Math.Clamp(economy.Number(key),0,6);string label=L.T("skill."+key);Ui.At(table,Name(label,L.T("skill."+key+".hint")),0,index);
            var bar=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};var buttons=new List<Button>();
            void Refresh()
            {
                for(int i=0;i<buttons.Count;i++)
                {
                    bool filled=i>0&&i<=levels[key],selected=i==levels[key];var b=buttons[i];
                    b.Background=i==0?selected?Ui.AccentSoft:Ui.Raised:selected?Ui.Accent:filled?Ui.Passed:Ui.Raised;b.BorderBrush=i==0&&selected?Ui.Accent:Brushes.Transparent;
                    b.Foreground=i==0?selected?Ui.Accent:Ui.Subtle:selected?Ui.AccentText:filled?Ui.Ink:Ui.Subtle;b.FontWeight=selected?FontWeights.SemiBold:FontWeights.Normal;
                    AutomationProperties.SetItemStatus(b,selected?L.T("skills.status.current"):filled?L.T("skills.status.on"):L.T("skills.status.off"));
                }
            }
            void Set(int rank){levels[key]=Math.Clamp(rank,0,6);Refresh();Dirty();}
            setters.Add(Set);
            for(int i=0;i<=6;i++)
            {
                int rank=i;var b=Ui.Id(Ui.Button(i.ToString(L.Culture),()=>Set(rank)),$"skill-{key}-{i}");b.Style=(Style)FindResource("SegmentButton");b.Margin=new(i==0?0:i==1?8:2,0,0,0);
                b.ToolTip=L.F("skills.tip",label,i);AutomationProperties.SetName(b,L.F("skills.tip",label,i));buttons.Add(b);bar.Children.Add(b);
            }
            bar.KeyDown+=(s,e)=>{if(e.Key is Key.Left or Key.Right or Key.Home or Key.End){Set(e.Key switch{Key.Left=>levels[key]-1,Key.Right=>levels[key]+1,Key.Home=>0,_=>6});buttons[levels[key]].Focus();e.Handled=true;}};
            Refresh();Ui.At(table,bar,1,index);
        }
        string[] kinds=["explosive","gas","flame","solid","toxic","corrosive"];int[] numbers=[1,2,3,4,6,8];
        if(economy.Get("adr") is not null)
        {
            var row=new RowDefinition();rows.Add(row);table.RowDefinitions.Add(row);int index=table.RowDefinitions.Count-1;Ui.At(table,Name(L.T("adr.title"),null),0,index);
            var toggles=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center,Margin=new(34,0,0,0)};int mask=(int)economy.Number("adr");
            for(int i=0;i<kinds.Length;i++)
            {
                string name=L.F("adr.class",numbers[i])+" · "+L.T("adr."+kinds[i]);
                var t=Ui.Id(new ToggleButton{Content=numbers[i].ToString(L.Culture),Style=(Style)FindResource("AdrToggle"),IsChecked=(mask&(1<<i))!=0,Margin=new(i==0?0:2,0,0,0)},$"adr-{i}");
                void Describe(){string state=name+" · "+(t.IsChecked==true?L.T("skills.status.on"):L.T("skills.status.off"));t.ToolTip=state;AutomationProperties.SetName(t,state);}
                t.Checked+=(s,e)=>{Describe();Dirty();};t.Unchecked+=(s,e)=>{Describe();Dirty();};Describe();adr.Add(t);toggles.Children.Add(t);
            }
            Ui.At(table,toggles,1,index);
            var legend=new WrapPanel{Margin=new(0,8,0,0)};
            for(int i=0;i<kinds.Length;i++){var item=Ui.Caption(numbers[i].ToString(L.Culture)+" "+L.T("adr."+kinds[i]).Replace(' ',' '));item.TextWrapping=TextWrapping.NoWrap;item.Margin=new(0,0,14,0);legend.Children.Add(item);}
            p.Children.Add(legend);
        }
        return rows;
    }
    enum Shortcut{Open,Done,None}
    sealed record ShortcutItem(string Id,string Title,string Description,Shortcut State,string Status,string? Why,Action Click);
    /// <summary>Every quick action as one checklist row: open (Accent, clickable), done (check, Muted) or not applicable (Subtle; the tooltip
    /// says why). Rows read the staged document, and every action and every undo rebuilds the page. Each click stages one undo step.</summary>
    List<Button> ShortcutsColumn(StackPanel p)
    {
        var s=session!;var doc=s.Document;var economy=doc.Economy;var items=new List<ShortcutItem>();
        void Stage(Func<string> action){if(FlushForm())Run(()=>{string done=action();Navigate("driver");Notify(done);});}
        static int Pending(Func<int> plan){try{return plan();}catch(InvalidDataException){return -1;}}
        static string Ratio(long done,long total)=>CompactNumber.Full(done)+"/"+CompactNumber.Full(total);
        void Add(string id,string title,string description,bool applicable,string why,int pending,string status,Func<string> action,Func<bool>? confirm=null)
        {
            var state=!applicable?Shortcut.None:pending!=0?Shortcut.Open:Shortcut.Done;
            items.Add(new(id,title,description,state,state switch{Shortcut.Done=>L.T("shortcut.done"),Shortcut.None=>"—",_=>status},state==Shortcut.None?why:null,
                ()=>{if(confirm!=null&&(!FlushForm()||!confirm()))return;Stage(action);}));
        }
        string nothing=L.T("shortcut.nothing"),noData=L.T("shortcut.why.data");
        Add("shortcut-money",L.T("shortcut.money"),L.T("shortcut.money.desc"),doc.Bank?.Get("money_account")!=null,noData,s.MoneyPending()?1:0,
            L.F("money.value",CompactNumber.Brief(doc.Bank?.Number("money_account")??0)),()=>s.MaxMoney()?L.T("shortcut.money.done"):nothing);
        bool xp=economy?.Get("experience_points")!=null;
        Add("shortcut-level",L.T("shortcut.level"),L.T("shortcut.level.desc"),xp,noData,s.LevelPending()?1:0,xp?CompactNumber.Full(xpCurve.Level((int)Math.Clamp(economy!.Number("experience_points"),0,int.MaxValue))):"",
            ()=>s.MaxLevel()?L.T("shortcut.level.done"):nothing);
        var skills=SaveSession.SkillKeys.Where(k=>economy?.Get(k)!=null).ToList();bool hasAdr=economy?.Get("adr")!=null;
        long points=skills.Sum(k=>Math.Clamp(economy!.Number(k),0,6))+(hasAdr?System.Numerics.BitOperations.PopCount((ulong)(economy!.Number("adr")&63)):0);
        Add("shortcut-skills",L.T("shortcut.skills"),L.T("shortcut.skills.desc"),skills.Count>0||hasAdr,noData,s.SkillsPending(),Ratio(points,6*skills.Count+(hasAdr?6:0)),
            ()=>s.MaxSkills()>0?L.T("shortcut.skills.done"):nothing);
        var garages=doc.Units.Where(u=>u.Type=="garage").ToList();var owned=garages.Where(g=>g.Number("status")>0).ToList();string large=GarageLevel(3);int buy=s.GaragesPending(true);
        Add("shortcut-garages-buy",L.T("shortcut.garages.buy"),L.T("shortcut.garages.buy.desc"),garages.Count>0,L.T("shortcut.why.garages"),buy,Ratio(garages.Count(g=>g.Number("status")==3),garages.Count),
            ()=>s.UpgradeGarages(true,null,3) is >0 and var n?L.P("garage.bought",n):L.F("garage.already",large),
            ()=>Confirm(L.T("shortcut.garages.buy"),L.F("garage.buyall.ask",L.P("count.garages",buy),large),L.T("garage.buyall.ok")));
        Add("shortcut-garages-upgrade",L.T("shortcut.garages.upgrade"),L.T("shortcut.garages.upgrade.desc"),owned.Count>0,L.T("shortcut.why.owned"),s.GaragesPending(false),
            Ratio(owned.Count(g=>g.Number("status")==3),owned.Count),()=>s.UpgradeGarages(false,null,3) is >0 and var n?L.P("garage.upgraded",n):L.F("garage.already",large));
        var known=s.KnownCities();
        long Known(IEnumerable<string> list){var set=list.ToHashSet();return known.Count(set.Contains);}
        Add("shortcut-cities",L.T("cities.title"),L.T("cities.desc"),known.Count>0&&economy?.Get("visited_cities")!=null,noData,Pending(s.CitiesPending),Ratio(Known(s.VisitedCities()),known.Count),
            ()=>L.P("cities.done",s.VisitCities(s.KnownCities())));
        Add("shortcut-dealers",L.T("shortcut.dealers"),L.T("shortcut.dealers.desc"),known.Count>0&&economy?.Get("unlocked_dealers")!=null,noData,Pending(s.DealersPending),Ratio(Known(s.UnlockedDealers()),known.Count),
            ()=>s.UnlockDealers() is >0 and var n?L.F("unlock.done",L.P("count.dealers",n)):nothing);
        Add("shortcut-agencies",L.T("shortcut.agencies"),L.T("shortcut.agencies.desc"),known.Count>0&&economy?.Get("unlocked_recruitments")!=null,noData,Pending(s.RecruitmentsPending),Ratio(Known(s.UnlockedRecruitments()),known.Count),
            ()=>s.UnlockRecruitments() is >0 and var n?L.F("unlock.done",L.P("count.agencies",n)):nothing);
        var vehicles=SaveSession.OwnedVehicleIds(doc).ToList();var trucks=vehicles.Where(id=>doc.Find(id).Type=="vehicle").ToList();
        int worn=s.ServicePending(vehicles,true,false,true).Count,empty=s.ServicePending(trucks,false,true,false).Count;
        Add("shortcut-repair",L.T("shortcut.repair"),L.T("shortcut.repair.desc"),vehicles.Count>0,L.T("trucks.empty.title"),worn,Ratio(vehicles.Count-worn,vehicles.Count),
            ()=>{int n=s.ServicePending(vehicles,true,false,true).Count;s.Service(vehicles,true,false,true);return n>0?L.P("vehicles.repaired",n):L.T("vehicles.norepair");});
        Add("shortcut-refuel",L.T("shortcut.refuel"),L.T("shortcut.refuel.desc"),trucks.Count>0,L.T("shortcut.why.trucks"),empty,Ratio(trucks.Count-empty,trucks.Count),
            ()=>{int n=s.ServicePending(trucks,false,true,false).Count;s.Service(trucks,false,true,false);return n>0?L.P("vehicles.refuelled",n):L.T("vehicles.norefuel");});
        int hired=s.HiredDrivers().Count,untrained=s.DriverSkillsPending();
        Add("shortcut-drivers",L.T("drivers.title"),L.T("drivers.desc"),hired>0,L.T("drivers.none"),untrained,Ratio(hired-untrained,hired),
            ()=>s.MaxDriverSkills() is >0 and var n?L.P("drivers.done",n):L.T("drivers.nothing"));
        var (found,stops)=s.BusStops();
        Add("shortcut-bus",L.T("busstops.title"),L.T("busstops.desc"),stops>0,L.T("shortcut.why.bus"),s.BusStopsPending(),Ratio(found,stops),()=>L.P("busstops.done",s.DiscoverBusStops()));
        int record=s.CleanRecordPending();
        Add("shortcut-clean",L.T("record.title"),L.T("record.desc"),record>0||s.CleanRecordPending(true)>0,L.T("shortcut.why.record"),record,L.P("count.offences",s.Offences()),
            ()=>s.CleanRecord()>0?L.T("record.done"):nothing);
        var parts=items.Take(8).Where(i=>i.State!=Shortcut.None).ToList();
        Add("shortcut-maxout",L.T("maxout.title"),L.T("maxout.desc"),true,noData,Pending(()=>s.MaxOutPending().Any?1:0),Ratio(parts.Count(i=>i.State==Shortcut.Done),parts.Count),
            ()=>s.MaxOut() is {Any:true} r?L.F("maxout.done",L.P("count.garages",r.Garages),L.P("count.cities",r.Cities)):L.T("status.maxout.none"),
            ()=>Confirm(L.T("maxout.title"),L.T("maxout.ask"),L.T("maxout.ok")));
        items.Insert(0,items[^1]);items.RemoveAt(items.Count-1);
        var head=new DockPanel{Margin=new(0,0,0,8)};
        var progress=Ui.Id(Ui.Caption(L.F("shortcuts.progress",items.Count(i=>i.State==Shortcut.Done),items.Count(i=>i.State!=Shortcut.None))),"shortcuts-progress");
        progress.TextWrapping=TextWrapping.NoWrap;progress.VerticalAlignment=VerticalAlignment.Center;progress.Margin=new(12,0,0,0);DockPanel.SetDock(progress,Dock.Right);
        head.Children.Add(progress);head.Children.Add(Ui.Section(L.T("shortcuts.title")));p.Children.Add(head);
        var rows=items.Select(ShortcutRow).ToList();foreach(var row in rows)p.Children.Add(row);
        return rows;
    }
    /// <summary>One 30 px checklist row: state glyph, title, right-aligned state, and a chevron while the row can act.</summary>
    Button ShortcutRow(ShortcutItem item)
    {
        bool open=item.State==Shortcut.Open;
        FrameworkElement glyph=item.State switch
        {
            Shortcut.Open=>Ui.Dot(null,10),
            Shortcut.Done=>Ui.Icon(Ui.IconCheck,14,Ui.Ok,2),
            _=>new Border{Width=8,Height=1.5,Background=Ui.Subtle,CornerRadius=new(1)}
        };
        if(glyph is System.Windows.Shapes.Ellipse ring)ring.Stroke=Ui.Muted;
        glyph.HorizontalAlignment=HorizontalAlignment.Left;glyph.VerticalAlignment=VerticalAlignment.Center;
        var title=Ui.Text(item.Title,13,open?Ui.Accent:item.State==Shortcut.Done?Ui.Muted:Ui.Subtle,open?FontWeights.SemiBold:FontWeights.Normal);
        title.TextWrapping=TextWrapping.NoWrap;title.TextTrimming=TextTrimming.CharacterEllipsis;title.VerticalAlignment=VerticalAlignment.Center;
        var status=Ui.Tabular(Ui.Text(item.Status,12,item.State==Shortcut.None?Ui.Subtle:Ui.Muted));status.TextWrapping=TextWrapping.NoWrap;status.VerticalAlignment=VerticalAlignment.Center;status.Margin=new(10,0,0,0);
        var line=Ui.Grid("14,8,*,Auto");Ui.At(line,glyph);Ui.At(line,title,2);Ui.At(line,status,3);
        var b=Ui.Id(Ui.Button("",item.Click),item.Id);b.Style=(Style)FindResource("ShortcutRow");b.Content=line;b.Margin=new(-10,0,-10,0);b.IsEnabled=open;
        b.ToolTip=item.Why==null?item.Description:item.Why+"\n"+item.Description;ToolTipService.SetShowOnDisabled(b,true);
        AutomationProperties.SetName(b,item.Title);AutomationProperties.SetHelpText(b,item.Description);AutomationProperties.SetItemStatus(b,item.Why??item.Status);
        return b;
    }
}
