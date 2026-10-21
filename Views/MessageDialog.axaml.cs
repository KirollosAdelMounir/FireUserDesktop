using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using UserIdentityDesktop.ViewModels;
namespace UserIdentityDesktop.Views;
public partial class MessageDialog : Window
{
    public MessageDialog() { InitializeComponent(); KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(false); } }; }
    public MessageDialog(MessageDialogModel model) : this() => DataContext = model;
    private void Close_Click(object? sender, RoutedEventArgs e) => Close(false);
    private void Delete_Click(object? sender, RoutedEventArgs e) => Close(true);
}
