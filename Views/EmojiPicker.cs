using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace UserIdentityDesktop.Views;

/// <summary>Offline Unicode picker. Inserts into the draft without sending it.</summary>
public sealed class EmojiPicker : UserControl
{
    private sealed record Emoji(string Value,string Name,string Category);
    private static readonly Emoji[] entries = CreateEntries();
    private readonly WrapPanel choices=new() { Orientation=Orientation.Horizontal,ItemSpacing=4,LineSpacing=4 };
    private readonly TextBox search=new() { PlaceholderText="Search emojis",Name="EmojiSearch" };
    private readonly TextBlock empty=new() { Text="No matching emojis",IsVisible=false };
    private string category="All";
    public event Action<string>? Selected;
    public EmojiPicker()
    {
        Width=340;
        var root=new StackPanel { Spacing=12 };
        root.Children.Add(new TextBlock {Text="Emojis",FontSize=17,FontWeight=FontWeight.SemiBold});root.Children.Add(search);
        var categories=new WrapPanel { ItemSpacing=6,LineSpacing=6 };
        foreach(var name in new[]{"All","Faces","Gestures","Symbols"}) {var button=new Button {Content=name,Padding=new Thickness(8,5)};button.Click+=(_,_)=>{category=name;Refresh();};categories.Children.Add(button);}
        root.Children.Add(categories);root.Children.Add(empty);root.Children.Add(new ScrollViewer {Content=choices,MaxHeight=240,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled});Content=root;
        Avalonia.Automation.AutomationProperties.SetName(search,"Search emojis");search.TextChanged+=(_,_)=>Refresh();Refresh();
    }
    private void Refresh()
    {
        choices.Children.Clear();var query=search.Text?.Trim()??"";
        foreach(var entry in entries.Where(x=>(category=="All"||x.Category==category)&&(x.Name.Contains(query,StringComparison.OrdinalIgnoreCase)||x.Value.Contains(query,StringComparison.Ordinal))))
        {
            var button=new Button { Content=entry.Value,FontFamily=new FontFamily("Noto Color Emoji, Segoe UI Emoji, Apple Color Emoji"),FontSize=24,Width=42,Height=42,Padding=new Thickness(4),Classes={"quiet"} };
            ToolTip.SetTip(button,entry.Name);Avalonia.Automation.AutomationProperties.SetName(button,entry.Name);button.Click+=(_,_)=>Selected?.Invoke(entry.Value);choices.Children.Add(button);
        }
        empty.IsVisible=choices.Children.Count==0;
    }
    internal static void Insert(TextBox input,string emoji,int first,int last)
    {
        var text=input.Text??"";var start=Math.Clamp(Math.Min(first,last),0,text.Length);var end=Math.Clamp(Math.Max(first,last),start,text.Length);
        // A selection must never split a surrogate pair.
        if(start>0 && start<text.Length && char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start-1]))start--;
        if(end>0 && end<text.Length && char.IsLowSurrogate(text[end]) && char.IsHighSurrogate(text[end-1]))end++;
        input.SetCurrentValue(TextBox.TextProperty,text[..start]+emoji+text[end..]);input.CaretIndex=start+emoji.Length;input.SelectionStart=input.SelectionEnd=input.CaretIndex;
    }
    private static Emoji[] CreateEntries()
    {
        var result=new List<Emoji>();
        void Add(string group,string items){foreach(var item in items.Split('|')){var parts=item.Split('=',2);result.Add(new Emoji(parts[0],parts[1],group));}}
        Add("Faces","😀=Grinning happy|😃=Smiling eyes|😄=Happy smile|😁=Beaming grin|😆=Laughing|😂=Tears of joy|🤣=Rolling laughing|😊=Blushing smile|🙂=Slight smile|😉=Wink|😍=Heart eyes love|🥰=Smiling hearts|😘=Kiss|😎=Cool sunglasses|🤔=Thinking|😅=Nervous smile sweat|😢=Sad crying|😭=Loud crying|😮=Surprised|😴=Sleeping tired|🥳=Party celebration|😇=Angel|🙃=Upside down|🤩=Star eyes");
        Add("Gestures","👍=Thumbs up yes|👎=Thumbs down no|👏=Clapping applause|🙌=Raised hands celebration|🙏=Thank you please|👋=Wave hello goodbye|🤝=Handshake agreement|💪=Strong muscle|👌=OK hand|✌️=Victory peace|🤞=Fingers crossed|🫶=Heart hands|👀=Eyes look|✅=Check done|❌=Cross no|💯=Hundred perfect");
        Add("Symbols","❤️=Red heart love|🧡=Orange heart|💛=Yellow heart|💚=Green heart|💙=Blue heart|💜=Purple heart|🔥=Fire|✨=Sparkles|⭐=Star|🎉=Party popper|🎂=Birthday cake|🎁=Gift present|🎵=Music note|📎=Paperclip attachment|📅=Calendar|💡=Idea light bulb|☕=Coffee|🌞=Sun|🌈=Rainbow|🚀=Rocket|🌻=Sunflower|🏆=Trophy award|💬=Speech chat|📸=Photo camera");
        return result.ToArray();
    }
}
