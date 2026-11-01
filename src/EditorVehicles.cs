using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Werkstatt;
public sealed record VehicleRow(string Id,string Kind,string Model,string Plate,string Odometer,string Condition,string Fuel,double Wear,float? FuelShare,bool Truck);
public sealed partial class MainWindow
{
    string? selectedVehicle;
    Task<GameFiles>? gameFiles;readonly Dictionary<string,PreviewModel> previews=[];int previewTicket;bool capture;
    /// <summary>Shows the 3D model of <paramref name="row"/>. Models are built in the background from the game files and the last four are kept.</summary>
    async void ShowPreview(VehicleStage stage,TextBlock state,TextBlock hint,VehicleRow row)
    {
        AutomationProperties.SetName(stage,L.F("preview.a11y",row.Model));
        var spec=VehiclePreview.Describe(session!.Document,session.Document.Find(row.Id));int ticket=++previewTicket;
        void Show(PreviewModel? model,string? message)
        {
            stage.Show(model);state.Text=message??"";state.Visibility=message==null?Visibility.Collapsed:Visibility.Visible;
            hint.Visibility=model==null?Visibility.Collapsed:Visibility.Visible;
            hint.Text=model is {Missing.Count:>0}?L.T("preview.hint")+" · "+L.P("preview.missing",model.Missing.Count):L.T("preview.hint");
            hint.ToolTip=model is {Missing.Count:>0}?string.Join(Environment.NewLine,model.Missing):null;
        }
        if(previews.TryGetValue(spec.Key,out var ready)){Show(ready,null);return;}
        if(install.GamePath==null){Show(null,L.T("preview.nogame"));return;}
        Show(null,L.T("preview.loading"));
        try
        {
            var found=install;gameFiles??=capture?Task.FromResult(GameFiles.Open(found)):Task.Run(()=>GameFiles.Open(found));
            var files=await gameFiles;
            var built=capture?VehiclePreview.Build(files,spec):await Task.Run(()=>{lock(files)return VehiclePreview.Build(files,spec);});
            if(previews.Count>=4)previews.Remove(previews.Keys.First());previews[spec.Key]=built;
            if(ticket==previewTicket)Show(built.Model.Children.Count>1?built:null,built.Model.Children.Count>1?null:L.T("preview.failed"));
        }
        catch(Exception e) when(e is not OutOfMemoryException){if(ticket==previewTicket)Show(null,L.T("preview.failed"));}
    }
    void ClosePreviews(){previews.Clear();if(gameFiles is {IsCompletedSuccessfully:true} task){var files=task.Result;lock(files)files.Dispose();}gameFiles=null;}
    static string Pretty(string token)
    {
        var words=token.Split('.').Last().Split('_',StringSplitOptions.RemoveEmptyEntries).Select(w=>w.Length<=3&&w.Any(char.IsLetter)?w.ToUpperInvariant():CultureInfo.InvariantCulture.TextInfo.ToTitleCase(w));
        string model=string.Join(' ',words);string brand=token.Contains('.')?token.Split('.')[0]:"";
        return brand.Length==0||brand=="scs"?model:CultureInfo.InvariantCulture.TextInfo.ToTitleCase(brand)+" "+model;
    }
    static double MaxWear(SiiUnit unit)=>unit.Fields.Where(f=>f.Key.Split('[')[0].EndsWith("wear")||f.Key.Split('[')[0].EndsWith("wear_unfixable")).Where(f=>!f.Key.StartsWith("wheels_wear")||f.Key.Contains('['))
        .Select(f=>(double)(SaveSession.FloatValue(f.Value)??0)).DefaultIfEmpty(0).Max();
    internal static string VehicleName(SiiDocument doc,SiiUnit unit)
    {
        string pattern=unit.Type=="vehicle"?@"/def/vehicle/truck/([^/]+)/":@"/def/vehicle/trailer_(?:owned|def)/([^/]+)/";
        foreach(var field in unit.Fields.Where(f=>f.Key.StartsWith("accessories[",StringComparison.Ordinal)))
            if(doc.Resolve(unit,field.Key)?.Get("data_path") is {} path&&Regex.Match(path,pattern) is {Success:true} m)return Pretty(m.Groups[1].Value);
        return unit.Type=="vehicle"?L.T("vehicle.truck"):L.T("vehicle.trailer");
    }
    static string Percent(double share)=>Math.Round(share*100).ToString("0",L.Culture)+" %";
    List<VehicleRow> VehicleRows()
    {
        var doc=session!.Document;
        return session.OwnedVehicles().Select(u=>
        {
            double wear=MaxWear(u);bool truck=u.Type=="vehicle";float? fuel=truck?SaveSession.FloatValue(u.Get("fuel_relative")):null;
            string odometer=long.TryParse(u.Get("odometer"),NumberStyles.Integer,CultureInfo.InvariantCulture,out long km)?L.F("unit.km",CompactNumber.Full(km)):"—";
            return new VehicleRow(u.Id,truck?L.T("vehicle.truck"):L.T("vehicle.trailer"),VehicleName(doc,u),SaveSession.PlateText(u.Get("license_plate")),odometer,Percent(1-wear),fuel==null?"—":Percent(fuel.Value),wear,fuel,truck);
        }).ToList();
    }
    FrameworkElement TrucksPage()
    {
        var rows=VehicleRows();var grid=Ui.Grid("*,24,264","32,16,Auto,16,*");int trucks=rows.Count(r=>r.Truck);
        void Service(IReadOnlyList<VehicleRow> targets,bool repair)=>Run(()=>
        {
            var before=session!.Document;session.Service(targets.Select(t=>t.Id),repair,!repair,repair);
            int n=targets.Count(t=>!before.Find(t.Id).Fields.Select(f=>f.Value).SequenceEqual(session.Document.Find(t.Id).Fields.Select(f=>f.Value)));
            Navigate("trucks");Notify(n>0?L.P(repair?"vehicles.repaired":"vehicles.refuelled",n):L.T(repair?"vehicles.norepair":"vehicles.norefuel"));
        });
        var toolbar=Ui.At(grid,new DockPanel(),0,0);
        var repairAll=Ui.Id(Ui.Button(L.T("vehicles.repair"),()=>Service(rows,true)),"vehicles-repair");repairAll.ToolTip=L.T("vehicles.note");repairAll.IsEnabled=rows.Count>0;
        var refuelAll=Ui.Id(Ui.Button(L.T("vehicles.refuel"),()=>Service(rows.Where(r=>r.Truck).ToList(),false)),"vehicles-refuel");refuelAll.Margin=new(8,0,0,0);refuelAll.IsEnabled=trucks>0;
        var actions=Ui.Row(repairAll,refuelAll);DockPanel.SetDock(actions,Dock.Right);toolbar.Children.Add(actions);
        if(rows.Count==0){Ui.At(grid,State(L.T("trucks.empty.title"),L.T("trucks.empty.body")),0,2,3);return grid;}
        var summary=Ui.Figures(L.P("count.trucks",trucks)+" · "+L.P("count.trailers",rows.Count-trucks)+" · "+L.F("trucks.lowest",Percent(1-rows.Max(r=>r.Wear))));
        summary.TextWrapping=TextWrapping.NoWrap;summary.TextTrimming=TextTrimming.CharacterEllipsis;summary.VerticalAlignment=VerticalAlignment.Center;summary.Margin=new(0,0,16,0);summary.ToolTip=summary.Text;toolbar.Children.Add(summary);
        var table=Ui.At(grid,Ui.Table("ScrollRegionTrucks",null,(L.T("col.kind"),nameof(VehicleRow.Kind),96,false),(L.T("col.model"),nameof(VehicleRow.Model),-1,false),(L.T("col.plate"),nameof(VehicleRow.Plate),110,false),
            (L.T("col.odometer"),nameof(VehicleRow.Odometer),100,true),(L.T("col.condition"),nameof(VehicleRow.Condition),90,true),(L.T("col.fuel"),nameof(VehicleRow.Fuel),64,true)),0,2);
        table.ItemsSource=rows;AutomationProperties.SetName(table,L.T("tab.trucks"));
        var stageArea=Ui.At(grid,new Grid(),0,4);stageArea.SizeChanged+=(s,e)=>stageArea.Clip=new RectangleGeometry(new Rect(e.NewSize),8,8);
        var stage=Ui.Id(new VehicleStage(),"vehicle-preview");stageArea.Children.Add(stage);
        var stageState=Ui.Caption("");stageState.HorizontalAlignment=HorizontalAlignment.Center;stageState.VerticalAlignment=VerticalAlignment.Center;stageState.TextAlignment=TextAlignment.Center;stageState.MaxWidth=360;stageState.TextWrapping=TextWrapping.Wrap;stageArea.Children.Add(stageState);
        var hint=Ui.Caption(L.T("preview.hint"));hint.Margin=new(14,0,14,12);hint.VerticalAlignment=VerticalAlignment.Bottom;hint.HorizontalAlignment=HorizontalAlignment.Left;stageArea.Children.Add(hint);
        var inspector=new StackPanel();var side=Ui.At(grid,new ScrollViewer{Name="ScrollRegionVehicle",Content=inspector,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Focusable=false},2);Grid.SetRowSpan(side,5);
        void Inspect(VehicleRow r)
        {
            selectedVehicle=r.Id;inspector.Children.Clear();ShowPreview(stage,stageState,hint,r);
            inspector.Children.Add(Ui.Trim(Ui.Title(r.Model)));var sub=Ui.Caption(r.Truck&&r.Plate.Length>0?r.Kind+" · "+r.Plate:r.Kind);sub.Margin=new(0,2,0,0);inspector.Children.Add(sub);
            var facts=Ui.Grid("Auto,12,*,10,44","Auto,12,Auto,12,Auto");facts.Margin=new(0,20,0,0);inspector.Children.Add(facts);int line=0;
            void Fact(string label,UIElement value,string? figure=null,bool field=false)
            {
                var name=Ui.Label(label);name.VerticalAlignment=field?VerticalAlignment.Top:VerticalAlignment.Center;if(field)name.Margin=new(0,8,0,0);Ui.At(facts,name,0,line);
                if(figure==null)Ui.At(facts,value,2,line,3);else{Ui.At(facts,value,2,line);var v=Ui.Tabular(Ui.Text(figure,13));v.TextAlignment=TextAlignment.Right;v.VerticalAlignment=VerticalAlignment.Center;Ui.At(facts,v,4,line);}
                line+=2;
            }
            FrameworkElement Bar(double share,bool warn){var bar=new Grid{Height=4,VerticalAlignment=VerticalAlignment.Center};bar.ColumnDefinitions.Add(new(){Width=new(Math.Max(share,.0001),GridUnitType.Star)});bar.ColumnDefinitions.Add(new(){Width=new(Math.Max(1-share,.0001),GridUnitType.Star)});
                var track=new Border{Background=Ui.Raised,CornerRadius=new(2)};Grid.SetColumnSpan(track,2);bar.Children.Add(track);bar.Children.Add(new Border{Background=warn&&share<.5?Ui.Danger:Ui.Accent,CornerRadius=new(2)});return bar;}
            Fact(L.T("col.condition"),Bar(1-r.Wear,true),r.Condition);
            if(r.FuelShare is {} fuel)Fact(L.T("col.fuel"),Bar(fuel,false),r.Fuel);
            if(OdometerField(r) is {} odometer)Fact(L.T("col.odometer"),odometer,null,true);
            else{var km=Ui.Tabular(Ui.Text(r.Odometer,13));km.VerticalAlignment=VerticalAlignment.Center;Fact(L.T("col.odometer"),km);}
            while(facts.RowDefinitions.Count>line-1)facts.RowDefinitions.RemoveAt(facts.RowDefinitions.Count-1);
            var repair=Ui.Id(Ui.Button(L.T("vehicle.repair"),()=>Service([r],true)),"vehicle-repair");repair.ToolTip=L.T("vehicles.note");
            var refuel=Ui.Id(Ui.Button(L.T("vehicle.refuel"),()=>Service([r],false)),"vehicle-refuel");refuel.Margin=new(8,0,0,0);refuel.IsEnabled=r.Truck;
            if(!r.Truck){refuel.ToolTip=L.T("vehicle.notank");ToolTipService.SetShowOnDisabled(refuel,true);}
            var buttons=Ui.Row(repair,refuel);buttons.Margin=new(0,20,0,0);inspector.Children.Add(buttons);
            if(!r.Truck)return;
            inspector.Children.Add(Ui.Rule(20,20));var plateLabel=Ui.Label(L.T("plate.label"));plateLabel.Margin=new(0,0,0,6);inspector.Children.Add(plateLabel);
            var plate=Ui.Id(Ui.Input(r.Plate,L.T("plate.label")),"plate-input");plate.MaxLength=12;var error=Ui.Caption("");error.Foreground=Ui.Danger;error.Margin=new(0,6,0,0);error.Visibility=Visibility.Collapsed;
            var change=Ui.Id(Ui.Button(L.T("plate.change"),()=>
            {
                ClearMessage();
                try{session!.SetLicensePlate(r.Id,plate.Text.Trim());Navigate("trucks");Notify(L.T("plate.done"));}
                catch(Exception e){error.Text=e.Message;error.Visibility=Visibility.Visible;plate.BorderBrush=Ui.Danger;}
            }),"plate-change");change.MinWidth=0;change.IsEnabled=false;
            plate.TextChanged+=(s,e)=>{change.IsEnabled=plate.Text.Trim().Length>0&&plate.Text.Trim()!=r.Plate;error.Visibility=Visibility.Collapsed;plate.ClearValue(Control.BorderBrushProperty);};
            var field=Ui.Grid("*,8,Auto");Ui.At(field,plate);Ui.At(field,change,2);inspector.Children.Add(field);inspector.Children.Add(error);
        }
        FrameworkElement? OdometerField(VehicleRow r)
        {
            if(!long.TryParse(session!.Document.Find(r.Id).Get("odometer"),NumberStyles.None,CultureInfo.InvariantCulture,out long km))return null;
            string initial=CompactNumber.Full(km);var input=Ui.Id(Ui.Tabular(Ui.Input(initial,L.T("odometer.label"))),"odometer-input");input.MaxLength=16;input.Padding=new(7,0,30,0);
            var unit=Ui.Text(L.F("unit.km","").Trim(),12,Ui.Muted);unit.HorizontalAlignment=HorizontalAlignment.Right;unit.VerticalAlignment=VerticalAlignment.Center;unit.Margin=new(0,0,10,0);unit.IsHitTestVisible=false;
            var error=Ui.Id(Ui.Caption(""),"odometer-error");error.Foreground=Ui.Danger;error.Margin=new(0,6,0,0);error.Visibility=Visibility.Collapsed;
            void Fail(string text){error.Text=text;error.Visibility=Visibility.Visible;input.BorderBrush=Ui.Danger;}
            var set=Ui.Id(Ui.Button(L.T("odometer.set"),()=>
            {
                ClearMessage();long value;
                try{value=CompactNumber.Parse(input.Text,SaveSession.MaxOdometer);}
                catch(InvalidDataException){Fail(L.F("input.range",L.T("odometer.label"),CompactNumber.Full(0),CompactNumber.Full(SaveSession.MaxOdometer)));return;}
                try{session.SetOdometer(r.Id,value);Navigate("trucks");Notify(L.T("odometer.done"));}catch(Exception e){Fail(e.Message);}
            }),"odometer-set");set.MinWidth=0;set.IsEnabled=false;
            input.TextChanged+=(s,e)=>{set.IsEnabled=input.Text.Trim().Length>0&&input.Text.Trim()!=initial;error.Visibility=Visibility.Collapsed;input.ClearValue(Control.BorderBrushProperty);};
            var field=Ui.Grid("*,8,Auto");Ui.At(field,new Grid{Children={input,unit}});Ui.At(field,set,2);
            return new StackPanel{Children={field,error}};
        }
        table.SelectionChanged+=(s,e)=>{if(table.SelectedItem is VehicleRow r)Inspect(r);};
        var initial=rows.FirstOrDefault(r=>r.Id==selectedVehicle)??rows[0];table.SelectedItem=initial;Inspect(initial);
        adapt=w=>{grid.ColumnDefinitions[2].Width=new(w?320:264);table.MaxHeight=w?250:182;};adapt(wide);
        return grid;
    }
}
