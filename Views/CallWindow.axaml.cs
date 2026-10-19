using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using UserIdentityDesktop.ViewModels;

namespace UserIdentityDesktop.Views;

public partial class CallWindow : Window
{
    private bool closingAfterCall, leaving;
    public CallWindow()
    {
        InitializeComponent();
        Closing += async (_, args) =>
        {
            if (closingAfterCall || DataContext is not WorkspaceViewModel { InCall: true } vm) return;
            args.Cancel = true;
            if (leaving) return;
            leaving = true;
            try { await vm.LeaveCommand.ExecuteAsync(null); }
            catch (Exception ex) { vm.Feedback = ex.Message; }
            finally { CloseAfterCall(); }
        };
    }
    private void Devices_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel vm) vm.RefreshDevicesCommand.Execute(null);
    }
    public void CloseAfterCall() { closingAfterCall = true; Dispatcher.UIThread.Post(() => { if (IsVisible) Close(); }); }
}
