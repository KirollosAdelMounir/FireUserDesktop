using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Styling;
using System;
using System.ComponentModel;
using System.Threading;
using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.ViewModels;

namespace UserIdentityDesktop.Views;

public partial class ChatView : UserControl
{
    private ChatViewModel? model;
    private CancellationTokenSource? motion;
    public ChatView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
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

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (model != null) { model.RequestScrollToBottom -= ScrollToBottom; model.PropertyChanged -= OnModelChanged; }
        model = DataContext as ChatViewModel;
        if (model != null) { model.RequestScrollToBottom += ScrollToBottom; model.PropertyChanged += OnModelChanged; }
    }
    private async void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ChatViewModel.SelectedRecipient)) return;
        motion?.Cancel(); motion?.Dispose(); motion = null;
        if (model?.Workspace?.ReduceMotion != false) return;
        var stop = new CancellationTokenSource(); motion = stop;
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(240), Easing = new AngularNavigationEasing(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0d), new Setter(Avalonia.Media.TranslateTransform.YProperty, 10d) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1d), new Setter(Avalonia.Media.TranslateTransform.YProperty, 0d) } }
            }
        };
        try { await animation.RunAsync(ConversationSurface, stop.Token); }
        catch (OperationCanceledException) { }
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        motion?.Cancel(); motion?.Dispose(); motion = null;
        if (model != null) { model.RequestScrollToBottom -= ScrollToBottom; model.PropertyChanged -= OnModelChanged; }
        base.OnDetachedFromVisualTree(e);
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (model != null)
        {
            model.RequestScrollToBottom -= ScrollToBottom; model.RequestScrollToBottom += ScrollToBottom;
            model.PropertyChanged -= OnModelChanged; model.PropertyChanged += OnModelChanged;
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
