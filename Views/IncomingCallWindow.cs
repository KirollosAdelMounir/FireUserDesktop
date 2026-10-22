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
    public IncomingCallWindow() { InitializeComponent(); _ = new WindowMotion(this); }
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
            if (Screens.Primary is {} screen) Position = new PixelPoint(screen.WorkingArea.Right - (int)((Bounds.Width > 0 ? Bounds.Width : Width) * screen.Scaling) - 20, screen.WorkingArea.Bottom - (int)((Bounds.Height > 0 ? Bounds.Height : Height) * screen.Scaling) - 20);
        }
        Opened += (_, _) => PlaceAlert(); SizeChanged += (_, _) => PlaceAlert();
        vm.PropertyChanged += Changed;
        Closed += (_, _) => vm.PropertyChanged -= Changed;
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceViewModel.Incoming) && model?.Incoming?.CallId != callId) Close();
    }
}
