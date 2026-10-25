using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using UserIdentityDesktop.ViewModels;

namespace UserIdentityDesktop.Views;

/// <summary>Opaque native attachment viewer with media-specific controls.</summary>
public sealed class AttachmentGalleryWindow : Window
{
    private readonly IReadOnlyList<ChatAttachmentItemViewModel> attachments;
    private readonly ContentControl preview = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly TextBlock title = new() { FontSize = 18, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock detail = new();
    private readonly Button previous = new() { Content = "‹", Width = 44, Height = 44 }, next = new() { Content = "›", Width = 44, Height = 44 };
    private readonly Button fullscreen = new() { Content = "Exit fullscreen", IsVisible = false, Classes = { "quiet" } };
    private readonly Button download = new() { Content = "Download", Classes = { "quiet" } };
    private readonly Button back = new() { Content = "‹ Attachments", Classes = { "quiet" } };
    private readonly Button archive;
    private readonly System.Threading.SemaphoreSlim thumbnailLoads = new(3);
    private readonly TextBlock position = new() { Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border surface = new() { CornerRadius = new CornerRadius(20), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, ClipToBounds = true };
    private readonly Grid navigation;
    private readonly List<Bitmap> thumbnails = new();
    private readonly SurfaceMotion motion = new();
    public bool IsOverview { get; private set; }
    private string? temporary;
    private Bitmap? image;
    private InlineVideoPlayer? player;
    private System.Threading.CancellationTokenSource? loading;
    private int generation;
    private readonly System.Threading.SemaphoreSlim selection = new(1, 1);
    private bool closing;
    private WindowState beforeFullscreen;
    private readonly BorderlessPresentation borderlessPresentation;
    internal bool IsPresenting=>OperatingSystem.IsLinux()?borderlessPresentation.IsActive:WindowState==WindowState.FullScreen;
    public int SelectedIndex { get; private set; }
    public AttachmentGalleryWindow(IReadOnlyList<ChatAttachmentItemViewModel> files, int initial = -1, System.Windows.Input.ICommand? downloadAll = null)
    {
        attachments = files; Width = 1000; Height = 760; MinWidth = 400; MinHeight = 400; Title = "Attachments";
        WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowDecorations = WindowDecorations.Full; ShowInTaskbar = false;
        borderlessPresentation=new(this);
        TransparencyLevelHint = [WindowTransparencyLevel.None];
        this.Bind(BackgroundProperty, this.GetResourceObservable("ForgeSurface"));
        this.Bind(ForegroundProperty, this.GetResourceObservable("ForgeTextPrimary"));
        previous.Content = AttachmentIcons.Create("ChevronLeft"); next.Content = AttachmentIcons.Create("ChevronRight");
        back.Content = AttachmentIcons.Action("ChevronLeft", "Attachments");
        download.Content = AttachmentIcons.Action("Download", "Download");
        Avalonia.Automation.AutomationProperties.SetName(download, "Download attachment");
        archive = new Button { Content = AttachmentIcons.Action("Archive", "Download All"), Command = downloadAll, Classes = { "quiet" } };
        Avalonia.Automation.AutomationProperties.SetName(archive, "Download All");
        var heading = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center }; heading.Children.Add(title); heading.Children.Add(detail);
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"), ColumnSpacing = 12, Margin = new Thickness(18, 12) };
        header.Children.Add(back); header.Children.Add(heading); Grid.SetColumn(heading, 1);
        header.Children.Add(archive); Grid.SetColumn(archive, 2); header.Children.Add(download); Grid.SetColumn(download, 3);
        header.Children.Add(fullscreen); Grid.SetColumn(fullscreen, 4);
        navigation = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto"), ColumnSpacing = 16, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(16, 8) };
        navigation.Children.Add(previous); navigation.Children.Add(position); Grid.SetColumn(position, 1); navigation.Children.Add(next); Grid.SetColumn(next, 2);
        Avalonia.Automation.AutomationProperties.SetName(previous, "Previous attachment"); Avalonia.Automation.AutomationProperties.SetName(next, "Next attachment");
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), MinWidth = 340 }; root.Children.Add(header); root.Children.Add(navigation); Grid.SetRow(navigation, 1); root.Children.Add(preview); Grid.SetRow(preview, 2);
        surface.Child = root; Content = surface;
        previous.Click += async (_, _) => await SelectAsync(SelectedIndex - 1); next.Click += async (_, _) => await SelectAsync(SelectedIndex + 1);
        back.Click += async (_, _) => await ShowOverviewAsync();
        download.Click += async (_, _) => { if (attachments.Count > 0) await attachments[SelectedIndex].DownloadCommand.ExecuteAsync(null); };
        fullscreen.Click += (_, _) => ExitFullscreen();
        KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape) { if (IsPresenting) ExitFullscreen(); else if (!IsOverview && attachments.Count > 1) await ShowOverviewAsync(); else Close(); e.Handled = true; }
            else if (!IsOverview && e.Source is not Slider && e.Key is Key.Left or Key.Right) { await SelectAsync(SelectedIndex + (e.Key == Key.Left ? -1 : 1)); e.Handled = true; }
        };
        Opened += async (_, _) =>
        {
            if (Owner is Window owner) { RequestedThemeVariant = owner.ActualThemeVariant; }
            if (generation == 0) { if (initial < 0 && files.Count > 1) await ShowOverviewAsync(); else await SelectAsync(Math.Clamp(initial, 0, Math.Max(0, files.Count - 1))); }
            _ = motion.EnterAsync(surface, SurfaceMotion.Reduced(this), SurfaceMotionKind.Overlay);
        };
        Closing += async (_, e) =>
        {
            if (closing) return; e.Cancel = true; closing = true; generation++; loading?.Cancel();
            await selection.WaitAsync();
            try { await ClearAsync(); await motion.ExitAsync(surface, SurfaceMotion.Reduced(this), SurfaceMotionKind.Overlay); }
            finally { selection.Release(); loading?.Dispose(); loading = null; } Close();
        };
        Closed += (_, _) => motion.Cancel();
    }
    private void ExitFullscreen()
    {
        if(OperatingSystem.IsLinux())borderlessPresentation.Exit();else WindowState=beforeFullscreen;
        fullscreen.IsVisible=false;
        surface.HorizontalAlignment = HorizontalAlignment.Stretch; surface.VerticalAlignment = VerticalAlignment.Stretch;
    }
    private void ToggleFullscreen()
    {
        if (IsPresenting) ExitFullscreen();
        else { beforeFullscreen = WindowState; if(OperatingSystem.IsLinux())borderlessPresentation.Enter();else WindowState=WindowState.FullScreen; fullscreen.IsVisible = true; surface.MaxWidth = surface.MaxHeight = double.PositiveInfinity; surface.HorizontalAlignment = HorizontalAlignment.Stretch; surface.VerticalAlignment = VerticalAlignment.Stretch; }
    }
    private void ApplyMode(bool overview)
    {
        IsOverview = overview; back.IsVisible = !overview && attachments.Count > 1; archive.IsVisible = overview && archive.Command != null; download.IsVisible = !overview; navigation.IsVisible = !overview && attachments.Count > 1;
        surface.MaxWidth = surface.MaxHeight = double.PositiveInfinity;
        if (overview)
        {
            surface.Bind(Border.BackgroundProperty, this.GetResourceObservable("ForgeSurface")); surface.Bind(Border.BorderBrushProperty, this.GetResourceObservable("ForgeBorder"));
            title.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("ForgeTextPrimary")); detail.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("ForgeTextMuted"));
            title.FontSize = 20; detail.IsVisible = true;
        }
        else
        {
            surface.Background = new SolidColorBrush(Color.Parse("#120D0D")); surface.BorderBrush = new SolidColorBrush(Color.Parse("#52FFB703"));
            title.Foreground = Brushes.White; title.FontSize = 14; detail.IsVisible = false;
            foreach (var button in new[] { previous, next, back, download, fullscreen }) { button.Foreground = Brushes.White; button.Background = new SolidColorBrush(Color.Parse("#14FFFFFF")); button.BorderBrush = new SolidColorBrush(Color.Parse("#40FFFFFF")); button.BorderThickness = new Thickness(1); button.CornerRadius = new CornerRadius(12); }
        }
    }
    public async Task ShowOverviewAsync()
    {
        if (closing) return;
        var version = ++generation; loading?.Cancel(); await selection.WaitAsync();
        try
        {
            if (version != generation || closing) return;
            await ClearAsync(); loading?.Dispose(); loading = new(); var token = loading.Token;
            if (IsPresenting) ExitFullscreen(); ApplyMode(true);
            Title = title.Text = Summary(attachments); detail.Text = "Attachments from this message";
            var cards = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14) };
            for (var index = 0; index < attachments.Count; index++)
            {
                var item = attachments[index]; var selected = index;
                var picture = new Grid { Height = 150, Width = 180 };
                var glyph = AttachmentIcons.ForFile(item, 56); glyph.Bind(PathIcon.ForegroundProperty, this.GetResourceObservable("ForgePrimary")); picture.Children.Add(glyph);
                var photo = new Image { Stretch = Stretch.UniformToFill }; picture.Children.Add(photo);
                var badge = new Border { Background = new SolidColorBrush(Color.Parse("#CC0A0C10")), CornerRadius = new CornerRadius(12), Padding = new Thickness(9, 5), Margin = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Child = new TextBlock { Text = item.IsImage ? "View photo" : item.IsVideo ? "Play video" : item.IsAudio ? "Play audio" : "View file", Foreground = Brushes.White, FontSize = 11 } }; picture.Children.Add(badge);
                var open = new Button { Content = new Border { Child = picture, CornerRadius = new CornerRadius(16), ClipToBounds = true }, Padding = new Thickness(0), BorderThickness = new Thickness(0), Classes = { "quiet" } };
                Avalonia.Automation.AutomationProperties.SetName(open, "View " + item.FileName); open.Click += async (_, _) => await SelectAsync(selected);
                var card = new StackPanel { Width = 180, Margin = new Thickness(8), Spacing = 6 }; card.Children.Add(open);
                card.Children.Add(new TextBlock { Text = item.FileName, FontSize = 12, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center });
                card.Children.Add(new TextBlock { Text = item.FormattedFileSize, FontSize = 11, Opacity = .7, TextAlignment = TextAlignment.Center });
                if (item.IsDocument) card.Children.Add(new Button { Content = "Download", Command = item.DownloadCommand, HorizontalAlignment = HorizontalAlignment.Center, Classes = { "quiet" } });
                cards.Children.Add(card);
                if (item.IsImage) _ = LoadGalleryThumbnail(item, photo, version, token);
            }
            preview.Content = new ScrollViewer { Content = cards, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        }
        finally { selection.Release(); }
    }
    private static string Summary(IReadOnlyList<ChatAttachmentItemViewModel> files) => $"{files.Count} " + (files.All(x => x.IsImage) ? "photos" : files.All(x => x.IsVideo) ? "videos" : files.All(x => x.IsAudio) ? "audio files" : "attachments");
    private async Task LoadGalleryThumbnail(ChatAttachmentItemViewModel item, Image target, int version, System.Threading.CancellationToken token)
    {
        string? path = null; var entered = false;
        try { await thumbnailLoads.WaitAsync(token); entered = true; if (version != generation || closing) return; path = await item.PreparePreviewAsync(token); if (version != generation || closing) return; using var input = File.OpenRead(path); var bitmap = Bitmap.DecodeToWidth(input, 360); thumbnails.Add(bitmap); target.Source = bitmap; }
        catch (Exception) { }
        finally { if (entered) thumbnailLoads.Release(); if (path != null) try { File.Delete(path); } catch (IOException) { } }
    }
    public async Task SelectAsync(int index)
    {
        if (closing || index < 0 || index >= attachments.Count) return;
        var version = ++generation; loading?.Cancel();
        await selection.WaitAsync();
        try {
        if (version != generation || closing) return;
        loading?.Dispose(); loading = new(); var cancellationToken = loading.Token;
        SelectedIndex = index;
        await ClearAsync(); if (version != generation || closing) return;
        ApplyMode(false);
        var item = attachments[index]; Title = title.Text = item.FileName; position.Text = $"{index + 1} / {attachments.Count}";
        previous.IsEnabled = index > 0; next.IsEnabled = index + 1 < attachments.Count;
        preview.Content = new TextBlock { Text = "Loading preview…", Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (IsPresenting) ExitFullscreen();
        try
        {
            if (item.IsAudio) { preview.Content = new InlineAudioPlayer { Attachment = item, IsSent = false, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16), MaxWidth = 720, IsPreview = true }; return; }
            if (!item.IsImage && !item.IsVideo) { preview.Content = new TextBlock { Text = item.FileName + "\n\nUse Download to save this file.", Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(30), VerticalAlignment = VerticalAlignment.Center }; return; }
            var path = await item.PreparePreviewAsync(cancellationToken);
            if (version != generation || closing) { File.Delete(path); return; }
            temporary = path;
            if (item.IsImage) { using var input = File.OpenRead(path); image = new Bitmap(input); var photo = new PhotoPreview(image); photo.FullscreenRequested += ToggleFullscreen; preview.Content = photo; }
            else { var video = new InlineVideoPlayer(); player = video; preview.Content = video; video.FullscreenRequested += ToggleFullscreen; await video.LoadAsync(path, cancellationToken); if (version == generation && !closing) await video.PlayAsync(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (version == generation && !closing) preview.Content = new TextBlock { Text = "Unable to preview attachment. " + error.Message, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(30) }; }
        } finally { selection.Release(); }
    }
    private async Task ClearAsync()
    {
        var old = player; player = null;
        if (old != null) await old.StopAsync();
        preview.Content = null; image?.Dispose(); image = null;
        foreach (var bitmap in thumbnails) bitmap.Dispose(); thumbnails.Clear();
        var path = temporary; temporary = null;
        if (path != null) { try { File.Delete(path); } catch (IOException) { } }
    }
}
