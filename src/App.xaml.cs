global using System.IO;
using System.Windows;

namespace Werkstatt;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException+=(s,args)=>
        {
            MessageBox.Show(args.Exception.Message,"ETS2 Werkstatt",MessageBoxButton.OK,MessageBoxImage.Error);args.Handled=true;
            if(MainWindow is not {IsLoaded:true}&&!e.Args.Contains("--self-test"))Shutdown(1);
        };
        L.Load();ApplyLanguage();
        if(e.Args.Contains("--self-test"))
        {
            try{SelfTest.Run(e.Args);Shutdown(0);}catch(Exception ex){File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"self-test-error.txt"),ex.ToString());Shutdown(1);}return;
        }
        var window=new MainWindow(e.Args.FirstOrDefault(a=>!a.StartsWith("--"))); MainWindow=window;window.Show();
    }
    /// <summary>Tells WPF which language the text is in: Chinese glyph choice, screen reader voice and default formatting follow it. Chinese also gets its own font order.</summary>
    internal static void ApplyLanguage()
    {
        var language=System.Windows.Markup.XmlLanguage.GetLanguage(L.Culture.IetfLanguageTag);
        if(Current==null)return;
        Current.Resources["UiFont"]=Ui.Font;
        foreach(Window window in Current.Windows){window.Language=language;window.FontFamily=Ui.Font;}
    }
}
