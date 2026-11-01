using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Werkstatt;
/// <summary>One row of the garage table. Status is the save's level (0, 6, 2, 3); Type is its display text.</summary>
public sealed record GarageRow(string Id,string City,string Country,string Hq,string Type,int Status,int Trucks,int Drivers)
{
    public string Tip=>City+Hq+(Country.Length>0?" · "+Country:"");
    public string Counts=>Status>0?Trucks.ToString(L.Culture)+" · "+Drivers.ToString(L.Culture):"—";
    public override string ToString()=>City;
}
public sealed partial class MainWindow
{
    string? selectedGarage;string garageSearch="",garageCountry="",mapProgress="";int garageSize=3;bool garageOwned,garageMapView,mapLoading,mapFailed;
    MapView? garageMap;TextBlock? mapStatus;
    internal sealed record GarageSize(int Status){public override string ToString()=>GarageLevel(Status);}
    internal static readonly GarageSize[] GarageSizes=[new(6),new(2),new(3)];
    string CityName(string id)=>cityIndex.TryGetValue(id,out var city)?city.Name:L.Culture.TextInfo.ToTitleCase(id.Replace('_',' '));
    void IndexCities()=>cityIndex=cityDefinitions.GroupBy(c=>c.Id).ToDictionary(g=>g.Key,g=>g.First());
    async Task LoadCities()
    {
        var warnings=new List<string>();
        try{cityDefinitions=await Task.Run(()=>GameMap.Cities(install,warnings));}catch(Exception e) when(e is IOException or InvalidDataException or UnauthorizedAccessException){cityDefinitions=[];}
        cityWarnings=warnings;IndexCities();
    }
    static string GarageLevel(long status)=>status switch{0=>L.T("garage.none"),6=>L.T("garage.tiny"),2=>L.T("garage.small"),3=>L.T("garage.large"),_=>L.F("garage.unknown",status)};
    static bool Occupied(string? value)=>value is not null and not "null";
    List<GarageRow> GarageRows()
    {
        string hq=session!.Document.Player?.Get("hq_city")??"";
        return session.Document.Units.Where(u=>u.Type=="garage").Select(u=>
        {
            string city=u.Id.Replace("garage.","");cityIndex.TryGetValue(city,out var c);int level=(int)u.Number("status");
            int slots=(int)u.Number("vehicles"),trucks=Enumerable.Range(0,slots).Count(i=>Occupied(u.Get($"vehicles[{i}]"))),drivers=Enumerable.Range(0,slots).Count(i=>Occupied(u.Get($"drivers[{i}]")));
            string type=level==0?L.T("garage.none"):level is 6 or 2 or 3?L.T("garage.short."+level):L.F("garage.unknown",level);
            return new GarageRow(u.Id,CityName(city),Countries.Name((c?.Country??"").ToLowerInvariant()),city==hq?" · "+L.T("garage.hq.short"):"",type,level,trucks,drivers);
        }).OrderBy(g=>g.City,StringComparer.Create(L.Culture,true)).ToList();
    }
    /// <summary>A read-only text column. <paramref name="notOwned"/> recolours the cell for garages not owned; owned rows keep
    /// <paramref name="owned"/> or, without it, the row colour (Accent when selected).</summary>
    static DataGridTextColumn GarageColumn(string title,string field,DataGridLength width,bool numeric,bool tip,Brush? owned=null,Brush? notOwned=null)
    {
        var element=new Style(typeof(TextBlock));
        element.Setters.Add(new Setter(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis));element.Setters.Add(new Setter(TextBlock.TextWrappingProperty,TextWrapping.NoWrap));
        if(tip)element.Setters.Add(new Setter(FrameworkElement.ToolTipProperty,new Binding(field)));
        if(owned!=null)element.Setters.Add(new Setter(TextBlock.ForegroundProperty,owned));
        if(notOwned!=null){var free=new DataTrigger{Binding=new Binding(nameof(GarageRow.Status)),Value=0};free.Setters.Add(new Setter(TextBlock.ForegroundProperty,notOwned));element.Triggers.Add(free);}
        var column=new DataGridTextColumn{Header=title,Binding=new Binding(field),Width=width,IsReadOnly=true,ElementStyle=element};
        if(numeric)
        {
            element.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Right));element.Setters.Add(new Setter(Typography.NumeralAlignmentProperty,FontNumeralAlignment.Tabular));
            var header=new Style(typeof(DataGridColumnHeader),(Style)Application.Current.FindResource(typeof(DataGridColumnHeader)));header.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,HorizontalAlignment.Right));column.HeaderStyle=header;
        }
        return column;
    }
    FrameworkElement GaragesPage()
    {
        var rows=GarageRows();var shown=rows;string all=L.T("garage.allcountries");
        var grid=Ui.Grid("*,20,256","Auto,16,*,Auto");
        var filters=Ui.At(grid,Ui.Grid("*,8,200,8,Auto,0,12,Auto"));
        var search=Ui.At(filters,Ui.Id(Ui.Input(garageSearch,L.T("garage.search"),L.T("garage.search")),"garage-search"));
        var countries=new[]{all}.Concat(rows.Select(r=>r.Country).Where(c=>c.Length>0).Distinct().Order(StringComparer.Create(L.Culture,true))).ToList();if(!countries.Contains(garageCountry))garageCountry=all;
        var country=Ui.At(filters,Ui.Id(new ComboBox{ItemsSource=countries,SelectedItem=garageCountry,IsEnabled=countries.Count>1,ToolTip=countries.Count>1?null:L.T("garage.country.none")},"garage-country"),2);
        AutomationProperties.SetName(country,L.T("garage.country"));ToolTipService.SetShowOnDisabled(country,true);
        var (owned,ownedOptions)=Ui.Segmented([L.T("garage.filter.all"),L.T("garage.filter.owned")],garageOwned?1:0);
        Ui.Id(owned,"garage-owned");Ui.Id(ownedOptions[0],"garage-owned-0");Ui.Id(ownedOptions[1],"garage-owned-1");owned.MinWidth=120;Ui.At(filters,owned,4);
        var (view,viewOptions)=Ui.Segmented([L.T("garage.view.list"),L.T("garage.view.map")],garageMapView?1:0);
        Ui.Id(view,"garage-view");Ui.Id(viewOptions[0],"garage-view-list");Ui.Id(viewOptions[1],"garage-view-map");view.MinWidth=120;Ui.At(filters,view,7);
        var table=new DataGrid{Name="ScrollRegionGarages",CanUserSortColumns=false,CanUserResizeColumns=false,IsReadOnly=true};AutomationProperties.SetName(table,L.T("tab.garages"));
        var cityColumn=new DataGridTemplateColumn{Header=L.T("col.city"),CellTemplate=(DataTemplate)FindResource("GarageCity"),Width=new DataGridLength(1,DataGridLengthUnitType.Star),IsReadOnly=true};
        var typeColumn=GarageColumn(L.T("col.kind"),nameof(GarageRow.Type),120,false,true,Ui.Accent,Ui.Muted);
        var countColumn=GarageColumn(L.T("col.trucksdrivers"),nameof(GarageRow.Counts),170,true,false,null,Ui.Subtle);
        foreach(var column in new DataGridColumn[]{cityColumn,GarageColumn(L.T("garage.country"),nameof(GarageRow.Country),new DataGridLength(1,DataGridLengthUnitType.Star),false,true),typeColumn,countColumn})table.Columns.Add(column);
        var emptyText=Ui.Text("",13,Ui.Muted);var clear=Ui.Link(L.T("garage.clear"),()=>{search.Text="";country.SelectedIndex=0;ownedOptions[0].IsChecked=true;});clear.Margin=new(0,8,0,0);
        var empty=new StackPanel{Visibility=Visibility.Collapsed,Margin=new(12,42,12,0),VerticalAlignment=VerticalAlignment.Top,Children={emptyText,clear}};
        var tableArea=Ui.At(grid,new Grid(),0,2);tableArea.Children.Add(table);tableArea.Children.Add(empty);
        var mapArea=Ui.At(grid,new Grid{Background=Ui.MapGround},0,2);mapArea.SizeChanged+=(s,e)=>mapArea.Clip=new RectangleGeometry(new Rect(e.NewSize),8,8);
        var map=MapArea(mapArea,rows);
        void ShowView(){tableArea.Visibility=garageMapView?Visibility.Collapsed:Visibility.Visible;mapArea.Visibility=garageMapView?Visibility.Visible:Visibility.Collapsed;if(garageMapView&&mapData==null&&install.GamePath!=null&&!mapLoading&&!mapFailed)_=LoadMap();}
        for(int i=0;i<2;i++){bool onMap=i==1;viewOptions[i].Checked+=(s,e)=>{garageMapView=onMap;ShowView();};}
        ShowView();
        var scope=Ui.Trim(Ui.Label(""));scope.VerticalAlignment=VerticalAlignment.Center;
        var size=Ui.Id(new ComboBox{ItemsSource=GarageSizes,SelectedItem=GarageSizes.FirstOrDefault(s=>s.Status==garageSize)??GarageSizes[^1],ToolTip=L.T("garage.size.tip")},"garage-size");AutomationProperties.SetName(size,L.T("garage.size"));
        size.SelectionChanged+=(s,e)=>garageSize=(size.SelectedItem as GarageSize)?.Status??3;
        var upgrade=Ui.Id(Ui.Button(L.T("garage.upgradeall"),()=>Bulk(false)),"garages-upgrade");var buyAll=Ui.Id(Ui.Button(L.T("garage.buyall"),()=>Bulk(true)),"garages-buy-all");buyAll.ToolTip=L.T("garage.buyall.tip");
        var bulkLine=Ui.Grid("*,12,200,8,Auto,8,Auto");Ui.At(bulkLine,scope);Ui.At(bulkLine,size,2);Ui.At(bulkLine,upgrade,4);Ui.At(bulkLine,buyAll,6);
        Ui.At(grid,new StackPanel{Children={Ui.Rule(0,12),bulkLine}},0,3);
        void Bulk(bool buy)
        {
            var ids=shown.Select(r=>r.Id).ToHashSet();string level=GarageLevel(garageSize);
            if(buy&&!Confirm(L.T("garage.buyall"),L.F("garage.buyall.ask",L.P("garage.shown",ids.Count),level),L.T("garage.buyall.ok")))return;
            Run(()=>{int n=session!.UpgradeGarages(buy,ids,garageSize);Navigate("garages");Notify(n==0?L.F("garage.already",level):L.P(buy?"garage.bought":"garage.upgraded",n));});
        }
        var inspector=new StackPanel();var side=Ui.At(grid,new ScrollViewer{Name="ScrollRegionInspector",Content=inspector,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Focusable=false},2);Grid.SetRowSpan(side,4);
        void Filter()
        {
            garageSearch=search.Text;garageCountry=country.SelectedItem as string??all;string term=garageSearch.Trim();
            shown=rows.Where(g=>(!garageOwned||g.Status>0)&&(garageCountry==all||g.Country==garageCountry)&&L.Culture.CompareInfo.IndexOf(g.City+" "+g.Country,term,CompareOptions.IgnoreCase|CompareOptions.IgnoreNonSpace)>=0).ToList();
            table.ItemsSource=shown;if(map!=null){map.Available=shown.Select(g=>g.Id.Replace("garage.","")).ToHashSet();map.Refresh();}scope.Text=L.P("garage.scope",shown.Count);scope.ToolTip=scope.Text;upgrade.IsEnabled=buyAll.IsEnabled=shown.Count>0;
            empty.Visibility=shown.Count==0?Visibility.Visible:Visibility.Collapsed;emptyText.Text=term.Length>0?L.F("garage.nomatch",term):L.T("garage.nomatch.filters");
            if(shown.FirstOrDefault(r=>r.Id==selectedGarage) is {} visible){table.SelectedItem=visible;table.ScrollIntoView(visible);}
        }
        search.TextChanged+=(s,e)=>Filter();country.SelectionChanged+=(s,e)=>Filter();for(int i=0;i<2;i++){bool only=i==1;ownedOptions[i].Checked+=(s,e)=>{garageOwned=only;Filter();};}
        void ChangeGarage(string id,int level)
        {
            ClearMessage();Exception? failure=null;try{session!.Garage(id,level);}catch(Exception e){failure=e;}
            Navigate("garages");if(failure!=null)Error(failure);
        }
        void Inspect(GarageRow? g)
        {
            inspector.Children.Clear();selectedGarage=g?.Id;if(map!=null){map.Selected=g?.Id.Replace("garage.","");map.InvalidateVisual();}
            if(g==null){inspector.Children.Add(Ui.Caption(L.T("garage.pick")));return;}
            var unit=session!.Document.Find(g.Id);bool hq=g.Hq.Length>0;
            inspector.Children.Add(Ui.Trim(Ui.Title(g.City)));if(g.Country.Length>0){var where=Ui.Caption(g.Country);where.Margin=new(0,2,0,0);inspector.Children.Add(where);}
            var sizeLabel=Ui.Label(L.T("garage.size"));sizeLabel.Margin=new(0,20,0,8);inspector.Children.Add(sizeLabel);
            int slots=(int)unit.Number("vehicles");var occupied=Enumerable.Range(0,slots).Where(i=>Occupied(unit.Get($"vehicles[{i}]"))||Occupied(unit.Get($"drivers[{i}]"))).ToList();
            int[] levels=[0,6,2,3];var (strip,options)=Ui.Segmented(levels.Select(l=>L.T("garage.short."+l)).ToList(),Array.IndexOf(levels,g.Status));
            for(int i=0;i<levels.Length;i++){int level=levels[i];Ui.Id(options[i],"garage-level-"+level);AutomationProperties.SetName(options[i],GarageLevel(level));options[i].Checked+=(s,e)=>ChangeGarage(g.Id,level);}
            if(hq||occupied.Count>0||unit.Number("trailers")>0){options[0].IsEnabled=false;options[0].ToolTip=L.T("garage.note");}
            inspector.Children.Add(strip);
            if(g.Status>0)
            {
                var count=Ui.Caption(L.P("count.slots",slots)+" · "+L.P("count.trucks",g.Trucks)+" · "+L.P("count.drivers",g.Drivers));count.Margin=new(0,8,0,0);inspector.Children.Add(count);
            }
            var hqLabel=Ui.Label(L.T("field.hq"));hqLabel.Margin=new(0,20,0,6);inspector.Children.Add(hqLabel);
            if(hq)inspector.Children.Add(Ui.Text(L.T("garage.hq.current"),13,Ui.Accent,FontWeights.SemiBold));
            else{var make=Ui.Id(Ui.Ghost(L.T("garage.hq.set"),()=>Run(()=>{session.Headquarters(g.Id);Navigate("garages");})),"garage-hq");make.Height=28;make.HorizontalAlignment=HorizontalAlignment.Left;make.Margin=new(-10,0,0,0);make.IsEnabled=g.Status>0;inspector.Children.Add(make);}
            if(occupied.Count==0)return;
            inspector.Children.Add(Ui.Rule(20,20));var moveLabel=Ui.Label(L.T("garage.move"));moveLabel.Margin=new(0,0,0,8);inspector.Children.Add(moveLabel);
            string SlotLabel(int i){bool truck=Occupied(unit.Get($"vehicles[{i}]")),driver=Occupied(unit.Get($"drivers[{i}]"));return L.F("garage.slot",i+1)+" · "+(truck&&driver?L.T("garage.slot.both"):truck?L.T("garage.slot.truck"):L.T("garage.slot.driver"));}
            var slot=new ComboBox{ItemsSource=occupied.Select(SlotLabel).ToList(),SelectedIndex=0};AutomationProperties.SetName(slot,L.T("garage.move"));inspector.Children.Add(slot);
            var targets=rows.Where(r=>r.Status>0&&r.Id!=g.Id).ToList();var target=new ComboBox{ItemsSource=targets.Select(r=>L.F("garage.move.to",r.City)).ToList(),SelectedIndex=targets.Count>0?0:-1,Margin=new(0,8,0,0)};inspector.Children.Add(target);
            var move=Ui.Id(Ui.Button(L.T("garage.move.go"),()=>Run(()=>
            {
                if(target.SelectedIndex<0||slot.SelectedIndex<0)throw new InvalidDataException(L.T("garage.move.pick"));
                int index=occupied[slot.SelectedIndex];session.MoveSlot(g.Id,targets[target.SelectedIndex].Id,Occupied(unit.Get($"vehicles[{index}]"))?"vehicles":"drivers",index);Navigate("garages");
            })),"garage-move");move.HorizontalAlignment=HorizontalAlignment.Left;move.Margin=new(0,8,0,0);move.IsEnabled=targets.Count>0;inspector.Children.Add(move);
        }
        void Center(GarageRow row)
        {
            table.SelectedItem=row;int index=shown.IndexOf(row);
            if(index>=0&&Ui.Find<ScrollViewer>(table) is {} viewer&&viewer.ViewportHeight>0)viewer.ScrollToVerticalOffset(Math.Max(0,index-(viewer.ViewportHeight-1)/2));else table.ScrollIntoView(row);
        }
        table.SelectionChanged+=(s,e)=>{if(table.SelectedItem is GarageRow g&&g.Id!=selectedGarage)Inspect(g);};
        if(map!=null)map.CitySelected=id=>{if(rows.FirstOrDefault(r=>r.Id=="garage."+id) is {} row){Inspect(row);if(shown.Contains(row))Center(row);}};
        string? hqCity=session!.Document.Player?.Get("hq_city");
        var initial=rows.FirstOrDefault(r=>r.Id==selectedGarage)??rows.FirstOrDefault(r=>r.Id=="garage."+hqCity);
        Filter();Inspect(initial);
        if(initial!=null&&shown.Contains(initial)){table.SelectedItem=initial;bool centred=false;table.SizeChanged+=(s,e)=>{if(centred||e.NewSize.Height<=0)return;centred=true;Dispatcher.BeginInvoke(()=>Center(initial),System.Windows.Threading.DispatcherPriority.Loaded);};}
        adapt=w=>
        {
            grid.ColumnDefinitions[1].Width=new(w?24:20);grid.ColumnDefinitions[2].Width=new(w?300:256);
            filters.ColumnDefinitions[0].Width=w?new(360):new(1,GridUnitType.Star);filters.ColumnDefinitions[2].Width=new(w?220:200);filters.ColumnDefinitions[5].Width=w?new(1,GridUnitType.Star):new(0);
            bulkLine.ColumnDefinitions[2].Width=new(w?220:200);typeColumn.Width=w?140:120;countColumn.Width=w?190:170;
        };adapt(wide);
        return grid;
    }
    /// <summary>Fills the map area: the road map with legend and zoom buttons, a folder prompt without a game folder, or the loading state.</summary>
    MapView? MapArea(Grid area,List<GarageRow> rows)
    {
        if(mapData!=null)
        {
            var map=new MapView{Data=mapData,Owned=rows.Where(r=>r.Status>0).Select(r=>r.Id.Replace("garage.","")).ToHashSet(),Available=rows.Select(r=>r.Id.Replace("garage.","")).ToHashSet(),Selected=selectedGarage?.Replace("garage.","")};map.Rebuild();
            if(garageMap!=null&&ReferenceEquals(garageMap.Data,mapData))map.RestoreViewport(garageMap.Viewport);garageMap=map;area.Children.Add(map);
            FrameworkElement Key(Brush stroke,double size,string label){var e=new Ellipse{Width=size,Height=size,Stroke=stroke,StrokeThickness=1.6,Margin=new(0,0,6,0),VerticalAlignment=VerticalAlignment.Center};var r=Ui.Row(e,Ui.Text(label,12,Ui.Muted));r.Margin=new(0,0,14,0);return r;}
            var legend=Ui.Row(Key(Ui.Accent,10,L.T("map.legend.owned")),Key(Ui.Brush("#ACB1B6"),7,L.T("map.legend.available")));legend.Margin=new(14,12,0,0);legend.HorizontalAlignment=HorizontalAlignment.Left;legend.VerticalAlignment=VerticalAlignment.Top;legend.IsHitTestVisible=false;area.Children.Add(legend);
            var zoom=new StackPanel{Margin=new(2)};
            foreach(var (icon,label,id,action) in new (string,string,string,Action)[]{(Ui.IconPlus,L.T("map.zoomin"),"map-zoom-in",()=>map.Zoom(1.3)),(Ui.IconMinus,L.T("map.zoomout"),"map-zoom-out",()=>map.Zoom(1/1.3)),(Ui.IconFit,L.T("map.fit"),"map-fit",map.Reset),(Ui.IconTarget,L.T("map.center"),"map-center",map.CenterSelected)})
            {var b=Ui.Id(Ui.IconButton(icon,label,action,28),id);b.Style=(Style)FindResource("FloatButton");zoom.Children.Add(b);}
            area.Children.Add(new Border{Child=zoom,Background=Ui.Raised,CornerRadius=new(6),HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Bottom,Margin=new(12)});
            return map;
        }
        if(install.GamePath==null)
        {
            var state=State(L.T("map.empty.title"),L.T("map.empty.text"),Ui.Button(L.T("flyout.pick"),PickGameFolder));state.Margin=new(20);area.Children.Add(state);
            return null;
        }
        mapStatus=Ui.Caption(mapFailed?L.T("map.failed"):L.F("map.loading",mapProgress));var status=new StackPanel{Margin=new(20),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top,MaxWidth=440,Children={mapStatus}};
        if(mapFailed){var again=Ui.Button(L.T("flyout.rescan"),async()=>await Rescan(install.GamePath,false));again.Margin=new(0,16,0,0);again.HorizontalAlignment=HorizontalAlignment.Left;status.Children.Add(again);}
        area.Children.Add(status);
        return null;
    }
    /// <summary>Reads roads and cities once per game version; the map area shows the progress meanwhile and editing goes on.</summary>
    async Task LoadMap()
    {
        if(mapLoading||install.GamePath==null)return;mapLoading=true;mapProgress="";
        try
        {
            var progress=new Progress<string>(text=>{mapProgress=text;if(mapStatus!=null)mapStatus.Text=L.F("map.loading",text);});
            var loaded=await Task.Run(()=>GameMap.Load(install,progress,cancel.Token));
            if(mapData==null||mapData.Signature!=loaded.Signature||loaded.Signature.Length==0)mapData=loaded;
            await MapView.PrepareAsync(mapData);
        }
        catch(OperationCanceledException){}catch(Exception e){mapFailed=true;Error(e);}
        finally{mapLoading=false;mapStatus=null;if(!closing&&currentPage=="garages"&&Editable)Navigate("garages");}
    }
}
