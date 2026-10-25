using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace UserIdentityDesktop.Views;

/// <summary>Shared icon-and-label presentation, preserving original content, bindings and accessible names.</summary>
public sealed class DesktopIconography : AvaloniaObject
{
    public static readonly AttachedProperty<bool> EnabledProperty = AvaloniaProperty.RegisterAttached<DesktopIconography, Control, bool>("Enabled");
    private static readonly AttachedProperty<PathIcon?> GeneratedIconProperty = AvaloniaProperty.RegisterAttached<DesktopIconography, Control, PathIcon?>("GeneratedIcon");
    private static readonly IDataTemplate Labels = new FuncDataTemplate<string>((text, _) => Label(text ?? ""));
    public static bool GetEnabled(Control control) => control.GetValue(EnabledProperty);
    public static void SetEnabled(Control control, bool value) => control.SetValue(EnabledProperty, value);
    static DesktopIconography()
    {
        EnabledProperty.Changed.AddClassHandler<Control>((control, _) => Update(control));
        ContentControl.ContentProperty.Changed.AddClassHandler<Button>((button, _) => Update(button));
        HeaderedContentControl.HeaderProperty.Changed.AddClassHandler<TabItem>((tab, _) => Update(tab));
        HeaderedSelectingItemsControl.HeaderProperty.Changed.AddClassHandler<MenuItem>((menu, _) => Update(menu));
    }
    private static void Update(Control control)
    {
        if (!GetEnabled(control)) return;
        if (control is Button button)
        {
            if(button.Parent is StackPanel group && !group.IsSet(StackPanel.SpacingProperty) && group.Children.Count>1 && group.Children.All(x=>x is Button)) group.SetCurrentValue(StackPanel.SpacingProperty,8d);
            if (button.Content is string text && IsLabel(text) && (button.ContentTemplate == null || ReferenceEquals(button.ContentTemplate, Labels)))
                button.SetCurrentValue(ContentControl.ContentTemplateProperty, Labels);
            else if (ReferenceEquals(button.ContentTemplate, Labels)) button.SetCurrentValue(ContentControl.ContentTemplateProperty, null);
        }
        else if (control is MenuItem menu)
        {
            var generated=menu.GetValue(GeneratedIconProperty);
            if (menu.Icon != null && !ReferenceEquals(menu.Icon,generated)) return;
            var icon=menu.Header is string text && IsLabel(text) ? Icon(text) : null;
            menu.SetCurrentValue(MenuItem.IconProperty,icon);menu.SetValue(GeneratedIconProperty,icon);
        }
        else if (control is TabItem tab)
        {
            if(tab.Header is string text && IsLabel(text) && (tab.HeaderTemplate==null || ReferenceEquals(tab.HeaderTemplate,Labels)))
                tab.SetCurrentValue(TabItem.HeaderTemplateProperty,Labels);
            else if(ReferenceEquals(tab.HeaderTemplate,Labels)) tab.SetCurrentValue(TabItem.HeaderTemplateProperty,null);
        }
    }
    private static bool IsLabel(string text) => text.Any(char.IsLetter) || text.EndsWith('×');
    internal static Control Label(string text)
    {
        var row=new StackPanel { Orientation=Orientation.Horizontal,Spacing=7,VerticalAlignment=VerticalAlignment.Center };
        row.Children.Add(Icon(text));row.Children.Add(new TextBlock { Text=text,VerticalAlignment=VerticalAlignment.Center });return row;
    }
    internal static RenderTargetBitmap NativeIcon(string text, Window owner)
    {
        var bitmap=new RenderTargetBitmap(new PixelSize(20,20),new Vector(96,96));
        if(owner.TryFindResource("Glyph."+Glyph(text),out var data) && data is Geometry geometry)
        {
            using var drawing=bitmap.CreateDrawingContext();
            using var transform=drawing.PushTransform(Matrix.CreateScale(20d/24,20d/24));
            drawing.DrawGeometry(owner.ActualThemeVariant==Avalonia.Styling.ThemeVariant.Dark?Brushes.White:Brushes.Black,null,geometry);
        }
        return bitmap;
    }
    internal static PathIcon Icon(string text)
    {
        var icon=new PathIcon { Width=17,Height=17,VerticalAlignment=VerticalAlignment.Center };
        icon.Bind(PathIcon.DataProperty,icon.GetResourceObservable("Glyph."+Glyph(text)));
        Avalonia.Automation.AutomationProperties.SetAccessibilityView(icon,Avalonia.Automation.AccessibilityView.Raw);
        return icon;
    }
    internal static string Glyph(string text)
    {
        var value=text.Trim().ToLowerInvariant();
        bool Has(params string[] terms)=>terms.Any(value.Contains);
        if(Has("zoom in"))return "ZoomIn";if(Has("zoom out"))return "ZoomOut";
        if(Has("actual size","fit"))return "Fit";if(Has("rotate left"))return "RotateLeft";if(Has("rotate right"))return "RotateRight";
        if(Has("fullscreen","full screen"))return "Fullscreen";
        if(Has("download all","archive"))return "Archive";if(Has("download"))return "Download";
        if(Has("pause"))return "Pause";if(value=="stop" || Has("stop control","revoke"))return "Stop";if(Has("play"))return "Play";
        if(Has("emoji","smile","faces","gestures","symbols"))return "Emoji";
        if(Has("volume","speaker","audio","music"))return "Audio";
        if(Has("camera","video"))return "Camera";if(Has("photo","image"))return "Image";
        if(Has("sign out","logout","exit"))return "SignOut";
        if(Has("reject","decline","deny","cancel","close","withdraw"))return "Close";
        if(Has("delete","remove"))return "Delete";if(Has("copy"))return "Copy";if(Has("reply"))return "Reply";
        if(Has("approve","accept","confirm","acknowledge","done","allow"))return "Check";
        if(Has("refresh","retry","reconnect","reset"))return "Refresh";
        if(Has("previous","back","earlier","return"))return "ChevronLeft";if(Has("next","more"))return "ChevronRight";
        if(Has("search","review","preview"))return "Search";
        if(Has("send","publish","submit","email"))return "Send";
        if(Has("pin","unlock","security","password","sign in","login"))return "Lock";
        if(Has("save","update","apply"))return "Save";if(Has("edit","compose"))return "Edit";
        if(Has("settings","devices","configure","preferences","motion","brightness","appearance"))return "Settings";
        if(Has("invite","attendee","member","people","colleague","participant"))return "Users";
        if(Has("profile","availability","busy","disturb","online","offline","away"))return "User";
        if(Has("organization","business unit","position","policy","workplace"))return "Organization";
        if(Has("calendar","schedule","vacation","today","meeting"))return "Calendar";
        if(Has("chat","conversation","message"))return "Chat";
        if(Has("call","answer","join","microphone"))return "Phone";
        if(Has("notification","announcement"))return "Bell";
        if(Has("time","hour","speed") || value.EndsWith('×'))return "Clock";
        if(Has("file","attachment","folder"))return "File";
        if(Has("share","sharing","screen","control","gallery"))return "Remote";
        if(Has("add","new","create","select","choose"))return "Plus";
        return "Action";
    }
}
