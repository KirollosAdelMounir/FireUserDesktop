using Avalonia.Controls;
using Avalonia.Interactivity;

namespace UserIdentityDesktop.Views;

public partial class MainWindow : Window
{
    public bool AllowClose { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    internal static bool HidesOnClose(bool allowClose, WindowCloseReason reason) => !allowClose && reason is WindowCloseReason.WindowClosing or WindowCloseReason.Undefined;

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (HidesOnClose(AllowClose, e.CloseReason))
        {
            e.Cancel = true;
            Hide();
        }
    }
}
