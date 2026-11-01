using Microsoft.Win32;
using System.Globalization;
using System.Windows;

namespace Werkstatt;
public sealed partial class MainWindow
{
    void History(bool forward)
    {
        if(session==null||writing||loading||guideStep>0||!FlushForm())return;
        ClearMessage();if(!(forward?session.Redo():session.Undo()))return;
        formDirty=false;applyForm=null;Navigate(currentPage);
    }
    async Task FileAction(Func<string> operation,Action<string> completed,Window? dialog=null,bool closeDialog=true)
    {
        if(writing)return;writing=true;IsEnabled=false;if(dialog!=null)dialog.IsEnabled=false;ClearMessage();UpdateState();
        string result;
        try{result=await Task.Run(operation);}
        catch(Exception e)
        {
            writing=false;IsEnabled=true;if(dialog!=null){dialog.IsEnabled=true;Notify(e.Message,Ui.Danger);Ui.Ask(dialog,L.T("error.title"),e.Message,L.T("ok"));}else Error(e);
            UpdateState();return;
        }
        writing=false;IsEnabled=true;if(dialog!=null){dialog.IsEnabled=true;if(closeDialog)dialog.Close();}
        try{completed(result);}catch(Exception e){Error(e);}
        finally{UpdateState();}
    }
    void ExportCopy(Window? dialog=null)
    {
        if(session==null||writing||!FlushForm())return;
        string source=Path.GetDirectoryName(session.FilePath)!;
        var picker=new OpenFolderDialog{Title=L.T("copy.pick"),InitialDirectory=new DirectoryInfo(source).Parent?.Name=="save"?Path.GetDirectoryName(source):source};
        if(picker.ShowDialog(dialog??this)!=true)return;
        string folder=Path.Combine(picker.FolderName,"werkstatt_"+DateTime.Now.ToString("yyyyMMdd_HHmmss",CultureInfo.InvariantCulture)+"_"+Guid.NewGuid().ToString("N")[..8]);
        var active=session;
        _=FileAction(()=>active.ExportCopy(folder),result=>{Navigate(currentPage);Notify(L.F("commit.copied",Path.GetFileName(result)),Ui.Ink,true);},dialog);
    }
    sealed record ChangeRow(string Area,string Label,string Before,string After,string Tip);
    static readonly string[] AreaOrder=["area.file","area.account","area.experience","time.title","area.skills","area.cities","area.garages","area.trucks","area.drivers","area.other"];
    (SiiDocument Document,string Code,bool Format,Dictionary<string,CityInfo> Cities,List<(string Area,ChangeRow Row)> Rows)? reviewCache;
    /// <summary>The changes as the player thinks of them: one row per garage, per vehicle condition, fuel and plate, per hired driver,
    /// one for the visited cities and one per other value. The raw fields go to the row tooltip. Cached per staged document.</summary>
    List<(string Area,ChangeRow Row)> ReviewRows()
    {
        if(session==null)return [];
        if(reviewCache is {} cache&&ReferenceEquals(cache.Document,session.Document)&&cache.Code==L.Code&&cache.Format==session.NeedsFormatConversion&&ReferenceEquals(cache.Cities,cityIndex))return cache.Rows;
        var rows=new List<(string Area,ChangeRow Row)>();
        if(session.NeedsFormatConversion)rows.Add(("area.file",new(L.T("area.file"),L.T("review.format"),L.T("review.format.before"),L.T("review.format.after"),L.T("notice.bsii"))));
        if(session.Dirty)foreach(var group in session.Changes().GroupBy(Topic))rows.Add(ReviewRow(group.Key,group.ToList()));
        rows=rows.OrderBy(r=>Array.IndexOf(AreaOrder,r.Area)).ToList();
        reviewCache=(session.Document,L.Code,session.NeedsFormatConversion,cityIndex,rows);return rows;
    }
    int PendingCount=>ReviewRows().Count;
    IEnumerable<string> ChangedAreas()=>ReviewRows().Select(r=>r.Area).Distinct().Select(L.T);
    SiiUnit UnitOf(Change change)=>session!.Document.TryFind(change.Unit)??session.Original.Find(change.Unit);
    /// <summary>The review row a field change belongs to.</summary>
    string Topic(Change change)
    {
        if(change.Unit==SaveSession.LayoutUnit)return "layout";
        if(change.Field==SaveSession.EntryField)return "entry "+change.Unit;
        var unit=UnitOf(change);string key=change.Field.Split('[')[0];
        return unit.Type switch
        {
            "economy" when key is "visited_cities" or "visited_cities_count"=>"cities",
            "company" when key=="discovered"=>"cities",
            "economy" when key=="unlocked_dealers"=>"dealers",
            "economy" when key=="unlocked_recruitments"=>"recruitments",
            "economy" when key=="red_light_fine_count"=>"record",
            "police_offence_log"=>"record",
            "bus_stop" when key=="discovered"=>"busstops",
            "garage"=>"garage "+unit.Id,
            "vehicle" or "trailer" when key=="fuel_relative"=>"fuel "+unit.Id,
            "vehicle" or "trailer" when key=="license_plate"=>"plate "+unit.Id,
            "vehicle" or "trailer" when key is "odometer" or "odometer_float_part" or "integrity_odometer" or "integrity_odometer_float_part"=>"odometer "+unit.Id,
            "vehicle" or "trailer" when key.Contains("wear")||key=="cargo_damage"=>"condition "+unit.Id,
            "driver_ai"=>"driver "+unit.Id,
            _=>"field "+unit.Id+" "+change.Field
        };
    }
    static string Share(double share)=>share>=1?"100 %":share<=0?"0 %":Math.Clamp(Math.Floor(share*1000)/10,.1,99.9).ToString("0.#",L.Culture)+" %";
    (string Area,ChangeRow Row) ReviewRow(string topic,List<Change> changes)
    {
        string raw=string.Join("\n",changes.Take(12).Select(c=>$"{c.Unit} · {c.Field}: {(c.Before==SaveSession.Removed?"—":c.Before)} → {(c.After==SaveSession.Removed?"—":c.After)}"))+(changes.Count>12?"\n…":"");
        (string,ChangeRow) Row(string area,string label,string before,string after)=>(area,new(L.T(area),label,before,after,$"{label}: {before} → {after}\n\n{raw}"));
        var change=changes[0];
        if(topic=="layout")return Row("area.file",L.T("review.layout"),L.T("review.original"),L.T("review.changed"));
        if(topic=="cities")return Row("area.cities",L.T("review.visited"),CompactNumber.Full(session!.Original.Economy?.Number("visited_cities")??0),CompactNumber.Full(session.Document.Economy?.Number("visited_cities")??0));
        if(topic is "dealers" or "recruitments")
        {
            string field=topic=="dealers"?"unlocked_dealers":"unlocked_recruitments";
            return Row("area.cities",L.T(topic=="dealers"?"review.dealers":"review.agencies"),CompactNumber.Full(session!.Original.Economy?.Number(field)??0),CompactNumber.Full(session.Document.Economy?.Number(field)??0));
        }
        if(topic=="busstops")
        {
            string Stops(SiiDocument d)=>L.F("review.busstops.value",CompactNumber.Full(d.Units.Count(u=>u.Type=="bus_stop"&&u.Get("discovered")=="true")));
            return Row("area.cities",L.T("review.busstops"),Stops(session!.Original),Stops(session.Document));
        }
        if(topic=="record")return Row("area.account",L.T("review.record"),L.P("count.offences",SaveSession.Offences(session!.Original)),L.P("count.offences",SaveSession.Offences(session.Document)));
        var unit=UnitOf(change);SiiUnit? old=session!.Original.TryFind(unit.Id),now=session.Document.TryFind(unit.Id);
        string Vehicle(string label)=>VehicleName(session.Document,unit)+" · "+label;
        static double Condition(SiiUnit u)=>1-Math.Max(MaxWear(u),SaveSession.FloatValue(u.Get("cargo_damage"))??0);
        static int Taken(SiiUnit g,string key)=>Enumerable.Range(0,(int)g.Number(key)).Count(i=>Occupied(g.Get($"{key}[{i}]")));
        switch(topic.Split(' ')[0])
        {
            case "entry":
                string area=unit.Type switch{"garage"=>"area.garages","vehicle" or "trailer"=>"area.trucks","driver_ai"=>"area.drivers","company"=>"area.cities",_=>"area.other"};
                return Row(area,unit.Id+" · "+L.T("review.entry"),change.Before,change.After==SaveSession.Removed?L.T("review.removed"):change.After);
            case "garage":
                bool moved=old!=null&&now!=null&&(Taken(old,"vehicles")!=Taken(now,"vehicles")||Taken(old,"drivers")!=Taken(now,"drivers"));
                string Garage(SiiUnit? g)=>g==null?"—":GarageLevel(g.Number("status"))+(moved?" · "+L.P("count.trucks",Taken(g,"vehicles"))+" · "+L.P("count.drivers",Taken(g,"drivers")):"");
                return Row("area.garages",CityName(unit.Id.Replace("garage.",""))+" · "+L.T("review.garage"),Garage(old),Garage(now));
            case "condition":return Row("area.trucks",Vehicle(L.T("col.condition")),old==null?"—":Share(Condition(old)),now==null?"—":Share(Condition(now)));
            case "fuel":return Row("area.trucks",Vehicle(L.T("col.fuel")),Share(SaveSession.FloatValue(old?.Get("fuel_relative"))??0),Share(SaveSession.FloatValue(now?.Get("fuel_relative"))??0));
            case "plate":return Row("area.trucks",Vehicle(L.T("plate.label")),SaveSession.PlateText(old?.Get("license_plate")),SaveSession.PlateText(now?.Get("license_plate")));
            case "odometer":
                string Km(SiiUnit? v)=>long.TryParse(v?.Get("odometer"),NumberStyles.Integer,CultureInfo.InvariantCulture,out long km)?L.F("unit.km",CompactNumber.Full(km)):"—";
                return Row("area.trucks",Vehicle(L.T("odometer.label")),Km(old),Km(now));
            case "driver":
                string Driver(SiiUnit? d)=>d==null?"—":L.F("review.driver.value",SaveSession.SkillKeys.Sum(k=>d.Number(k)),System.Numerics.BitOperations.PopCount((uint)(d.Number("adr")&63)));
                return Row("area.drivers",L.F("review.driver",unit.Id.Split('.')[^1])+" · "+L.T("review.driver.change"),Driver(old),Driver(now));
        }
        string key=change.Field.Split('[')[0];
        string fieldArea=unit.Type switch
        {
            "bank"=>"area.account",
            "economy"=>key switch{"experience_points"=>"area.experience","game_time"=>"time.title",_ when SaveSession.SkillKeys.Contains(key)||key=="adr"=>"area.skills",_=>"area.other"},
            "player" when key=="hq_city"=>"area.garages",_=>"area.other"
        };
        string label=key switch
        {
            "money_account"=>L.T("money.title"),"experience_points"=>L.T("xp.level"),"game_time"=>L.T("time.change"),"hq_city"=>L.T("field.hq"),"adr"=>L.T("adr.title"),
            _ when SaveSession.SkillKeys.Contains(key)=>L.T("skill."+key),_=>change.Field
        };
        string Display(string value)
        {
            if(value==SaveSession.Removed)return L.T("review.removed");
            if(value=="—")return value;
            long.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out long number);
            return key switch
            {
                "hq_city"=>CityName(value),
                "adr"=>number==0?L.T("review.none"):string.Join(L.Code=="zh"?"、":", ",new[]{1,2,3,4,6,8}.Where((_,i)=>(number&(1<<i))!=0)),
                "experience_points"=>L.F("review.xp",CompactNumber.Full(xpCurve.Level((int)Math.Clamp(number,0,int.MaxValue))),CompactNumber.Full(number)),
                "game_time"=>L.F("time.value",CompactNumber.Full(number/1440),$"{number/60%24:00}:{number%60:00}"),
                "money_account"=>L.F("money.value",CompactNumber.Full(number)),
                _=>long.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out _)?CompactNumber.Full(number):SiiDocument.Display(value)
            };
        }
        return Row(fieldArea,label,Display(change.Before),Display(change.After));
    }
}
