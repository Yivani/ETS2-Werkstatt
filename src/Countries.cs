namespace Werkstatt;
/// <summary>ETS2 country tokens (def/country) with names in the five interface languages.</summary>
public static class Countries
{
    static readonly Dictionary<string,string[]> Names=new()
    {
        ["albania"]=["Albania","Albanien","Albania","Албания","阿尔巴尼亚"],
        ["austria"]=["Austria","Österreich","Austria","Австрия","奥地利"],
        ["belgium"]=["Belgium","Belgien","Belgia","Бельгия","比利时"],
        ["bosnia"]=["Bosnia and Herzegovina","Bosnien und Herzegowina","Bośnia i Hercegowina","Босния и Герцеговина","波斯尼亚和黑塞哥维那"],
        ["bulgaria"]=["Bulgaria","Bulgarien","Bułgaria","Болгария","保加利亚"],
        ["croatia"]=["Croatia","Kroatien","Chorwacja","Хорватия","克罗地亚"],
        ["czech"]=["Czechia","Tschechien","Czechy","Чехия","捷克"],
        ["denmark"]=["Denmark","Dänemark","Dania","Дания","丹麦"],
        ["estonia"]=["Estonia","Estland","Estonia","Эстония","爱沙尼亚"],
        ["finland"]=["Finland","Finnland","Finlandia","Финляндия","芬兰"],
        ["france"]=["France","Frankreich","Francja","Франция","法国"],
        ["germany"]=["Germany","Deutschland","Niemcy","Германия","德国"],
        ["greece"]=["Greece","Griechenland","Grecja","Греция","希腊"],
        ["hungary"]=["Hungary","Ungarn","Węgry","Венгрия","匈牙利"],
        ["ireland"]=["Ireland","Irland","Irlandia","Ирландия","爱尔兰"],
        ["italy"]=["Italy","Italien","Włochy","Италия","意大利"],
        ["kosovo"]=["Kosovo","Kosovo","Kosowo","Косово","科索沃"],
        ["latvia"]=["Latvia","Lettland","Łotwa","Латвия","拉脱维亚"],
        ["lithuania"]=["Lithuania","Litauen","Litwa","Литва","立陶宛"],
        ["luxembourg"]=["Luxembourg","Luxemburg","Luksemburg","Люксембург","卢森堡"],
        ["macedonia"]=["North Macedonia","Nordmazedonien","Macedonia Północna","Северная Македония","北马其顿"],
        ["montenegro"]=["Montenegro","Montenegro","Czarnogóra","Черногория","黑山"],
        ["netherlands"]=["Netherlands","Niederlande","Holandia","Нидерланды","荷兰"],
        ["norway"]=["Norway","Norwegen","Norwegia","Норвегия","挪威"],
        ["poland"]=["Poland","Polen","Polska","Польша","波兰"],
        ["portugal"]=["Portugal","Portugal","Portugalia","Португалия","葡萄牙"],
        ["romania"]=["Romania","Rumänien","Rumunia","Румыния","罗马尼亚"],
        ["russia"]=["Russia","Russland","Rosja","Россия","俄罗斯"],
        ["serbia"]=["Serbia","Serbien","Serbia","Сербия","塞尔维亚"],
        ["slovakia"]=["Slovakia","Slowakei","Słowacja","Словакия","斯洛伐克"],
        ["slovenia"]=["Slovenia","Slowenien","Słowenia","Словения","斯洛文尼亚"],
        ["spain"]=["Spain","Spanien","Hiszpania","Испания","西班牙"],
        ["sweden"]=["Sweden","Schweden","Szwecja","Швеция","瑞典"],
        ["switzerland"]=["Switzerland","Schweiz","Szwajcaria","Швейцария","瑞士"],
        ["turkey"]=["Türkiye","Türkei","Turcja","Турция","土耳其"],
        ["uk"]=["United Kingdom","Vereinigtes Königreich","Wielka Brytania","Великобритания","英国"],
    };
    public static string Name(string token)
    {
        if(token.Length==0)return "";
        if(Names.TryGetValue(token,out var names))return names[Math.Max(0,Array.FindIndex(L.Languages,l=>l.Code==L.Code))];
        return L.Culture.TextInfo.ToTitleCase(token.Replace('_',' '));
    }
}
