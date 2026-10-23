using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using UserIdentityDesktop.ViewModels;
using UserIdentityDesktop.Core.Models;

namespace UserIdentityDesktop.Views;

public partial class ScreenSharePicker : Window
{
    private readonly WindowMotion motion;
    public ScreenSharePicker()
    {
        InitializeComponent(); motion = new WindowMotion(this);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; _ = motion.CloseAsync<ScreenShareSource?>(null); } };
    }
    private void Cancel_Click(object? sender, RoutedEventArgs e) => _ = motion.CloseAsync<ScreenShareSource?>(null);
    private void Share_Click(object? sender, RoutedEventArgs e) { if (DataContext is ScreenSharePickerViewModel { SelectedSource: {} source }) _ = motion.CloseAsync(source); }
}
