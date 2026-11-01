using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using GridPanel=System.Windows.Controls.Grid;

namespace Werkstatt;
public static class Ui
{
    /// <summary>The app logo (a truck front whose grille is a W) on a 64-unit grid, as in app.ico and logo.svg.
    /// The small variant drops tyres and headlights and thickens the W so it stays legible at 16–24 px.</summary>
    public static DrawingImage Logo(bool small)
    {
        var accent=Brush("#F2A04B");var dark=Brush("#1A1208");var group=new DrawingGroup();
        void Box(double x0,double y0,double x1,double y1,double r,Brush fill)=>group.Children.Add(new GeometryDrawing(fill,null,new RectangleGeometry(new Rect(x0,y0,x1-x0,y1-y0),r,r)));
        Box(1,1,63,63,14,accent);Box(12,9,52,31,5,dark);Box(10,27,54,50,3.5,dark);
        if(!small){Box(13,48,21,55,2,dark);Box(43,48,51,55,2,dark);}
        group.Children.Add(new GeometryDrawing(accent,null,Geometry.Parse("M17.5,13 L46.5,13 L48.5,25 L15.5,25 Z")));
        var pen=new Pen(accent,small?6:4.4){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round,LineJoin=PenLineJoin.Round};
        group.Children.Add(new GeometryDrawing(null,pen,Geometry.Parse("M19.5,31.5 L25.5,44 L32,35.5 L38.5,44 L44.5,31.5")));
        if(!small){Box(12.5,41,17.5,45,1.2,accent);Box(46.5,41,51.5,45,1.2,accent);}
        group.Children.Add(new GeometryDrawing(Brushes.Transparent,null,new RectangleGeometry(new Rect(0,0,64,64))));
        group.Freeze();var image=new DrawingImage(group);image.Freeze();return image;
    }
    public static SolidColorBrush Brush(string color){var b=new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));b.Freeze();return b;}
    public static readonly Brush Accent=Brush("#F2A04B"),AccentSoft=Brush("#2B2219"),AccentText=Brush("#1A1208"),Bg=Brush("#0F1113"),Chrome=Brush("#131619"),Surface=Brush("#171A1E"),Raised=Brush("#1F2327"),
        Line=Brush("#262B30"),LineStrong=Brush("#3A4046"),Ink=Brush("#ECEAE6"),Body=Brush("#C4C9CE"),Muted=Brush("#959DA4"),Subtle=Brush("#6E777E"),Danger=Brush("#FF9C87"),Ok=Brush("#8CC79A"),Passed=Brush("#4A3520"),Scrim=Brush("#D90F1113"),MapGround=Brush("#111315");
    static readonly FontFamily Latin=new("Segoe UI, Microsoft YaHei UI"),Chinese=new("Microsoft YaHei UI, Segoe UI");
    /// <summary>Chinese leads with YaHei, so Han characters and the curly quotes around them come from one font.</summary>
    public static FontFamily Font=>L.Code=="zh"?Chinese:Latin;
    public const string IconBackup="M2.8 9 A6.2 6.2 0 1 0 4.6 4.6 M2.4 1.8 L2.8 5.2 L6.2 4.8 M9 5.4 L9 9 L11.6 10.6",
        IconHelp="M9 16.4 A7.4 7.4 0 1 0 9 1.6 A7.4 7.4 0 1 0 9 16.4 M6.8 7 C6.8 5.6 7.8 4.8 9 4.8 C10.3 4.8 11.2 5.7 11.2 6.8 C11.2 8.6 9 8.7 9 10.6 M9 13 L9 13.2",
        IconUndo="M7 3 L3 7 L7 11 M3 7 L11 7 C14 7 15.5 9 15.5 11.5 C15.5 14 14 16 11 16 L8 16",
        IconRedo="M11 3 L15 7 L11 11 M15 7 L7 7 C4 7 2.5 9 2.5 11.5 C2.5 14 4 16 7 16 L10 16",
        IconPlus="M9 3 L9 15 M3 9 L15 9",IconMinus="M3 9 L15 9",IconFit="M2.5 6.5 L2.5 2.5 L6.5 2.5 M11.5 2.5 L15.5 2.5 L15.5 6.5 M15.5 11.5 L15.5 15.5 L11.5 15.5 M6.5 15.5 L2.5 15.5 L2.5 11.5",
        IconTarget="M9 14.5 A5.5 5.5 0 1 0 9 3.5 A5.5 5.5 0 1 0 9 14.5 M9 1 L9 4 M9 14 L9 17 M1 9 L4 9 M14 9 L17 9 M9 10.4 A1.4 1.4 0 1 0 9 7.6 A1.4 1.4 0 1 0 9 10.4",
        IconCheck="M4 9.5 L7.5 13 L14 5.5";
    static Style StyleOf(string key)=>(Style)Application.Current.FindResource(key);
    public static T Id<T>(T element,string id) where T:DependencyObject{AutomationProperties.SetAutomationId(element,id);return element;}
    public static TextBlock Text(string text,double size=13,Brush? color=null,FontWeight? weight=null)=>new(){Text=text,FontSize=L.Code=="zh"?Math.Max(size,12):size,Foreground=color??Ink,FontWeight=weight??FontWeights.Normal,TextWrapping=TextWrapping.Wrap};
    public static TextBlock Caption(string text){var t=Text(text,12,Muted);t.LineHeight=17;return t;}
    public static TextBlock Label(string text){var t=Text(text,12,Muted,FontWeights.SemiBold);t.LineHeight=16;return t;}
    public static TextBlock Section(string text){var t=Text(text,14,null,FontWeights.SemiBold);t.LineHeight=20;return t;}
    public static TextBlock Title(string text,double size=18){var t=Text(text,size,null,FontWeights.SemiBold);t.LineHeight=size+6;return t;}
    /// <summary>One line, trimmed with an ellipsis; the full text goes to the tooltip.</summary>
    public static TextBlock Trim(TextBlock t){t.TextWrapping=TextWrapping.NoWrap;t.TextTrimming=TextTrimming.CharacterEllipsis;t.ToolTip=t.Text;return t;}
    /// <summary>A sentence whose figures stand out: digits in Ink, words in Muted.</summary>
    public static TextBlock Figures(string text,double size=13)
    {
        var t=Text("",size,Muted);foreach(var part in Regex.Split(text,@"(\d[\d\s.,:%]*)"))if(part.Length>0)t.Inlines.Add(new Run(part){Foreground=char.IsDigit(part[0])?Ink:Muted});
        Typography.SetNumeralAlignment(t,FontNumeralAlignment.Tabular);return t;
    }
    /// <summary>First visual descendant of type <typeparamref name="T"/>, e.g. the ScrollViewer inside a list.</summary>
    public static T? Find<T>(DependencyObject root) where T:DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T match)return match;if(Find<T>(child) is {} deeper)return deeper;}
        return null;
    }
    public static T Tabular<T>(T element) where T:DependencyObject{Typography.SetNumeralAlignment(element,FontNumeralAlignment.Tabular);return element;}
    public static StackPanel Row(params UIElement[] children){var p=new StackPanel{Orientation=Orientation.Horizontal};foreach(var c in children)p.Children.Add(c);return p;}
    public static Border Rule(double above=0,double below=0)=>new(){Height=1,Background=Line,Margin=new Thickness(0,above,0,below),SnapsToDevicePixels=true};
    public static Ellipse Dot(Brush? fill,double size=8)=>fill==null?new(){Width=size,Height=size,Stroke=Subtle,StrokeThickness=1.5,VerticalAlignment=VerticalAlignment.Center}:new(){Width=size,Height=size,Fill=fill,VerticalAlignment=VerticalAlignment.Center};
    public static System.Windows.Shapes.Path Icon(string geometry,double size=18,Brush? color=null,double stroke=1.6)=>new(){Data=Geometry.Parse(geometry),Stroke=color??Ink,StrokeThickness=stroke,Width=size,Height=size,Stretch=Stretch.Uniform,StrokeLineJoin=PenLineJoin.Round,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,VerticalAlignment=VerticalAlignment.Center};
    /// <summary>Grid from compact track lists such as "292*,36,Auto" (columns) and "Auto,*" (rows).</summary>
    public static GridPanel Grid(string columns="*",string rows="*")
    {
        var g=new GridPanel();var converter=new GridLengthConverter();
        foreach(var c in columns.Split(','))g.ColumnDefinitions.Add(new(){Width=(GridLength)converter.ConvertFromInvariantString(c)!});
        foreach(var r in rows.Split(','))g.RowDefinitions.Add(new(){Height=(GridLength)converter.ConvertFromInvariantString(r)!});
        return g;
    }
    public static T At<T>(GridPanel parent,T child,int column=0,int row=0,int columnSpan=1) where T:UIElement{GridPanel.SetColumn(child,column);GridPanel.SetRow(child,row);GridPanel.SetColumnSpan(child,columnSpan);parent.Children.Add(child);return child;}
    public static Button Button(string label,Action click,bool primary=false){var b=new Button{Content=label};if(primary)b.Style=StyleOf("PrimaryButton");b.Click+=(s,e)=>click();return b;}
    public static Button Ghost(string label,Action click){var b=Button(label,click);b.Style=StyleOf("GhostButton");return b;}
    public static Button Link(string label,Action click,bool muted=false){var b=Button(label,click);b.Style=StyleOf("LinkButton");if(muted)b.Foreground=Muted;return b;}
    public static Button Chip(string label,Action click){var b=Button(label,click);b.Style=StyleOf("Chip");return b;}
    public static ToggleButton ToggleChip(string label)=>new(){Content=label,Style=StyleOf("ToggleChip")};
    public static Button IconButton(string geometry,string label,Action click,double size=32)
    {
        var button=Button("",click);button.Style=StyleOf("GhostButton");
        var icon=Icon(geometry,16,null,1.6);icon.SetBinding(Shape.StrokeProperty,new Binding("Foreground"){Source=button});button.Content=icon;button.Width=button.Height=size;button.Padding=new(0);button.ToolTip=label;
        AutomationProperties.SetName(button,label);return button;
    }
    /// <summary>A row in a list or popup: label on the left, optional muted detail on the right.</summary>
    public static Button RowButton(string label,string? detail,Action click,bool selected=false)
    {
        var b=Button("",click);b.Style=StyleOf("RowButton");b.Padding=new(10,0,10,0);var row=new DockPanel();
        if(detail!=null){var d=Text(detail,12,Muted);d.TextWrapping=TextWrapping.NoWrap;d.Margin=new(12,0,0,0);d.VerticalAlignment=VerticalAlignment.Center;DockPanel.SetDock(d,Dock.Right);row.Children.Add(d);}
        var name=Trim(Text(label,13,selected?Accent:Ink,selected?FontWeights.SemiBold:FontWeights.Normal));name.VerticalAlignment=VerticalAlignment.Center;row.Children.Add(name);
        b.Content=row;if(selected)b.Background=AccentSoft;b.IsEnabledChanged+=(s,e)=>name.Foreground=b.IsEnabled?selected?Accent:Ink:Subtle;AutomationProperties.SetName(b,label);return b;
    }
    public static TextBox Input(string value,string name,string? placeholder=null)
    {var t=new TextBox{Text=value,Tag=placeholder};AutomationProperties.SetName(t,name);return t;}
    /// <summary>Equal options in one outlined strip; exactly one is selected. Callers listen to Checked on the options.</summary>
    public static (Border Strip,List<RadioButton> Options) Segmented(IReadOnlyList<string> labels,int selected)
    {
        var grid=new UniformGrid{Rows=1};var options=new List<RadioButton>();string group=Guid.NewGuid().ToString("N");
        for(int i=0;i<labels.Count;i++)
        {
            var option=new RadioButton{Content=labels[i],GroupName=group,Style=StyleOf("Segment"),IsChecked=i==selected,BorderThickness=new(i==0?0:1,0,0,0),Tag=new CornerRadius(i==0?5:0,i==labels.Count-1?5:0,i==labels.Count-1?5:0,i==0?5:0)};
            ToolTipService.SetShowOnDisabled(option,true);options.Add(option);grid.Children.Add(option);
        }
        return (new Border{Child=grid,BorderBrush=LineStrong,BorderThickness=new(1),CornerRadius=new(6),Height=30,SnapsToDevicePixels=true},options);
    }
    /// <summary>Read-only table. Trimmed cells keep their text in a tooltip: the cell's own value, or <paramref name="tip"/> for a whole-row text.</summary>
    public static DataGrid Table(string name,string? tip,params (string Title,string Field,double Width,bool Numeric)[] columns)
    {
        var g=new DataGrid{Name=name};
        foreach(var c in columns)
        {
            var element=new Style(typeof(TextBlock));element.Setters.Add(new Setter(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis));element.Setters.Add(new Setter(TextBlock.TextWrappingProperty,TextWrapping.NoWrap));
            element.Setters.Add(new Setter(FrameworkElement.ToolTipProperty,new Binding(tip??c.Field)));
            var column=new DataGridTextColumn{Header=c.Title,Binding=new Binding(c.Field),Width=c.Width<0?new DataGridLength(-c.Width,DataGridLengthUnitType.Star):c.Width==0?DataGridLength.Auto:new DataGridLength(c.Width),IsReadOnly=true,ElementStyle=element};
            if(c.Numeric)
            {
                element.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Right));element.Setters.Add(new Setter(Typography.NumeralAlignmentProperty,FontNumeralAlignment.Tabular));
                var header=new Style(typeof(DataGridColumnHeader),(Style)Application.Current.FindResource(typeof(DataGridColumnHeader)));header.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,HorizontalAlignment.Right));column.HeaderStyle=header;
            }
            g.Columns.Add(column);
        }
        return g;
    }
    /// <summary>A popup anchored under <paramref name="target"/>, opened and closed by it, rebuilt each time it opens.</summary>
    public static Popup Flyout(ToggleButton target,Func<FrameworkElement> content,double width,bool alignRight=false)
    {
        var border=new Border{Width=width,Background=Surface,BorderBrush=LineStrong,BorderThickness=new(1),CornerRadius=new(10),Effect=new DropShadowEffect{BlurRadius=24,ShadowDepth=8,Direction=270,Opacity=.4,Color=Colors.Black}};
        var popup=new Popup{Child=new Decorator{Child=border,Margin=new(24,0,24,32)},PlacementTarget=target,Placement=PlacementMode.Bottom,AllowsTransparency=true,StaysOpen=false,VerticalOffset=6,HorizontalOffset=-24};
        popup.Opened+=(s,e)=>{target.IsHitTestVisible=false;if(alignRight)popup.HorizontalOffset=target.ActualWidth-width-24;};
        popup.Closed+=(s,e)=>{target.IsHitTestVisible=true;target.IsChecked=false;};
        target.Checked+=(s,e)=>{border.Child=content();popup.IsOpen=true;};target.Unchecked+=(s,e)=>popup.IsOpen=false;
        return popup;
    }
    /// <summary>Keeps the start and end of a long path, e.g. "C:\Program Files…\Euro Truck Simulator 2".</summary>
    public static string MiddleTrim(string text,int max)=>text.Length<=max?text:text[..(max/2-1)]+"…"+text[^(max-max/2)..];
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);
    public static void DarkTitleBar(Window window)
    {
        try{var handle=new WindowInteropHelper(window).EnsureHandle();int dark=1;DwmSetWindowAttribute(handle,20,ref dark,sizeof(int));int caption=0x00131619;DwmSetWindowAttribute(handle,35,ref caption,sizeof(int));}
        catch(Exception e) when(e is DllNotFoundException or EntryPointNotFoundException){}
    }
    /// <summary>Themed replacement for MessageBox. Returns true for the primary choice.</summary>
    public static bool Ask(Window? owner,string title,string message,string ok,string? cancel=null)
    {var (dialog,result)=CreateAsk(owner,title,message,ok,cancel);dialog.ShowDialog();return result();}
    internal static (Window Dialog,Func<bool> Result) CreateAsk(Window? owner,string title,string message,string ok,string? cancel=null)
    {
        var dialog=new Window{Title=title,Width=460,Language=System.Windows.Markup.XmlLanguage.GetLanguage(L.Culture.IetfLanguageTag),SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=owner!=null?WindowStartupLocation.CenterOwner:WindowStartupLocation.CenterScreen,ShowInTaskbar=false,Background=Surface,Foreground=Ink,FontFamily=Font,FontSize=13};
        if(owner!=null&&owner.IsLoaded)dialog.Owner=owner;dialog.SourceInitialized+=(s,e)=>DarkTitleBar(dialog);
        bool result=false;var panel=new StackPanel{Margin=new(24)};
        var head=Text(title,16,null,FontWeights.SemiBold);head.LineHeight=22;panel.Children.Add(head);
        var text=Text(message,13,Body);text.LineHeight=20;text.Margin=new(0,8,0,0);panel.Children.Add(text);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new(0,24,0,0)};
        if(cancel!=null){var no=Button(cancel,()=>dialog.Close());no.IsCancel=true;no.Margin=new(0,0,8,0);buttons.Children.Add(no);}
        var yes=Button(ok,()=>{result=true;dialog.Close();},true);yes.IsDefault=true;if(cancel==null)yes.IsCancel=true;buttons.Children.Add(yes);panel.Children.Add(buttons);
        dialog.Content=panel;dialog.Loaded+=(s,e)=>yes.Focus();return (dialog,()=>result);
    }
}
/// <summary>Draws the scroll thumb at least 24 px long. Track shrinks it to a sliver on long lists and clips a MinHeight; here the
/// extra length is spread by the scroll position, so the thumb never leaves the track.</summary>
public sealed class ThumbLength : Decorator
{
    ScrollBar? bar;
    ScrollBar? Bar=>bar??(TemplatedParent as Thumb)?.TemplatedParent as ScrollBar;
    public ThumbLength()
    {
        Loaded+=(s,e)=>{bar=Bar;if(bar!=null)bar.ValueChanged+=Moved;};Unloaded+=(s,e)=>{if(bar!=null)bar.ValueChanged-=Moved;bar=null;};
    }
    void Moved(object sender,RoutedPropertyChangedEventArgs<double> e)=>InvalidateArrange();
    protected override Size MeasureOverride(Size constraint){Child?.Measure(new Size());return new Size();}
    protected override Size ArrangeOverride(Size size)
    {
        var scroll=Bar;bool vertical=scroll?.Orientation!=Orientation.Horizontal;double length=vertical?size.Height:size.Width,full=Math.Max(length,24);
        double shift=scroll!=null&&scroll.Maximum>scroll.Minimum?-(full-length)*(scroll.Value-scroll.Minimum)/(scroll.Maximum-scroll.Minimum):0;
        Child?.Arrange(vertical?new Rect(0,shift,size.Width,full):new Rect(shift,0,full,size.Height));return size;
    }
}
