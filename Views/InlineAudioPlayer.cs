using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UserIdentityDesktop.Service.Media;
using UserIdentityDesktop.ViewModels;

namespace UserIdentityDesktop.Views;

/// <summary>Inline, explicit playback for local previews and authenticated decrypted attachments.</summary>
public sealed class InlineAudioPlayer : UserControl
{
    public static readonly StyledProperty<string?> LocalPathProperty = AvaloniaProperty.Register<InlineAudioPlayer, string?>(nameof(LocalPath));
    public static readonly StyledProperty<ChatAttachmentItemViewModel?> AttachmentProperty = AvaloniaProperty.Register<InlineAudioPlayer, ChatAttachmentItemViewModel?>(nameof(Attachment));
    public static readonly StyledProperty<bool> IsSentProperty = AvaloniaProperty.Register<InlineAudioPlayer, bool>(nameof(IsSent), true);
    public bool IsPreview { get; set; }
    private bool previewApplied;
    private double volume = 1;
    public bool IsSent { get => GetValue(IsSentProperty); set => SetValue(IsSentProperty, value); }
    internal double PlaybackVolume => Volatile.Read(ref volume);
    internal double PlaybackSpeed => rates[rateIndex];
    private static readonly double[] rates = { .5, 1, 1.5, 2 };
    private int rateIndex = 1;
    private readonly VoiceWaveform waveform = new() { Height = 48, MinWidth = 45 };
    private readonly Button speed = new() { Content = "1×", FontSize = 11, Padding = new Thickness(3), MinWidth = 0, MinHeight = 0, Background = Brushes.Transparent, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(5) };
    private readonly Border timePill = new() { Padding = new Thickness(7, 4), CornerRadius = new CornerRadius(15), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
    private Border surface = null!;
    private bool hasWaveform;
    private CancellationTokenSource? waveCancellation;
    public string? LocalPath { get => GetValue(LocalPathProperty); set => SetValue(LocalPathProperty, value); }
    public ChatAttachmentItemViewModel? Attachment { get => GetValue(AttachmentProperty); set => SetValue(AttachmentProperty, value); }
    private readonly Button play = new() { Width = 25, Height = 25, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), CornerRadius = new CornerRadius(13), BorderThickness = new Thickness(0), HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center };
    private readonly Slider volumeSlider = new() { Minimum=0, Maximum=100, Value=100, Width=110 };
    private readonly Button stopButton = new() { Content="Stop", Classes={ "quiet" } };
    private readonly Slider seek = new() { Minimum = 0, Maximum = 1, MinWidth = 45, Height = 48, Opacity = .001, IsEnabled = false, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
    private readonly TextBlock time = new() { Text = "0:00", FontSize = 11, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
    private CancellationTokenSource? cancellation, metadata, preparing;
    private Task playback = Task.CompletedTask;
    private string? cachedPath;
    private double current, duration;
    private bool busy, playing, updating, seeking;
    private int version;
    private static InlineAudioPlayer? active;
    public InlineAudioPlayer()
    {
        MaxWidth = 310;
        var waveTrack = new Grid { MinWidth = 45, Height = 48 }; waveTrack.Children.Add(waveform); waveTrack.Children.Add(seek);
        var waveFocus = new Border { Child = waveTrack, CornerRadius = new CornerRadius(4) };
        seek.GotFocus += (_, e) => { if (e.NavigationMethod is Avalonia.Input.NavigationMethod.Tab or Avalonia.Input.NavigationMethod.Directional) { waveFocus.BorderBrush = waveform.Ink; waveFocus.BorderThickness = new Thickness(1); } };
        seek.LostFocus += (_, _) => waveFocus.BorderThickness = new Thickness(0);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 8 };
        timePill.Child = time;
        grid.Children.Add(play); grid.Children.Add(waveFocus); grid.Children.Add(speed); grid.Children.Add(timePill);
        Grid.SetColumn(waveFocus, 1); Grid.SetColumn(speed, 2); Grid.SetColumn(timePill, 3);
        surface = new Border { Child = grid, Padding = new Thickness(12), CornerRadius = new CornerRadius(20) }; Content = surface;
        ApplyTheme(); SetPlayIcon(false); UpdateSpeedLabel();
        Avalonia.Automation.AutomationProperties.SetName(play, IsPreview ? "Play audio" : "Play voice note"); Avalonia.Automation.AutomationProperties.SetName(seek, "Seek voice note");
        speed.Click += (_, _) => _ = CycleSpeed();
        play.Click += (_, _) => _ = Toggle();
        seek.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty && !updating) seeking = true; };
        seek.AddHandler(PointerReleasedEvent, (_, _) => { if (seeking) _ = Seek(); }, Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        seek.KeyUp += (_, _) => { if (seeking) _ = Seek(); };
        PropertyChanged += (_, e) => { if (e.Property == IsSentProperty) ApplyTheme(); if (e.Property == LocalPathProperty || e.Property == AttachmentProperty) { Reset(); _ = LoadPreviewDuration(); } };
        AttachedToVisualTree += (_, _) => { ApplyPreviewMode(); ApplyTheme(); _ = LoadPreviewDuration(); };
        volumeSlider.PropertyChanged += (_, e) => { if(e.Property==Slider.ValueProperty) Volatile.Write(ref volume,volumeSlider.Value/100); };
        Avalonia.Automation.AutomationProperties.SetName(volumeSlider,"Audio volume");
        stopButton.Click += async (_, _) => await StopPlaybackAsync();
        DetachedFromVisualTree += (_, _) => Reset();
    }
    private void ApplyPreviewMode()
    {
        if (!IsPreview || previewApplied || surface.Child is not Grid grid) return;
        previewApplied=true;
        MaxWidth=720; seek.Opacity=1; seek.Height=34;
        var old=seek.Parent as Panel; old?.Children.Remove(seek);
        var layout=new Grid { RowDefinitions=new RowDefinitions("Auto,Auto,Auto"), RowSpacing=14 };
        surface.Child=null; layout.Children.Add(grid); Grid.SetRow(seek,1); layout.Children.Add(seek);
        var options=new StackPanel { Orientation=Avalonia.Layout.Orientation.Horizontal, Spacing=12 };
        options.Children.Add(stopButton);options.Children.Add(new ContentControl { Content=DesktopIconography.Label("Volume"),VerticalAlignment=Avalonia.Layout.VerticalAlignment.Center });options.Children.Add(volumeSlider);
        Grid.SetRow(options,2);layout.Children.Add(options);surface.Child=layout;
        Avalonia.Automation.AutomationProperties.SetName(play,"Play audio");Avalonia.Automation.AutomationProperties.SetName(seek,"Seek audio");
    }
    internal async Task StopPlaybackAsync()
    {
        version++; preparing?.Cancel(); Pause(); await playback; seeking=false;busy=false;play.IsEnabled=true;UpdatePosition(0);
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 310;
        var desired = base.MeasureOverride(new Size(width, availableSize.Height));
        return new Size(width, desired.Height);
    }
    private void ApplyTheme()
    {
        surface.Bind(Border.BackgroundProperty, this.GetResourceObservable(IsSent ? "ForgePrimaryGradient" : "ForgeSurfaceCard"));
        if (IsSent)
        {
            play.Background = Brushes.White; timePill.Background = Brushes.White; speed.Foreground = Brushes.White; waveform.Ink = Brushes.White;
            play.Bind(Button.ForegroundProperty, this.GetResourceObservable("ForgePrimary")); time.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("ForgePrimary"));
        }
        else
        {
            play.Bind(Button.BackgroundProperty, this.GetResourceObservable("ForgeTextPrimary")); timePill.Bind(Border.BackgroundProperty, this.GetResourceObservable("ForgeTextPrimary"));
            speed.Bind(Button.ForegroundProperty, this.GetResourceObservable("ForgeTextPrimary")); waveform.Bind(VoiceWaveform.InkProperty, this.GetResourceObservable("ForgeTextPrimary"));
            play.Bind(Button.ForegroundProperty, this.GetResourceObservable("ForgeSurfaceCard")); time.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("ForgeSurfaceCard"));
        }
    }
    private void SetPlayIcon(bool paused) => play.Content = new PathIcon { Width = 14, Height = 14, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Data = Geometry.Parse(paused ? "M6,4 L10,4 10,20 6,20 Z M14,4 L18,4 18,20 14,20 Z" : "M8,5 L20,12 8,19 Z") };
    private void UpdateSpeedLabel()
    {
        speed.Content = PlaybackSpeed.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "×";
        Avalonia.Automation.AutomationProperties.SetName(speed, "Playback speed " + speed.Content + ". Change speed");
    }
    private async Task CycleSpeed()
    {
        var generation = version; var resume = playing; Pause(); await playback;
        if (generation != version) return;
        rateIndex = (rateIndex + 1) % rates.Length; UpdateSpeedLabel();
        if (resume) await Toggle();
    }
    private async Task LoadWaveform(string path, int generation)
    {
        if (hasWaveform || waveCancellation != null) return;
        var stop = new CancellationTokenSource(); waveCancellation = stop;
        try
        {
            var peaks = await AudioNotePlayback.WaveformAsync(path, duration, stop.Token);
            if (generation == version && !stop.IsCancellationRequested) { waveform.Peaks = peaks; hasWaveform = true; }
        }
        catch (Exception) { /* A neutral track remains seekable if analysis is unavailable. */ }
        finally { if (waveCancellation == stop) waveCancellation = null; stop.Dispose(); }
    }
    private void Reset()
    {
        version++; preparing?.Cancel(); metadata?.Cancel(); waveCancellation?.Cancel(); waveCancellation = null; hasWaveform = false; waveform.Peaks = Attachment?.Attachment.Encryption?.Waveform?.Take(48).ToArray(); hasWaveform = waveform.Peaks?.Length > 0; Pause(); busy = false; play.IsEnabled = true; seeking = false; current = duration = 0; UpdatePosition(0);
        var old = cachedPath; cachedPath = null;
        var stop = cancellation; cancellation = null;
        _ = DeleteAfterPlayback(old, playback, stop);
    }
    private static async Task DeleteAfterPlayback(string? path, Task complete, CancellationTokenSource? stop)
    {
        try { await complete; } catch { }
        stop?.Dispose();
        try { if (path != null) { await AudioNotePlayback.StopAsync(path); File.Delete(path); } } catch (IOException) { }
    }
    private async Task LoadPreviewDuration()
    {
        if (LocalPath is not { } path || !this.IsAttachedToVisualTree()) return;
        var generation = version; var stop = new CancellationTokenSource();
        metadata?.Cancel(); metadata = stop;
        try
        {
            var value = await AudioNotePlayback.DurationAsync(path, stop.Token);
            if (generation == version && !stop.IsCancellationRequested) { duration = value; UpdatePosition(0); _ = LoadWaveform(path, generation); }
        }
        catch (Exception) { /* Playback can report a useful error when explicitly requested. */ }
        finally { if (metadata == stop) metadata = null; stop.Dispose(); }
    }
    private void Pause()
    {
        playing = false; cancellation?.Cancel(); SetPlayIcon(false);
        Avalonia.Automation.AutomationProperties.SetName(play, IsPreview ? "Play audio" : "Play voice note");
        if (active == this) active = null;
    }
    private async Task Toggle()
    {
        if (playing) { Pause(); return; }
        if (busy) return;
        busy = true; var generation = version; play.IsEnabled = false;
        using var load = new CancellationTokenSource(); preparing = load;
        try
        {
            await playback;
            var path = LocalPath;
            if (path == null && Attachment != null)
            {
                path = cachedPath ?? await Attachment.PrepareAudioPlaybackAsync(load.Token);
                if (generation != version || !this.IsAttachedToVisualTree()) { File.Delete(path); return; }
                cachedPath = path;
            }
            if (path == null || generation != version || !this.IsAttachedToVisualTree()) return;
            duration = await AudioNotePlayback.DurationAsync(path, load.Token);
            if (generation != version || !this.IsAttachedToVisualTree()) return;
            if (current >= duration) current = 0;
            _ = LoadWaveform(path, generation);
            active?.Pause(); active = this;
            cancellation?.Dispose(); cancellation = new CancellationTokenSource();
            playing = true; SetPlayIcon(true); Avalonia.Automation.AutomationProperties.SetName(play, IsPreview ? "Pause audio" : "Pause voice note");
            playback = Run(path, current, cancellation.Token, generation);
        }
        catch (Exception ex) { if (generation == version) { time.Text = "Retry"; ToolTip.SetTip(this, ex.Message); } }
        finally { if (preparing == load) preparing = null; if (generation == version) { busy = false; play.IsEnabled = true; } }
    }
    private async Task Run(string path, double start, CancellationToken ct, int generation)
    {
        var receiptAttachment=Attachment;
        try
        {
            await AudioNotePlayback.PlayAsync(path, start, value => Dispatcher.UIThread.Post(() => { if (!ct.IsCancellationRequested && generation == version) UpdatePosition(value); }), ct, PlaybackSpeed, () => Volatile.Read(ref volume), () => { if(!ct.IsCancellationRequested) _ = ReportPlaybackStarted(receiptAttachment); });
            if (generation == version && !ct.IsCancellationRequested) UpdatePosition(duration);
        }
        catch (OperationCanceledException) { }
        catch (Exception) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { if (generation == version) { time.Text = "Retry"; ToolTip.SetTip(this, ex.Message); } }
        finally { if (generation == version) Pause(); }
    }
    private async Task ReportPlaybackStarted(ChatAttachmentItemViewModel? item)
    {
        try { if(item != null) await item.ReportPlaybackStartedAsync(); }
        catch(Exception) { await Dispatcher.UIThread.InvokeAsync(()=>ToolTip.SetTip(this,"Unable to update the playback receipt.")); }
    }
    private async Task Seek()
    {
        if (busy) return;
        var generation = version; var resume = playing; var value = seek.Value; seeking = false;
        Pause(); await playback;
        if (generation != version) return;
        UpdatePosition(value);
        if (resume) await Toggle();
    }
    private void UpdatePosition(double value)
    {
        current = value; updating = true;
        seek.IsEnabled = duration > 0; seek.Maximum = Math.Max(1, duration); if (!seeking) seek.Value = Math.Min(value, seek.Maximum); updating = false;
        static string Format(double seconds) => $"{(int)seconds / 60}:{(int)seconds % 60:00}";
        waveform.Progress = duration > 0 ? Math.Clamp(value / duration, 0, 1) : 0;
        time.Text = IsPreview ? Format(value) + " / " + Format(duration) : Format(playing || value > 0 ? value : duration);
        Avalonia.Automation.AutomationProperties.SetHelpText(seek, Format(value) + " of " + Format(duration));
    }
}
