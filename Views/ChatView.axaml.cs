using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.ViewModels;

namespace UserIdentityDesktop.Views;

public partial class ChatView : UserControl
{
    public ChatView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is ChatViewModel vm)
        {
            vm.RequestScrollToBottom += ScrollToBottom;
        }

        PointerMoved += (_, _) =>
        {
            if (DataContext is ChatViewModel chatVm)
                chatVm.RecordActivity();
        };
        var inputBox = this.FindControl<TextBox>("MessageInputBox");
        if (inputBox != null)
        {
            inputBox.KeyDown += (s, args) =>
            {
                if (args.Key == Key.Enter && !args.KeyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    args.Handled = true;
                    if (DataContext is ChatViewModel chatVm)
                    {
                        chatVm.RecordActivity();
                        chatVm.SendMessageCommand.Execute(null);
                    }
                }
            };
        }
    }

    private void StatusButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: StatusOption option } && DataContext is ChatViewModel vm)
            vm.SetStatusCommand.Execute(option.Status);
    }

    private void ScrollToBottom()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var scrollViewer = this.FindControl<ScrollViewer>("ChatMessagesScrollViewer");
            scrollViewer?.ScrollToEnd();
        }, DispatcherPriority.Background);
    }
}
