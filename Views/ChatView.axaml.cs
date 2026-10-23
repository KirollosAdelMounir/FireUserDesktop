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
    private AttachmentGalleryWindow? sharedGallery;
    private readonly SurfaceMotion motion = new();
    private void SearchResult_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Dispatcher.UIThread.Post(() => { FindPeopleButton.Flyout?.Hide(); MessageInputBox.Focus(); });
    }
    private Flyout? emojiFlyout;
    internal EmojiPicker? ActiveEmojiPicker => emojiFlyout?.Content as EmojiPicker;
    private void Emoji_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (model == null || model.SelectedRecipient == null || !model.CanTypeMessage || model.IsSendingMessage) return;
        var selectionStart=MessageInputBox.SelectionStart;var selectionEnd=MessageInputBox.SelectionEnd;var recipient=model.SelectedRecipient;
        var picker=new EmojiPicker();
        var flyout=new Flyout { Content=picker,Placement=Avalonia.Controls.PlacementMode.TopEdgeAlignedLeft };emojiFlyout=flyout;
        flyout.Closed+=(_,_)=>{if(ReferenceEquals(emojiFlyout,flyout))emojiFlyout=null;};
        picker.Selected+=emoji=>
        {
            if(model is not {} current || current.SelectedRecipient != recipient || !current.CanTypeMessage){flyout.Hide();return;}
            EmojiPicker.Insert(MessageInputBox,emoji,selectionStart,selectionEnd);flyout.Hide();MessageInputBox.Focus();
        };
        flyout.ShowAt(EmojiButton);
    }
    private async void JoinCall_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) { if (DataContext is ChatViewModel { Workspace: {} workspace } && sender is Control { DataContext: ChatMessageItemViewModel { Message.CallSummary: {} call } }) await workspace.JoinExistingCallAsync(call.CallId); }
    private void SharedAttachment_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (model != null && sender is Control { DataContext: ChatAttachmentItemViewModel attachment } && TopLevel.GetTopLevel(this) is Window owner)
        { var index = model.AllAttachments.IndexOf(attachment); if (index >= 0) { sharedGallery?.Close(); sharedGallery = new AttachmentGalleryWindow(model.AllAttachments.ToArray(), index); sharedGallery.ShowDialog(owner); } }
    }
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
        if(model.Workspace is {} workspace)
        {
            var clock=new MenuItem { Header="Time format" };
            foreach(var value in new[]{true,false})
            {
                var choice=new MenuItem { Header=value?"12-hour (AM/PM)":"24-hour", ToggleType=MenuItemToggleType.Radio, IsChecked=workspace.Clock.TwelveHour==value };
                choice.Click+=(_,_)=>workspace.Clock.TwelveHour=value;clock.Items.Add(choice);
            }
            menu.Items.Add(clock);
            var appearance=new StackPanel { Spacing=8, Width=180, Margin=new Thickness(6) };
            appearance.Children.Add(new TextBlock { Text="Dark to light",FontSize=12 });
            var slider=new Slider { Minimum=0,Maximum=100,TickFrequency=1,IsSnapToTickEnabled=true };
            slider.AddHandler(Avalonia.Input.InputElement.PointerPressedEvent,(_,_)=>workspace.Appearance.BeginAdjusting(),Avalonia.Interactivity.RoutingStrategies.Tunnel);
            slider.AddHandler(Avalonia.Input.InputElement.PointerReleasedEvent,(_,_)=>_ = workspace.Appearance.EndAdjustingAsync(),Avalonia.Interactivity.RoutingStrategies.Tunnel);
            slider.PointerCaptureLost+=(_,_)=>_ = workspace.Appearance.EndAdjustingAsync();
            slider.LostFocus+=(_,_)=>_ = workspace.Appearance.EndAdjustingAsync();
            slider.KeyDown+=(_,key)=>{if(key.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)workspace.Appearance.BeginAdjusting();};
            slider.KeyUp+=(_,_)=>_ = workspace.Appearance.EndAdjustingAsync();
            slider.Bind(Slider.ValueProperty,new Avalonia.Data.Binding("Brightness") { Source=workspace.Appearance,Mode=Avalonia.Data.BindingMode.TwoWay });
            Avalonia.Automation.AutomationProperties.SetName(slider,"Appearance brightness");appearance.Children.Add(slider);
            var feedback=new TextBlock { TextWrapping=Avalonia.Media.TextWrapping.Wrap,FontSize=11 };
            feedback.Bind(TextBlock.TextProperty,new Avalonia.Data.Binding("Status") {Source=workspace.Appearance});appearance.Children.Add(feedback);
            var appearanceMenu=new MenuItem {Header="Appearance"};
            appearanceMenu.Items.Add(new MenuItem { Header=appearance,StaysOpenOnClick=true });menu.Items.Add(appearanceMenu);
        }
        var status = new MenuItem { Header = "Availability" };
        foreach (var option in model.StatusOptions) status.Items.Add(new MenuItem { Header = option.Label, Icon=new PresenceIndicator {Status=option.Status,Width=18,Height=18}, Command = model.SetStatusCommand, CommandParameter = option.Status });
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

    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (model != null) { model.RequestScrollToBottom -= ScrollToBottom; model.PropertyChanged -= OnModelChanged; }
        model = DataContext as ChatViewModel;
        if (model != null) { model.RequestScrollToBottom += ScrollToBottom; model.PropertyChanged += OnModelChanged; }
    }
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatViewModel.SelectedRecipient))
        {
            emojiFlyout?.Hide();emojiFlyout=null;
            _ = motion.EnterAsync(ConversationSurface, model?.Workspace?.ReduceMotion != false);
        }
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        emojiFlyout?.Hide();emojiFlyout=null;
        sharedGallery?.Close(); sharedGallery = null;
        motion.Cancel();
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
