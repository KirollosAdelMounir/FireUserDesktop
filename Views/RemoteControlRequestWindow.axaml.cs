using System;
using Avalonia.Controls;
using Avalonia.Threading;
using UserIdentityDesktop.ViewModels;

namespace UserIdentityDesktop.Views;

public partial class RemoteControlRequestWindow : Window
{
    private readonly DispatcherTimer countdown = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly SurfaceMotion motion = new();
    private DateTimeOffset deadline;
    public RemoteControlRequestWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            deadline = (DataContext as WorkspaceViewModel)?.ControlRequestExpiresAt ?? DateTimeOffset.UtcNow.AddSeconds(30);
            UpdateCountdown(); countdown.Start();
            if (Content is Control surface) _ = motion.EnterAsync(surface, SurfaceMotion.Reduced(this), SurfaceMotionKind.Toast);
        };
        countdown.Tick += (_, _) => UpdateCountdown();
        Closed += (_, _) => { countdown.Stop(); motion.Cancel(); };
    }
    private void UpdateCountdown() => Countdown.Text = $"Automatically rejects in {Math.Max(0, (int)Math.Ceiling((deadline - DateTimeOffset.UtcNow).TotalSeconds))} seconds";
}
