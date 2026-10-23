using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.Service.Media;

namespace UserIdentityDesktop.Views;

public sealed class InlineVideoPlayer : UserControl
{
    private readonly NativeVideoView video = new();
    private readonly Button play = new() { Content = "Play", Classes = { "quiet" } };
    private readonly Slider seek = new() { Minimum = 0, Maximum = 1, MinWidth = 80 };
    private readonly TextBlock time = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer display = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly object gate = new();
    private VideoPicture? latest;
    private double position, duration;
    private double volume = 1;
    private string? path;
    private CancellationTokenSource? stop;
    private Task playback = Task.CompletedTask;
    private bool updating, playing, seeking;
    private double seekPosition;
    private string? playbackError;
    internal double Volume => System.Threading.Volatile.Read(ref volume);
    public event Action? FullscreenRequested;
    public InlineVideoPlayer()
    {
        var fullscreen = new Button { Content = "Fullscreen", Classes = { "quiet" } }; fullscreen.Click += (_, _) => FullscreenRequested?.Invoke();
        var stopButton = new Button { Content = "Stop", Classes = { "quiet" } };
        stopButton.Click += async (_, _) => { await PauseAsync(); position=0; seeking=false; };
        var volumeSlider = new Slider { Minimum=0, Maximum=100, Value=100, Width=95 };
        Avalonia.Automation.AutomationProperties.SetName(volumeSlider, "Video volume");
        volumeSlider.PropertyChanged += (_, e) => { if(e.Property==Slider.ValueProperty) System.Threading.Volatile.Write(ref volume,volumeSlider.Value/100); };
        var volumeControls = new StackPanel { Orientation=Orientation.Horizontal, Spacing=5 }; volumeControls.Children.Add(new ContentControl { Content=DesktopIconography.Label("Volume"), Foreground=Brushes.White, VerticalAlignment=VerticalAlignment.Center }); volumeControls.Children.Add(volumeSlider);
        var timeline = new Grid { ColumnDefinitions=new ColumnDefinitions("*,Auto"), ColumnSpacing=12 };
        timeline.Children.Add(seek);timeline.Children.Add(time);Grid.SetColumn(time,1);
        var toolbar = new WrapPanel { Orientation=Orientation.Horizontal, HorizontalAlignment=HorizontalAlignment.Center };
        foreach(var button in new[]{play,stopButton,fullscreen}) { button.Foreground=Brushes.White;button.Background=new SolidColorBrush(Color.Parse("#252525"));toolbar.Children.Add(button); }
        toolbar.Children.Add(volumeControls);
        var controls = new StackPanel { Spacing=8, Margin=new Thickness(12) };controls.Children.Add(timeline);controls.Children.Add(toolbar);
        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Background = Brushes.Black };
        layout.Children.Add(video); Grid.SetRow(controls, 1); layout.Children.Add(controls); Content = layout;
        time.Foreground = Brushes.White;
        Avalonia.Automation.AutomationProperties.SetName(seek, "Seek video");
        play.Click += async (_, _) => { if (playing) await PauseAsync(); else await PlayAsync(); };
        seek.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty && !updating) { seeking = true; seekPosition = seek.Value; } };
        seek.AddHandler(PointerReleasedEvent, async (_, _) => await SeekAsync(), Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        seek.KeyUp += async (_, _) => await SeekAsync();
        display.Tick += (_, _) => { lock (gate) { if (latest != null) { video.Frame = latest; latest = null; } } updating = true; seek.Maximum = Math.Max(1, duration); if (!seeking) seek.Value = Math.Min(position, seek.Maximum); updating = false; time.Text = playbackError ?? (Format(position) + " / " + Format(duration)); };
        DetachedFromVisualTree += (_, _) => _ = StopAsync();
    }
    public async Task LoadAsync(string localPath, CancellationToken cancellationToken = default)
    {
        await StopAsync(); path = localPath; duration = await AudioNotePlayback.DurationAsync(localPath, cancellationToken); position = 0; display.Start();
    }
    public async Task PlayAsync()
    {
        if (path == null || playing) return;
        await playback; if (position >= duration) position = 0;
        playbackError = null; stop?.Dispose(); stop = new(); var token = stop.Token; playing = true; play.Content = "Pause";
        playback = RunAsync(path, position, token);
    }
    private async Task RunAsync(string file, double start, CancellationToken token)
    {
        try { await VideoAttachmentPlayback.PlayAsync(file, start, frame => { lock (gate) latest = frame; }, value => { if (!token.IsCancellationRequested) position = value; }, token, () => System.Threading.Volatile.Read(ref volume)); }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!token.IsCancellationRequested) { playbackError = "Playback failed · retry"; ToolTip.SetTip(play, error.Message); } }
        finally { playing = false; play.Content = "Play"; }
    }
    private async Task SeekAsync()
    {
        if (!seeking) return;
        var target = seekPosition; seeking = false; var resume = playing;
        await PauseAsync(); position = target;
        if (resume) await PlayAsync();
    }
    private async Task PauseAsync() { stop?.Cancel(); await playback; }
    public async Task StopAsync()
    { await PauseAsync(); display.Stop(); path = null; video.Frame = null; lock (gate) latest = null; stop?.Dispose(); stop = null; }
    private static string Format(double value) => TimeSpan.FromSeconds(Math.Max(0, value)).ToString(value >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
}
