using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Werkstatt;
internal static class CompactNumber
{
    const decimal K=1_000m,M=1_000_000m,G=1_000_000_000m,T=1_000_000_000_000m,P=1_000_000_000_000_000m,E=1_000_000_000_000_000_000m;
    static (string Unit,decimal Factor)[] Units=>L.Code switch
    {
        "de"=>[("k",K),("Mio",M),("Mrd",G),("Bio",T),("Brd",P),("Trio",E)],
        "pl"=>[("tys.",K),("mln",M),("mld",G),("bln",T),("bld",P),("tryl.",E)],
        "ru"=>[("тыс.",K),("млн",M),("млрд",G),("трлн",T),("квадрлн",P),("квинтлн",E)],
        "zh"=>[("万",10_000m),("亿",100_000_000m),("万亿",T),("亿亿",10_000_000_000_000_000m)],
        _=>[("k",K),("M",M),("B",G),("T",T),("Qa",P),("Qi",E)]
    };
    public static string UnitList=>string.Join(" · ",Units.Select(u=>u.Unit));
    static bool CommaDecimal=>L.Code is "de" or "pl" or "ru";
    static decimal? Factor(string unit)
    {
        bool longScale=L.Code is "de" or "pl";
        return unit switch
        {
            ""=>1,"k" or "tsd" or "tausend" or "thousand" or "tys" or "тыс" or "тысяч" or "тысяча" or "тысячи" or "千"=>K,"万" or "萬"=>10_000m,
            "m" or "mio" or "mln" or "mn" or "million" or "millions" or "millionen" or "milion" or "miliony" or "milionów" or "млн" or "миллион" or "миллиона" or "миллионов" or "百万"=>M,"亿" or "億"=>100_000_000m,
            "mrd" or "mld" or "b" or "bn" or "milliarde" or "milliarden" or "miliard" or "miliardy" or "miliardów" or "млрд" or "миллиард" or "миллиарда" or "миллиардов" or "十亿"=>G,
            "bio" or "bln" or "t" or "tn" or "трлн" or "триллион" or "триллиона" or "триллионов" or "万亿"=>T,
            "billion" or "billions" or "billionen" or "bilion" or "biliony"=>longScale?T:G,
            "brd" or "bld" or "qa" or "billiarde" or "billiarden" or "biliard" or "quadrillion" or "квдрлн" or "квадрлн" or "квадриллион"=>P,"亿亿"=>10_000_000_000_000_000m,
            "trillion" or "trillions" or "trillionen" or "trylion"=>longScale?E:T,
            "trio" or "tryl" or "qi" or "quintillion" or "квинтлн" or "квинтиллион"=>E,
            _=>null
        };
    }
    /// <summary>Parses compact input like 30k or 1,5 Mio. A leading minus is accepted only when <paramref name="minimum"/> is negative (money: debt).</summary>
    public static long Parse(string text,long maximum=long.MaxValue,long minimum=0)
    {
        text=Regex.Replace(text.Replace("€",""),@"\s","").Trim();
        bool negative=text.Length>1&&text[0] is '-' or '−';
        if(negative){if(minimum>=0)throw new InvalidDataException(RangeMessage(minimum,maximum));text=text[1..];}
        long limit=negative?-minimum:maximum;
        if(text.ToLowerInvariant() is "max" or "maximum" or "maks" or "maksimum" or "макс" or "максимум" or "最大")return negative?minimum:maximum;
        var match=Regex.Match(text,@"^(?<number>\+?\d[\d.,]*)(?<unit>[\p{L}]*\.?)$");
        if(!match.Success)throw new InvalidDataException(L.F("num.invalid",Short(30_000),Short(1_500_000)));
        string n=match.Groups["number"].Value,unit=match.Groups["unit"].Value.TrimEnd('.').ToLowerInvariant();
        decimal factor=Factor(unit)??throw new InvalidDataException(L.F("num.units",UnitList));
        if(n.Contains('.')&&n.Contains(','))
        {
            bool commaLast=n.LastIndexOf(',')>n.LastIndexOf('.');
            if(!Regex.IsMatch(n,commaLast?@"^\+?\d{1,3}(?:\.\d{3})+,\d+$":@"^\+?\d{1,3}(?:,\d{3})+\.\d+$"))throw new InvalidDataException(L.T("num.separators"));
            n=commaLast?n.Replace(".","").Replace(',','.'):n.Replace(",","");
        }
        else if(CommaDecimal)
        {
            if(Regex.IsMatch(n,@"^\+?\d{1,3}(?:\.\d{3})+$"))n=n.Replace(".","");
            else if(n.Count(c=>c==',')>1&&Regex.IsMatch(n,@"^\+?\d{1,3}(?:,\d{3})+$"))n=n.Replace(",","");
            else n=n.Replace(',','.');
        }
        else
        {
            if(Regex.IsMatch(n,@"^\+?\d{1,3}(?:,\d{3})+$"))n=n.Replace(",","");
            else if(Regex.IsMatch(n,@"^\+?\d{1,3}(?:\.\d{3}){2,}$"))n=n.Replace(".","");
            else if(n.Count(c=>c==',')==1&&!n.Contains('.'))n=n.Replace(',','.');
        }
        if(!decimal.TryParse(n,NumberStyles.AllowDecimalPoint|NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out var number))throw new InvalidDataException(L.T("num.bad"));
        decimal value;try{value=number*factor;}catch(OverflowException){throw new InvalidDataException(L.T("num.overflow"));}
        if(value<0||value>limit)throw new InvalidDataException(RangeMessage(minimum,maximum));
        if(value!=decimal.Truncate(value))throw new InvalidDataException(L.T("num.whole"));
        return negative?-(long)value:(long)value;
    }
    static string RangeMessage(long minimum,long maximum)=>minimum<0?L.F("num.range.signed",Full(minimum),Full(maximum)):L.F("num.range",Full(maximum));
    /// <summary>Money may go negative (a debt), symmetric to the maximum so the value always fits the save's signed 64-bit field.</summary>
    public const long MinMoney=-long.MaxValue;
    public static string Full(long value)=>value.ToString("N0",L.Culture);
    public static string Editable(long value){string compact=Short(value);return Parse(compact,long.MaxValue,MinMoney)==value?compact:Full(value);}
    public static string Short(long value)
    {
        if(value<0)return value==long.MinValue?Full(value):"-"+Short(-value);
        var units=Units;string gap=L.Code=="zh"?"":" ";
        for(int i=units.Length-1;i>=0;i--)if(value>=units[i].Factor)return ((decimal)value/units[i].Factor).ToString("0.###",L.Culture)+gap+units[i].Unit;
        return Full(value);
    }
    /// <summary>At most one decimal and a sign, for short status texts such as "-300.5 k"; not meant to be parsed back.</summary>
    public static string Brief(long value)
    {
        var units=Units;string gap=L.Code=="zh"?"":" ",sign=value<0?L.Culture.NumberFormat.NegativeSign:"";decimal size=Math.Abs((decimal)value);
        for(int i=units.Length-1;i>=0;i--)if(size>=units[i].Factor)return sign+(size/units[i].Factor).ToString("0.#",L.Culture)+gap+units[i].Unit;
        return Full(value);
    }
}

