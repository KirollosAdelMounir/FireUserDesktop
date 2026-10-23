using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UserIdentityDesktop.ViewModels;

namespace UserIdentityDesktop.Views;

/// <summary>Actionable native alert that stays usable while the main window is minimized.</summary>
public sealed partial class IncomingCallWindow : Window
{
    private readonly WorkspaceViewModel? model;
    private readonly string? callId;
    public IncomingCallWindow() { InitializeComponent(); _ = new WindowMotion(this, SurfaceMotionKind.Toast); }
    public IncomingCallWindow(WorkspaceViewModel vm) : this()
    {
        model = vm; callId = vm.Incoming!.CallId;
        DataContext = vm;
        var answer = AnswerButton; var decline = DeclineButton;
        answer.Click += async (_, _) =>
        {
            if (vm.Incoming?.CallId != callId) { Close(); return; }
            answer.IsEnabled = decline.IsEnabled = false;
            answer.Content = "Connecting…";
            CallAlertError.Text = "Connecting to the call. Please wait…"; CallAlertError.IsVisible = true;
            await vm.AcceptCommand.ExecuteAsync(null);
            if (vm.Incoming?.CallId == callId) { answer.IsEnabled = decline.IsEnabled = true; answer.Content = "Answer"; CallAlertError.Text = vm.Feedback; CallAlertError.IsVisible = !string.IsNullOrWhiteSpace(vm.Feedback); }
        };
        decline.Click += async (_, _) => { if (vm.Incoming?.CallId == callId) await vm.DeclineCommand.ExecuteAsync(null); };
        void PlaceAlert()
        {
            if (Screens.Primary is {} screen) Position = RightCenter(screen.WorkingArea, Bounds.Width > 0 ? Bounds.Width : Width, Bounds.Height > 0 ? Bounds.Height : Height, screen.Scaling);
        }
        Opened += (_, _) => PlaceAlert(); SizeChanged += (_, _) => PlaceAlert();
        vm.PropertyChanged += Changed;
        Closed += (_, _) => vm.PropertyChanged -= Changed;
    }
    internal static PixelPoint RightCenter(PixelRect area, double width, double height, double scale)
    {
        var gap = (int)Math.Round(20 * scale);
        return new PixelPoint(Math.Max(area.X, area.Right - (int)Math.Round(width * scale) - gap),
            area.Y + Math.Max(0, (area.Height - (int)Math.Round(height * scale)) / 2));
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceViewModel.Incoming) && model?.Incoming?.CallId != callId) Close();
    }
}
