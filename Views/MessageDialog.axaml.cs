using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using UserIdentityDesktop.ViewModels;
namespace UserIdentityDesktop.Views;
public partial class MessageDialog : Window
{
    private readonly WindowMotion motion;
    public MessageDialog() { InitializeComponent(); motion = new WindowMotion(this); KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; _ = motion.CloseAsync(false); } }; }
    public MessageDialog(MessageDialogModel model) : this() => DataContext = model;
    private void Close_Click(object? sender, RoutedEventArgs e) => _ = motion.CloseAsync(false);
    private void Delete_Click(object? sender, RoutedEventArgs e) => _ = motion.CloseAsync(true);
}
