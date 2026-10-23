using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using UserIdentityDesktop.ViewModels;

namespace UserIdentityDesktop.Views;

internal static class AttachmentIcons
{
    public static PathIcon Create(string name, double size = 20)
    {
        var icon = new PathIcon { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        icon.Bind(PathIcon.DataProperty, icon.GetResourceObservable("Glyph." + name));
        return icon;
    }
    public static PathIcon ForFile(ChatAttachmentItemViewModel item, double size = 48) => Create(item.IsVideo ? "Play" : item.IsAudio ? "Audio" : item.IsImage ? "Image" : "File", size);
    public static StackPanel Action(string icon, string label)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        row.Children.Add(Create(icon, 18)); row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }); return row;
    }
}
