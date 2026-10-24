using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using UserIdentityDesktop.Core;
using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.Service;
using UserIdentityDesktop.ViewModels;
using UserIdentityDesktop.Views;
using Xunit;

namespace UserIdentityDesktop.Tests;
public sealed class DesktopRegressionTests
{
    [AvaloniaFact]
    public void EnterSubmitsTheFormOnce_WhileShiftEnterKeepsMultilineEditing()
    {
        var submissions = 0;
        var input = new TextBox { AcceptsReturn = true, Text = "Request details" };
        var form = new StackPanel(); form.Children.Add(input);
        EnterSubmit.SetCommand(form, new CommunityToolkit.Mvvm.Input.RelayCommand(() => submissions++));
        var window = new Window { Content = form }; window.Show(); input.Focus(); Dispatcher.UIThread.RunJobs();
        var enter = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter };
        input.RaiseEvent(enter); Assert.True(enter.Handled); Assert.Equal(1, submissions);
        var newline = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = KeyModifiers.Shift };
        input.RaiseEvent(newline); Assert.Equal(1, submissions);
        window.Close();
    }
    [AvaloniaFact]
    public async Task CalendarEnter_ReviewsBeforeSaving_AndReReviewsChangedDrafts()
    {
        var (vm, _, api, _) = Create();
        vm.EventTitle = "Request"; vm.EventDate = DateTimeOffset.Now.AddDays(2); vm.EventTime = TimeSpan.FromHours(12); vm.IsMeeting = false;
        api.ReviewAsync(Arg.Any<CalendarWrite>(), Arg.Any<CancellationToken>()).Returns(new CalendarReview(new(), "review"));
        api.SaveCalendarAsync(Arg.Any<CalendarWrite>(), Arg.Any<CancellationToken>()).Returns(new SavedCalendar(Guid.NewGuid(), 1));
        await vm.SubmitCalendarFormCommand.ExecuteAsync(null);
        await api.Received(1).ReviewAsync(Arg.Any<CalendarWrite>(), Arg.Any<CancellationToken>());
        await api.DidNotReceive().SaveCalendarAsync(Arg.Any<CalendarWrite>(), Arg.Any<CancellationToken>());
        vm.EventTitle = "Changed request";
        await vm.SubmitCalendarFormCommand.ExecuteAsync(null);
        await api.Received(2).ReviewAsync(Arg.Any<CalendarWrite>(), Arg.Any<CancellationToken>());
        await vm.SubmitCalendarFormCommand.ExecuteAsync(null);
        await api.Received(1).SaveCalendarAsync(Arg.Is<CalendarWrite>(x => x.Title == "Changed request" && x.ConflictToken == "review"), Arg.Any<CancellationToken>());
        vm.Chat.UnregisterEvents(); await vm.DisposeAsync();
    }
    [AvaloniaTheory]
    [InlineData(true)] [InlineData(false)]
    public void DesktopToast_UsesWorkingArea_BoundsStack_AndActivatesOnlyOnce(bool dark)
    {
        var owner = new Window { Width = 900, Height = 700, RequestedThemeVariant = dark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light }; owner.Show();
        var presenter = new DesktopToastPresenter(owner, () => true); var opened = 0;
        for (var i = 0; i < 4; i++) presenter.Show("Planning starts soon", "Your team meeting is ready to join.", () => opened++);
        Dispatcher.UIThread.RunJobs(); Assert.Equal(3, presenter.ActiveWindows.Count);
        var toast = presenter.ActiveWindows[0]; Assert.True(toast.Topmost); Assert.False(toast.ShowActivated); Assert.False(toast.ShowInTaskbar);
        var area = owner.Screens.Primary!.WorkingArea;
        Assert.True(toast.Position.X < area.Right - 10); Assert.True(toast.Position.Y >= area.Y);
        Assert.Contains(toast.GetVisualDescendants().OfType<TextBlock>(), x => x.Text == "Just now");
        if (Environment.GetEnvironmentVariable("FIREUSER_TEST_ARTIFACTS") is {} directory)
        {
            using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(toast);
            using var file = File.Create(Path.Combine(directory, $"desktop-toast-{(dark ? "dark" : "light")}.png")); frame!.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        var open = toast.FindControl<Button>("OpenToast")!;
        open.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); open.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Assert.Equal(1, opened);
        foreach (var remaining in presenter.ActiveWindows.ToArray()) remaining.Close(); owner.Close();
    }
    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task SubTabs_AnimateSelectedPage_AndRapidSwitchesLeaveOnlyCurrentSurface(bool reduced)
    {
        var (vm, _, _, _) = Create(); vm.ReduceMotion = reduced; vm.SelectedWorkspaceTab = 2;
        var view = new WorkspaceView { DataContext = vm };
        var window = new Window { Content = view, Width = 1200, Height = 800 }; window.Show(); Dispatcher.UIThread.RunJobs();
        var tabs = view.FindControl<TabControl>("CalendarTabs")!;
        var browser = view.FindControl<Grid>("CalendarBrowser")!;
        var vacation = view.FindControl<ScrollViewer>("VacationPage")!;
        Assert.Null(view.FindControl<TabControl>("WorkspaceTabs")!.PageTransition);
        vm.SelectedCalendarTab = 1; Dispatcher.UIThread.RunJobs();
        Assert.True(vacation.IsVisible); Assert.False(browser.IsVisible);
        Assert.Equal(reduced, vacation.RenderTransform == null);
        vm.SelectedCalendarTab = 2; Dispatcher.UIThread.RunJobs();
        Assert.True(browser.IsVisible); Assert.False(vacation.IsVisible); Assert.Null(vacation.RenderTransform);
        vm.SelectedCalendarTab = 0; vm.SelectedCalendarTab = 1; Dispatcher.UIThread.RunJobs();
        Assert.False(browser.IsVisible); Assert.Null(browser.RenderTransform); Assert.True(vacation.IsVisible);
        Assert.True(view.FindControl<Border>("CalendarTabFocus")!.Width > 0);
        window.Close(); Assert.Null(vacation.RenderTransform);
        vm.Chat.UnregisterEvents(); await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task MotionCancellation_RestoresTheSurface_AndPopupCanReopenDuringExit()
    {
        var card = new Border { Width = 200, Height = 100, RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative) };
        var window = new Window { Content = card }; window.Show(); Dispatcher.UIThread.RunJobs();
        var motion = new SurfaceMotion(); var entry = motion.EnterAsync(card, false); motion.Cancel(); await entry;
        Assert.Null(card.RenderTransform); Assert.Equal(1, card.Opacity); Assert.Equal(new RelativePoint(0, 0, RelativeUnit.Relative), card.RenderTransformOrigin);
        RevealMotion.SetIsOpen(card, false); Assert.True(card.IsVisible);
        RevealMotion.SetIsOpen(card, true); Assert.True(card.IsVisible);
        await Task.Delay(300, TestContext.Current.CancellationToken); Dispatcher.UIThread.RunJobs();
        Assert.True(card.IsVisible); Assert.Equal(1, card.Opacity); Assert.Null(card.RenderTransform); window.Close();
    }
    [AvaloniaFact]
    public void DesktopToast_CloseDoesNotNavigate_AndChatUsesWebAvatarAndHint()
    {
        var owner = new Window(); owner.Show(); var opened = 0;
        var presenter = new DesktopToastPresenter(owner, () => true);
        presenter.Show("Colleague", "New chat message", () => opened++, true); Dispatcher.UIThread.RunJobs();
        var toast = Assert.IsType<DesktopToastWindow>(Assert.Single(presenter.ActiveWindows));
        Assert.True(toast.IsChat);
        Assert.Contains(toast.GetVisualDescendants().OfType<TextBlock>(), x => x.Text == "Click to open chat" && x.IsVisible);
        toast.FindControl<Button>("CloseToast")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(0, opened); Assert.Empty(presenter.ActiveWindows); owner.Close();
    }
    private static (WorkspaceViewModel Vm, IChatService Chat, ICollaborationApi Api, INativeMediaSession Media) Create(INotificationService? notifications = null)
    {
        var auth = Substitute.For<IAuthService>(); auth.CurrentUser.Returns(new UserData { Id = Guid.NewGuid() });
        var chat = Substitute.For<IChatService>(); var api = Substitute.For<ICollaborationApi>(); var media = Substitute.For<INativeMediaSession>();
        chat.GetChatHistoryAsync(Arg.Any<Guid>(), Arg.Any<int>()).Returns(new List<ChatMessage>());
        api.CalendarAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(new List<CalendarItem>());
        api.AnnouncementsAsync(Arg.Any<CancellationToken>()).Returns(new List<Announcement>());
        api.CatalogAsync(Arg.Any<CancellationToken>()).Returns(new WorkplaceCatalog(new()));
        var vm = new ChatViewModel(auth, chat, Substitute.For<IUserService>(), Substitute.For<IImageService>(), Substitute.For<IFileService>(), notifications ?? Substitute.For<INotificationService>());
        return (new WorkspaceViewModel(vm, api, media, auth, chat, notifications) { ReduceMotion = true, ConfirmActionAsync = (_,_) => Task.FromResult(true) }, chat, api, media);
    }
    [AvaloniaFact]
    public async Task History_ReconcilesLiveArrival_AndCachesConversationAcrossSelection()
    {
        var (vm, service, _, _) = Create(); var peer = Guid.NewGuid();
        var pending = new TaskCompletionSource<List<ChatMessage>>();
        service.GetChatHistoryAsync(peer, 0).Returns(pending.Task);
        var recipient = new RecipientItemViewModel { ReceiverId = peer, ReceiverName = "Peer" };
        vm.Chat.Recipients.Add(recipient); vm.Chat.SelectedRecipient = recipient;
        var live = new ChatMessage { MessageId = "live", Sender = peer, Receiver = vm.Chat.CurrentUser.Id, Text = "Arrived during history", TimeSent = DateTime.UtcNow };
        service.MessageReceived += Raise.Event<Action<ChatMessage>>(live); Dispatcher.UIThread.RunJobs();
        pending.SetResult(new() { new() { MessageId = "old", Sender = peer, Text = "Older", TimeSent = live.TimeSent.AddMinutes(-1) }, live });
        for (var i = 0; i < 10 && vm.Chat.IsLoadingMessages; i++) { await Task.Delay(10, TestContext.Current.CancellationToken); Dispatcher.UIThread.RunJobs(); }
        Assert.Equal(new[] { "old", "live" }, vm.Chat.Messages.Select(x => x.Id));
        vm.Chat.SelectedRecipient = new() { ReceiverId = Guid.NewGuid() }; Dispatcher.UIThread.RunJobs();
        vm.Chat.SelectedRecipient = recipient; Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, vm.Chat.Messages.Count); Assert.False(vm.Chat.IsLoadingMessages);
        await service.Received(1).GetChatHistoryAsync(peer, 0);
        vm.Chat.UnregisterEvents(); await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task Receipt_OnlyMarksMatchingOutgoingMessagesThroughTheServerTimestamp()
    {
        var (vm, service, _, _) = Create(); var peer = Guid.NewGuid(); var now = DateTime.UtcNow;
        vm.Chat.SelectedRecipient = new() { ReceiverId = peer }; Dispatcher.UIThread.RunJobs();
        foreach (var time in new[] { now.AddSeconds(-1), now.AddSeconds(1) })
            vm.Chat.Messages.Add(new(new() { MessageId = time.Ticks.ToString(), Sender = vm.Chat.CurrentUser.Id, Receiver = peer, TimeSent = time }, true, service, Substitute.For<IFileService>(), Substitute.For<IImageService>()));
        service.ReadReceiptReceived += Raise.Event<Action<Guid, Guid, DateTime>>(peer, vm.Chat.CurrentUser.Id, now); Dispatcher.UIThread.RunJobs();
        Assert.True(vm.Chat.Messages[0].Seen); Assert.False(vm.Chat.Messages[1].Seen);
        Assert.Equal("Seen", vm.Chat.Messages[0].ReceiptLabel); Assert.Equal("Sent", vm.Chat.Messages[1].ReceiptLabel);
        vm.Chat.UnregisterEvents(); await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task AcceptCall_ShowsConnectingBeforeTokenReturns_ThenEnablesMicrophone()
    {
        var (vm, _, api, media) = Create(); var pending = new TaskCompletionSource<MediaToken>();
        api.CallTokenAsync("call", Arg.Any<CancellationToken>()).Returns(pending.Task);
        vm.Incoming = new("call", Guid.NewGuid(), "Peer", "audio", 2);
        var accept = vm.AcceptCommand.ExecuteAsync(null);
        Assert.True(vm.ConnectingCall); Assert.True(vm.CallWindowVisible); Assert.False(vm.InCall);
        await media.DidNotReceive().SetMicrophoneAsync(Arg.Any<bool>(), Arg.Any<MediaDevices>(), Arg.Any<CancellationToken>());
        pending.SetResult(new("wss://media.test", "test-only")); await accept;
        Assert.True(vm.InCall); Assert.False(vm.ConnectingCall); Assert.Null(vm.Incoming); Assert.True(vm.MicrophoneEnabled);
        await media.Received(1).SetMicrophoneAsync(true, Arg.Any<MediaDevices>(), Arg.Any<CancellationToken>());
        vm.Chat.UnregisterEvents(); await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task Calendar_OnlyOffersSelfAndDescendantUsers_AndUsesAuthorizedSharedKind()
    {
        var (vm, _, api, _) = Create(); var me = vm.Chat.CurrentUser.Id; var child = Guid.NewGuid(); var unrelated = Guid.NewGuid(); var unit = Guid.NewGuid();
        api.CatalogAsync(Arg.Any<CancellationToken>()).Returns(new WorkplaceCatalog(new() { new(me, "Me"), new(child, "Child", ReportingDistance: 2), new(unrelated, "Unrelated", ReportingDistance: -1) }, new(new(), new() { new(unit, "Team", Guid.NewGuid(), null) })));
        await vm.RefreshAsync(); Dispatcher.UIThread.RunJobs();
        Assert.Equal(new[] { me, child }, vm.CalendarPeople.Select(x => x.Id));
        vm.SelectedCalendarPerson = new(unrelated, "Injected selection"); await vm.RefreshAsync();
        await api.DidNotReceive().CalendarAsync(unrelated, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        vm.SelectedCalendarTab = 2; Dispatcher.UIThread.RunJobs(); await vm.RefreshAsync();
        await api.Received().SharedCalendarAsync("Unit", unit, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        vm.Chat.UnregisterEvents(); await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task PortalSource_StartsSystemSelectionWithoutOpeningApplicationPicker()
    {
        var (vm, _, _, media) = Create(); vm.InCall = true;
        var source = new ScreenShareSource("portal", "System selection", ScreenShareKind.Screen, ScreenCaptureBackend.Portal);
        media.GetScreenSourcesAsync(Arg.Any<CancellationToken>()).Returns(new[] { source });
        var picked = 0; vm.ChooseScreenShareSourceAsync = _ => { picked++; return Task.FromResult<ScreenShareSource?>(null); };
        await vm.ToggleScreenCommand.ExecuteAsync(null);
        Assert.Equal(0, picked); Assert.True(vm.ScreenEnabled);
        await media.Received().SetScreenAsync(true, Arg.Is<MediaDevices>(x => x.ShareSource == source), Arg.Any<CancellationToken>());
        vm.Chat.UnregisterEvents(); await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task NotificationEvent_IsDeduplicated_AndCenterActivationLoadsAnnouncementDetails()
    {
        var notifications = Substitute.For<INotificationService>(); var (vm, chat, api, _) = Create(notifications);
        var id = Guid.NewGuid(); var resource = Guid.NewGuid();
        api.NotificationAsync(id, Arg.Any<CancellationToken>()).Returns(new WorkplaceNotification(id, "New announcement", "Read the update", "Announcement", resource, DateTime.UtcNow, null));
        api.AnnouncementsAsync(Arg.Any<CancellationToken>()).Returns(new List<Announcement> { new(resource, "Update", "Details", "General", DateTime.UtcNow, DateTime.UtcNow.AddDays(1), false, false, 1, false) });
        chat.WorkplaceNotificationChanged += Raise.Event<Action<Guid>>(id); chat.WorkplaceNotificationChanged += Raise.Event<Action<Guid>>(id); Dispatcher.UIThread.RunJobs();
        notifications.Received(1).ShowNotification("New announcement", "Read the update", $"fireuserconnect://notification/{id}");
        var item = new DesktopNotification("New announcement", "Read the update", $"fireuserconnect://notification/{id}", DateTime.UtcNow);
        notifications.NotificationPosted += Raise.Event<Action<DesktopNotification>>(item); Assert.Single(vm.RecentNotifications);
        await vm.OpenNotificationCommand.ExecuteAsync(item);
        Assert.Equal(3, vm.SelectedWorkspaceTab); Assert.Equal(resource, vm.SelectedAnnouncement!.Id);
        Assert.True(vm.HasNotificationDetails); Assert.False(vm.HasWorkspaceStatus);
        vm.SelectedWorkspaceTab = 2; Assert.False(vm.HasWorkspaceStatus);
        vm.DismissNotificationDetailsCommand.Execute(null); Assert.False(vm.HasNotificationDetails);
        await api.Received(1).ReadNotificationAsync(id, Arg.Any<CancellationToken>());
        vm.Chat.UnregisterEvents(); await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task UploadAndRecordingActivities_AreScopedToCurrentConversation_AndStopImmediately()
    {
        var (vm, chat, _, _) = Create(); var peer = Guid.NewGuid(); vm.Chat.SelectedRecipient = new() { ReceiverId = peer, ReceiverName = "Peer" }; Dispatcher.UIThread.RunJobs();
        var recording = new ChatActivity(peer, vm.Chat.CurrentUser.Id, null, "recording", true, "connection");
        chat.ChatActivityReceived += Raise.Event<Action<ChatActivity>>(recording); Dispatcher.UIThread.RunJobs();
        Assert.Contains("recording a voice note", vm.Chat.ChatActivityLabel);
        chat.ChatActivityReceived += Raise.Event<Action<ChatActivity>>(recording with { Active = false }); Dispatcher.UIThread.RunJobs(); Assert.Empty(vm.Chat.ChatActivityLabel);
        chat.ChatActivityReceived += Raise.Event<Action<ChatActivity>>(recording with { UserId = Guid.NewGuid() }); Dispatcher.UIThread.RunJobs(); Assert.Empty(vm.Chat.ChatActivityLabel);
        vm.Chat.UnregisterEvents(); await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task Sidebar_PointerExitCollapsesAfterSelectingATab()
    {
        var (vm, _, _, _) = Create(); var view = new WorkspaceView { DataContext = vm }; var window = new Window { Content = view, Width = 1200, Height = 800 }; window.Show(); Dispatcher.UIThread.RunJobs();
        var rail = view.FindControl<Border>("NavigationRail")!; rail.RaiseEvent(new PointerEventArgs(InputElement.PointerEnteredEvent, rail, new Pointer(1, PointerType.Mouse, true), rail, default, 0, default, default));
        Assert.Contains("expanded", rail.Classes);
        view.GetVisualDescendants().OfType<Button>().First(x => x.Tag?.ToString() == "1").Focus();
        rail.RaiseEvent(new PointerEventArgs(InputElement.PointerExitedEvent, rail, new Pointer(1, PointerType.Mouse, true), rail, default, 0, default, default)); Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain("expanded", rail.Classes);
        window.Close(); vm.Chat.UnregisterEvents(); await vm.DisposeAsync();
    }
}
