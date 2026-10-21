using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Styling;
using System;
using System.Linq;
using System.ComponentModel;
using System.Threading;
using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.ViewModels;

namespace UserIdentityDesktop.Views;

public partial class ChatView : UserControl
{
    private ChatViewModel? model;
    private CancellationTokenSource? motion;
    private void SearchResult_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Dispatcher.UIThread.Post(() => { FindPeopleButton.Flyout?.Hide(); MessageInputBox.Focus(); });
    }
    private async void JoinCall_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) { if (DataContext is ChatViewModel { Workspace: {} workspace } && sender is Control { DataContext: ChatMessageItemViewModel { Message.CallSummary: {} call } }) await workspace.JoinExistingCallAsync(call.CallId); }
    private void Message_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (DataContext is ChatViewModel vm && sender is Control { DataContext: ChatMessageItemViewModel item } target)
        { MessageMenus.Open(target, MessageMenus.Create(vm, item, target), e.TryGetPosition(target, out _)); e.Handled = true; }
    }
    private void MessageMenu_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is ChatViewModel vm && sender is Control { DataContext: ChatMessageItemViewModel item } target)
            MessageMenus.Open(target, MessageMenus.Create(vm, item, target), false);
    }
    private ContextMenu ConversationMenu(RecipientItemViewModel? recipient = null)
    {
        var menu = new ContextMenu(); if (model == null) return menu;
        if (recipient != null) model.SelectedRecipient = recipient;
        void Add(string title, Action action) => menu.Items.Add(new MenuItem { Header = title, Command = new CommunityToolkit.Mvvm.Input.RelayCommand(action) });
        Add("View profile", () => HeaderProfile_Click(null, new()));
        Add("Shared files", () => model.OpenConversationDetails(0));
        Add("Members", () => model.OpenConversationDetails(1));
        menu.Items.Add(new Separator()); Add("Chat security", () => model.OpenConversationDetails(2));
        return menu;
    }
    private void ConversationMenu_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) { if (sender is Control target) MessageMenus.Open(target, ConversationMenu(), false); }
    private void Recipient_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is Control { DataContext: RecipientItemViewModel item } target) { MessageMenus.Open(target, ConversationMenu(item), e.TryGetPosition(target, out _)); e.Handled = true; }
    }
    private void RecipientMenu_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control { DataContext: RecipientItemViewModel item } target) MessageMenus.Open(target, ConversationMenu(item), false);
    }
    private async void HeaderProfile_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (model?.SelectedRecipient is not {} selected) return;
        if (selected.IsGroup) { model.OpenConversationDetails(1); return; }
        await model.OpenConversationProfileAsync();
    }
    private void AccountMenu_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (model == null || sender is not Control target) return;
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "My profile", Command = model.OpenMyProfileCommand });
        var status = new MenuItem { Header = "Availability" };
        foreach (var option in model.StatusOptions) status.Items.Add(new MenuItem { Header = option.Label, Command = model.SetStatusCommand, CommandParameter = option.Status });
        menu.Items.Add(status); menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Sign out", Command = model.LogoutCommand }); MessageMenus.Open(target, menu, false);
    }
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
            if (model.Messages.Count > 0) ScrollToBottom();
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
            var messages = this.FindControl<ListBox>("MessageStream");
            if (messages?.ItemCount > 0) messages.ScrollIntoView(messages.ItemCount - 1);
        }, DispatcherPriority.Background);
    }
}