internal sealed class XpCurve
{
    static readonly int[] Defaults=[200,500,700,900,1000,1100,1300,1600,1700,2100,2300,2600,2700,2900,3000,3100,3400,3700,4000,4300,4600,4700,4900,5200,5700,5900,6000,6200,6600,6800];
    readonly long[] thresholds;
    readonly int lastGain;
    public string Source {get;}
    internal const string DefaultSource="default";
    public static XpCurve Standard=>new(Defaults,DefaultSource);
    internal XpCurve(int[] gains,string source)
    {
        if(gains.Length==0||gains.Length>1000||gains.Any(g=>g<=0))throw new InvalidDataException("Invalid XP curve.");
        thresholds=new long[gains.Length+1];for(int i=0;i<gains.Length;i++)thresholds[i+1]=thresholds[i]+gains[i];
        lastGain=gains[^1];Source=source;
    }
    public int MaxLevel=>Level(int.MaxValue);
    public int Level(int xp)
    {
        if(xp<0)throw new ArgumentOutOfRangeException(nameof(xp));
        int index=Array.BinarySearch(thresholds,(long)xp);
        if(index>=0)return index;
        int next=~index;if(next<thresholds.Length)return next-1;
        return thresholds.Length-1+(int)((xp-thresholds[^1])/lastGain);
    }
    public int Xp(int level)
    {
        if(level<0||level>MaxLevel)throw new InvalidDataException(L.F("xp.range",CompactNumber.Full(MaxLevel)));
        return checked((int)(level<thresholds.Length?thresholds[level]:thresholds[^1]+(long)(level-thresholds.Length+1)*lastGain));
    }
    public static XpCurve Load(string? gamePath)
    {
        if(gamePath==null)return Standard;
        try
        {
            using var archive=TruckLib.HashFs.HashFsReader.Open(Path.Combine(gamePath,"def.scs"));
            foreach(var path in new[]{"def/economy_data_xp.sii","def/economy_data.sii"})
            {
                if(!archive.FileExists(path))continue;
                string text=Encoding.UTF8.GetString(archive.Extract(path)[0]);
                var entries=Regex.Matches(text,@"(?m)^\s*level_xp\[(\d+)\]:\s*(\d+)").Select(m=>(Index:int.Parse(m.Groups[1].Value),Gain:int.Parse(m.Groups[2].Value))).OrderBy(x=>x.Index).ToList();
                if(entries.Count>0&&entries.Select(e=>e.Index).SequenceEqual(Enumerable.Range(0,entries.Count)))return new(entries.Select(e=>e.Gain).ToArray(),Path.GetFileName(path));
            }
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or NotSupportedException){}
        return Standard;
    }
}
