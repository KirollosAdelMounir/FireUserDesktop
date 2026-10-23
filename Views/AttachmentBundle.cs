using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using UserIdentityDesktop.ViewModels;

namespace UserIdentityDesktop.Views;

/// <summary>Native equivalent of the web attachment stack, with bounded visible cards.</summary>
public sealed class AttachmentBundle : UserControl
{
    public static readonly StyledProperty<ChatMessageItemViewModel?> MessageProperty = AvaloniaProperty.Register<AttachmentBundle, ChatMessageItemViewModel?>(nameof(Message));
    public ChatMessageItemViewModel? Message { get => GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    private readonly List<Bitmap> images = new();
    private int generation;
    private System.Threading.CancellationTokenSource loading = new();
    private AttachmentGalleryWindow? gallery;
    public AttachmentBundle()
    {
        MaxWidth = 320;
        PropertyChanged += (_, e) => { if (e.Property == MessageProperty) Rebuild(); };
        AttachedToVisualTree += (_, _) => Rebuild();
        DetachedFromVisualTree += (_, _) => Clear();
    }
    private void Clear() { loading.Cancel(); loading.Dispose(); loading = new(); gallery?.Close(); gallery = null; generation++; Content = null; foreach (var image in images) image.Dispose(); images.Clear(); }
    private void Rebuild()
    {
        Clear(); if (Message is not { HasAttachments: true } message) return;
        if (message.Attachments.Count == 1 && message.Attachments[0].IsAudio)
        { Content = new InlineAudioPlayer { Attachment = message.Attachments[0], IsSent = message.IsMine }; return; }
        var count = message.Attachments.Count; var cards = new Panel { Width = count > 1 ? 230 : 280, Height = count > 1 ? 190 : 180 };
        var visible = message.Attachments.Take(4).ToArray();
        for (var i = visible.Length - 1; i >= 0; i--)
        {
            var item = visible[i]; var label = new TextBlock { Text = item.FileName, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 180, HorizontalAlignment = HorizontalAlignment.Center };
            var symbol = AttachmentIcons.ForFile(item);
            symbol.Bind(PathIcon.ForegroundProperty, this.GetResourceObservable("ForgePrimary"));
            var inner = new Grid(); var fallback = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center }; fallback.Children.Add(symbol); fallback.Children.Add(label); inner.Children.Add(fallback);
            var thumbnail = new Image { Stretch = Stretch.UniformToFill }; inner.Children.Add(thumbnail);
            var card = new Border { Child = new Border { Child = inner, CornerRadius = new CornerRadius(20), ClipToBounds = true }, Width = count > 1 ? 184 : 234, Height = count > 1 ? 144 : 164, CornerRadius = new CornerRadius(22), ClipToBounds = false, BorderThickness = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = RelativePoint.Center };
            card.Bind(Border.BackgroundProperty, this.GetResourceObservable("ForgeSurfaceCard")); card.Bind(Border.BorderBrushProperty, this.GetResourceObservable("ForgeSurface"));
            card.BoxShadow = BoxShadows.Parse("0 8 24 0 #60000000");
            if (count > 1) { var angles = new[] { -5d, 7, -3, 3 }; var offsets = new[] { (-6d,12d), (18d,-2d), (-2d,-12d), (9d,-18d) };
                card.RenderTransform = new TransformGroup { Children = { new RotateTransform(angles[i]), new TranslateTransform(offsets[i].Item1, offsets[i].Item2) } }; }
            cards.Children.Add(card);
            if (item.IsImage && this.IsAttachedToVisualTree()) _ = LoadImageAsync(item, thumbnail, generation);
        }
        if (count > 1)
        {
            var badge = new Border { Background = new SolidColorBrush(Color.Parse("#CC0A0C10")), CornerRadius = new CornerRadius(16), Padding = new Thickness(10, 5), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 18, 20), Child = new TextBlock { Text = count.ToString(), Foreground = Brushes.White, FontWeight = FontWeight.Bold } }; cards.Children.Add(badge);
        }
        var layout = new StackPanel { Spacing = 4 }; if (count > 1) layout.Children.Add(new TextBlock { Text = message.AttachmentSummary, FontWeight = FontWeight.SemiBold, FontSize = 13 }); layout.Children.Add(cards);
        var open = new Button { Content = layout, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), Classes = { "quiet" } };
        Avalonia.Automation.AutomationProperties.SetName(open, "View " + message.AttachmentSummary);
        open.Click += (_, _) => { gallery = new AttachmentGalleryWindow(message.Attachments.ToArray(), downloadAll: message.DownloadAllCommand); if (TopLevel.GetTopLevel(this) is Window owner) gallery.ShowDialog(owner); };
        Content = open;
    }
    private async Task LoadImageAsync(ChatAttachmentItemViewModel item, Image target, int version)
    {
        string? path = null;
        try
        {
            path = await item.PreparePreviewAsync(loading.Token);
            if (version != generation) return;
            using var stream = File.OpenRead(path); var bitmap = Bitmap.DecodeToWidth(stream, 460);
            if (version == generation) { images.Add(bitmap); target.Source = bitmap; } else bitmap.Dispose();
        }
        catch (Exception) { /* Keep the file card visible; explicit opening displays any verification failure. */ }
        finally { if (path != null) try { File.Delete(path); } catch (IOException) { } }
    }
}
