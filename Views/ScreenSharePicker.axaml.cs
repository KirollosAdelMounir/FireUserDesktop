using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using UserIdentityDesktop.ViewModels;

namespace UserIdentityDesktop.Views;

public partial class ScreenSharePicker : Window
{
    public ScreenSharePicker()
    {
        InitializeComponent();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(null); } };
    }
    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
    private void Share_Click(object? sender, RoutedEventArgs e) { if (DataContext is ScreenSharePickerViewModel { SelectedSource: {} source }) Close(source); }
}
