using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using UserIdentityDesktop.Core;
using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.ViewModels;
using UserIdentityDesktop.Views;
using Xunit;
namespace UserIdentityDesktop.Tests;

public sealed class CollaborationParityTests
{
    private static (WorkspaceViewModel Vm, ICollaborationApi Api, INativeMediaSession Media, IChatService Chat) Create()
    {
        var auth = Substitute.For<IAuthService>(); auth.CurrentUser.Returns(new UserData { Id = Guid.NewGuid() });
        var chat = Substitute.For<IChatService>(); var api = Substitute.For<ICollaborationApi>(); var media = Substitute.For<INativeMediaSession>();
        var vm = new ChatViewModel(auth, chat, Substitute.For<IUserService>(), Substitute.For<IImageService>(), Substitute.For<IFileService>(), Substitute.For<INotificationService>());
        return (new WorkspaceViewModel(vm, api, media, auth, chat), api, media, chat);
    }
    private static async Task Start(WorkspaceViewModel vm, ICollaborationApi api)
    {
        vm.Chat.SelectedRecipient = new RecipientItemViewModel { ReceiverId = Guid.NewGuid(), ReceiverName = "Kirollos Mounir" }; vm.CallingEnabled = true;
        api.CreateCallAsync(Arg.Any<Guid>(), false, Arg.Any<CancellationToken>()).Returns(new CallSession("call", "audio", new(), null));
        api.CallTokenAsync("call", Arg.Any<CancellationToken>()).Returns(new MediaToken("wss://media.test", "token"));
        await vm.StartVoiceCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs();
    }
    [AvaloniaFact]
    public async Task AudioOnlyParticipantAppears_UpdatesMuteAndQuality_AndLeavesWithoutFrames()
    {
        var (vm, api, media, _) = Create(); await Start(vm, api);
        media.ParticipantJoined += Raise.Event<Action<MediaParticipant>>(new MediaParticipant("peer", "Kirollos Mounir")); Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, vm.Videos.Count); var peer = vm.Videos.Single(x => x.Identity == "peer");
        Assert.Equal("Kirollos Mounir", peer.Name); Assert.Null(peer.Frame); Assert.Equal("KM", peer.Initials);
        media.ParticipantUpdated += Raise.Event<Action<MediaParticipantUpdate>>(new MediaParticipantUpdate("peer", "microphone", "1"));
        media.ParticipantUpdated += Raise.Event<Action<MediaParticipantUpdate>>(new MediaParticipantUpdate("peer", "quality", "2")); Dispatcher.UIThread.RunJobs();
        Assert.True(peer.MicrophoneOn); Assert.Equal("Excellent", peer.Quality);
        var window = new CallWindow { DataContext = vm }; window.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark; window.Show(); Save(window, "audio-only-call.png");
        media.ParticipantLeft += Raise.Event<Action<string>>("peer"); Dispatcher.UIThread.RunJobs();
        Assert.Single(vm.Videos); Assert.Contains("1 participants", vm.ParticipantSummary);
        window.CloseAfterCall(); Dispatcher.UIThread.RunJobs(); await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task ScreenShareAutomaticallyFocusesStage_AndTrackRemovalRetainsAudioParticipant()
    {
        var (vm, api, media, _) = Create(); await Start(vm, api);
        media.ParticipantJoined += Raise.Event<Action<MediaParticipant>>(new MediaParticipant("peer", "Colleague")); Dispatcher.UIThread.RunJobs();
        media.VideoReceived += Raise.Event<Action<VideoPicture>>(new VideoPicture("peer · camera · cam", 2, 2, new byte[16])); Dispatcher.UIThread.RunJobs();
        Assert.NotNull(vm.Videos.Single(x => x.Identity == "peer").Frame); Assert.Null(vm.FocusedVideo);
        media.ParticipantLeft += Raise.Event<Action<string>>("peer · camera · cam"); Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.Videos.Single(x => x.Identity == "peer").Frame); Assert.Equal(2, vm.Videos.Count);
        media.VideoReceived += Raise.Event<Action<VideoPicture>>(new VideoPicture("peer · screen · share", 160, 90, Enumerable.Repeat((byte)230,160 * 90 * 4).ToArray())); Dispatcher.UIThread.RunJobs();
        Assert.True(vm.HasFocusedVideo); Assert.Equal("peer · screen · share", vm.FocusedVideo!.Identity);
        var window = new CallWindow { DataContext = vm }; window.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark; window.Show(); Save(window, "focused-screen-share.png");
        media.ParticipantLeft += Raise.Event<Action<string>>("peer · screen · share"); Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.FocusedVideo); Assert.Equal(2, vm.Videos.Count);
        window.CloseAfterCall(); Dispatcher.UIThread.RunJobs(); await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task PendingMeetingInvitation_DoesNotRequestTokenOrStartDevices()
    {
        var (vm, api, media, _) = Create(); vm.MeetingCode = "invite";
        api.MeetingAsync("invite", Arg.Any<CancellationToken>()).Returns(new MeetingDetail(Guid.NewGuid(), "invite", "Review", true, "", 0, false, true, Guid.NewGuid(), 7));
        await vm.JoinMeetingCommand.ExecuteAsync(null);
        await api.DidNotReceive().MeetingTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await media.DidNotReceive().ConnectAsync(Arg.Any<MediaToken>(), Arg.Any<CancellationToken>());
        Assert.Contains("Accept this invitation", vm.Feedback); Assert.NotNull(vm.PendingMeetingInvitation);
        await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task SlowCalendar_DoesNotHoldBackCallingCapability()
    {
        var (vm, api, _, _) = Create(); var pending = new TaskCompletionSource<List<CalendarItem>>();
        api.CalendarAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(pending.Task);
        api.CallingEnabledAsync(Arg.Any<CancellationToken>()).Returns(true);
        api.CatalogAsync(Arg.Any<CancellationToken>()).Returns(new WorkplaceCatalog(new()));
        api.AnnouncementsAsync(Arg.Any<CancellationToken>()).Returns(new List<Announcement>());
        var refresh = vm.RefreshAsync();
        Assert.True(vm.CallingEnabled); Assert.False(refresh.IsCompleted); Assert.False(vm.IsLoading);
        pending.SetResult(new()); await refresh; await vm.DisposeAsync();
    }
    private static void Snapshot(ICollaborationApi api)
    {
        api.CalendarAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(new List<CalendarItem>());
        api.AnnouncementsAsync(Arg.Any<CancellationToken>()).Returns(new List<Announcement>());
        api.CallingEnabledAsync(Arg.Any<CancellationToken>()).Returns(true);
    }
    [AvaloniaFact]
    public async Task BoundCalendarSelector_CatalogReloadDoesNotRepeatSnapshotRequests()
    {
        var (vm, api, _, chat) = Create(); Snapshot(api);
        var self = vm.Chat.CurrentUser.Id; var report = Guid.NewGuid(); var catalogs = 0;
        api.CatalogAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            // Bound the old feedback loop so a regression fails instead of hanging the test host.
            if (++catalogs > 8) return Task.FromException<WorkplaceCatalog>(new InvalidOperationException("Unexpected snapshot loop"));
            return Task.FromResult(new WorkplaceCatalog(new() { new(self, "Me"), new(report, "Report " + catalogs, ReportingDistance: 1) }));
        });
        var view = new WorkspaceView { DataContext = vm }; vm.SelectedWorkspaceTab = 2;
        var window = new Window { Width = 1200, Height = 850, Content = view }; window.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            await vm.RefreshAsync(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, catalogs); Assert.Equal(self, vm.SelectedCalendarPerson!.Id);
            var selector = view.GetVisualDescendants().OfType<ComboBox>().Single(x => ReferenceEquals(x.ItemsSource, vm.CalendarPeople));
            selector.SelectedItem = vm.CalendarPeople.Single(x => x.Id == report); Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, catalogs); Assert.Equal(report, vm.SelectedCalendarPerson!.Id);
            chat.WorkplaceChanged += Raise.Event<Action>(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(3, catalogs); Assert.Equal(report, vm.SelectedCalendarPerson!.Id);
            Assert.Equal("Report 3", ((WorkplacePerson)selector.SelectedItem!).Name);
            await api.Received(3).CalendarAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
            await api.Received(3).AnnouncementsAsync(Arg.Any<CancellationToken>());
            await api.Received(3).CallingEnabledAsync(Arg.Any<CancellationToken>());
            // Repeated dispatcher/layout passes while idle do not request data.
            for (var i = 0; i < 4; i++) { window.InvalidateMeasure(); Dispatcher.UIThread.RunJobs(); }
            Assert.Equal(3, catalogs);
        }
        finally { window.Close(); await vm.DisposeAsync(); }
    }
    [AvaloniaFact]
    public async Task BoundCalendarSelector_RemovedReportFallsBackToOwnCalendarOnce()
    {
        var (vm, api, _, _) = Create(); Snapshot(api); var self = vm.Chat.CurrentUser.Id; var report = Guid.NewGuid();
        var includeReport = true; var calls = 0;
        api.CatalogAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (++calls > 8) return Task.FromException<WorkplaceCatalog>(new InvalidOperationException("Unexpected snapshot loop"));
            return Task.FromResult(new WorkplaceCatalog(includeReport ? new() { new(self, "Me"), new(report, "Report", ReportingDistance: 1) } : new() { new(self, "Me") }));
        });
        var view = new WorkspaceView { DataContext = vm }; vm.SelectedWorkspaceTab = 2;
        var window = new Window { Width = 1200, Height = 850, Content = view }; window.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            await vm.RefreshAsync(); Dispatcher.UIThread.RunJobs();
            vm.SelectedCalendarPerson = vm.CalendarPeople.Single(x => x.Id == report); Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, calls);
            includeReport = false; api.ClearReceivedCalls();
            await vm.RefreshAsync(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(4, calls); Assert.Equal(self, vm.SelectedCalendarPerson!.Id); Assert.Single(vm.CalendarPeople);
            await api.Received(1).CalendarAsync(report, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
            await api.Received(1).CalendarAsync(self, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
            Assert.Equal("", vm.Feedback);
        }
        finally { window.Close(); await vm.DisposeAsync(); }
    }
    private sealed class BindingLog : Avalonia.Logging.ILogSink
    {
        public List<string> Errors { get; } = new();
        public bool IsEnabled(Avalonia.Logging.LogEventLevel level, string area) => area == "Binding" && level >= Avalonia.Logging.LogEventLevel.Warning;
        public void Log(Avalonia.Logging.LogEventLevel level, string area, object? source, string messageTemplate) => Errors.Add(messageTemplate);
        public void Log(Avalonia.Logging.LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) => Errors.Add(messageTemplate + " " + string.Join(";", propertyValues));
    }
    [AvaloniaFact]
    public async Task OptionalReviewBindings_NullAndLoadedTransitionsHaveNoBindingErrors()
    {
        var (vm, api, _, _) = Create(); api.VacationsAsync(1, Arg.Any<CancellationToken>()).Returns(new VacationPage(new(), false, 1));
        var previous = Avalonia.Logging.Logger.Sink; var log = new BindingLog(); Avalonia.Logging.Logger.Sink = log;
        var view = new WorkspaceView(); var window = new Window { Width = 1200, Height = 850, Content = view };
        try
        {
            view.DataContext = vm; window.Show(); Dispatcher.UIThread.RunJobs();
            vm.SelectedWorkspaceTab = 1; Dispatcher.UIThread.RunJobs();
            Assert.Empty(vm.MeetingInvitationTitle); Assert.Empty(vm.MeetingInvitationConflicts);
            vm.PendingMeetingInvitation = new(Guid.NewGuid(), "invite", "Invitation title", true, "", 0, false, true);
            var conflict = new CalendarConflict(Guid.NewGuid(), "Meeting", "Overlap", DateTime.UtcNow, DateTime.UtcNow.AddHours(1), false);
            vm.MeetingInvitationReview = new(new() { conflict }, "review"); Dispatcher.UIThread.RunJobs();
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), x => x.Text == "Invitation title");
            Assert.Contains(view.GetVisualDescendants().OfType<ItemsControl>(), x => ReferenceEquals(x.ItemsSource, vm.MeetingInvitationConflicts) && x.ItemCount == 1);
            vm.PendingMeetingInvitation = null; vm.MeetingInvitationReview = null;
            vm.SelectedWorkspaceTab = 2; Dispatcher.UIThread.RunJobs();
            vm.SelectedCalendarTab = 1; Dispatcher.UIThread.RunJobs();
            vm.ShowVacationEditor = true; Dispatcher.UIThread.RunJobs();
            var vacationEditor = Assert.Single(view.EditorWindows);
            var shifts = new List<VacationShift> { new(DateOnly.FromDateTime(DateTime.Today), DateTime.UtcNow, DateTime.UtcNow.AddHours(8), 1) };
            var balances = new List<VacationBalance> { new(2026, 20, 0, 0) }; vm.VacationReview = new(shifts, "preview", balances); Dispatcher.UIThread.RunJobs();
            Assert.Contains(vacationEditor.GetVisualDescendants().OfType<ItemsControl>(), x => ReferenceEquals(x.ItemsSource, shifts) && x.ItemCount == 1);
            Assert.Contains(vacationEditor.GetVisualDescendants().OfType<ItemsControl>(), x => ReferenceEquals(x.ItemsSource, balances) && x.ItemCount == 1);
            vm.VacationReview = null; Assert.Empty(vm.VacationShifts); Assert.Empty(vm.VacationBalances);
            await vacationEditor.CloseAnimatedAsync();
            var chatView = new ChatView { DataContext = vm.Chat }; window.Content = chatView; Dispatcher.UIThread.RunJobs();
            Assert.Empty(vm.Chat.SelectedRecipientStatusLabel);
            vm.Chat.SelectedRecipient = new() { ReceiverId = Guid.NewGuid(), ReceiverName = "Peer", Status = UserStatus.Online }; Dispatcher.UIThread.RunJobs();
            vm.Chat.SelectedRecipient.Status = UserStatus.Busy; Dispatcher.UIThread.RunJobs();
            Assert.Contains(chatView.GetVisualDescendants().OfType<TextBlock>(), x => x.Text == "Busy");
            vm.Chat.SelectedRecipient = null; Dispatcher.UIThread.RunJobs();
            Assert.Empty(vm.Chat.SelectedRecipientStatusLabel); Assert.Empty(log.Errors);
        }
        finally { Avalonia.Logging.Logger.Sink = previous; window.Close(); await vm.DisposeAsync(); }
    }
    [AvaloniaFact]
    public async Task SnapshotCanceledByDisposal_DoesNotBecomeAnErrorOrRetry()
    {
        var (vm, api, _, _) = Create(); Snapshot(api); api.CatalogAsync(Arg.Any<CancellationToken>()).Returns(new WorkplaceCatalog(new()));
        var pending = new TaskCompletionSource<List<CalendarItem>>();
        api.CalendarAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(call => pending.Task.WaitAsync(call.Arg<CancellationToken>()));
        var refresh = vm.RefreshAsync(); Assert.False(refresh.IsCompleted);
        await vm.DisposeAsync(); await refresh; Dispatcher.UIThread.RunJobs();
        Assert.Equal("", vm.Feedback);
        await api.Received(1).CalendarAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }
    [AvaloniaFact]
    public async Task VacationSubmissionUsesReviewedToken_AndDraftChangesRequireNewPreview()
    {
        var (vm, api, _, _) = Create(); vm.VacationReason = "Family";
        api.PreviewVacationAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), 1, "Family", Arg.Any<CancellationToken>()).Returns(new VacationPreview(new(), "preview", new()));
        await vm.PreviewVacationCommand.ExecuteAsync(null); Assert.Equal("preview", vm.VacationReview!.PreviewToken); Assert.False(vm.IsLoading); Assert.False(vm.MediaBusy);
        vm.VacationDays = "2"; Assert.Null(vm.VacationReview);
        await vm.SubmitVacationCommand.ExecuteAsync(null);
        await api.DidNotReceive().SubmitVacationAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        vm.VacationDays = "1"; await vm.PreviewVacationCommand.ExecuteAsync(null);
        api.VacationsAsync(1, Arg.Any<CancellationToken>()).Returns(new VacationPage(new(), false, 1));
        api.CatalogAsync(Arg.Any<CancellationToken>()).Returns(new WorkplaceCatalog(new())); api.AnnouncementsAsync(Arg.Any<CancellationToken>()).Returns(new List<Announcement>());
        api.CalendarAsync(Arg.Any<Guid>(),Arg.Any<DateTime>(),Arg.Any<DateTime>(),Arg.Any<CancellationToken>()).Returns(new List<CalendarItem>());
        await vm.SubmitVacationCommand.ExecuteAsync(null);
        await api.Received(1).SubmitVacationAsync(vm.Chat.CurrentUser.Id, Arg.Any<DateOnly>(),1,"Family","preview",Arg.Any<CancellationToken>());
        Assert.Null(vm.VacationReview); await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task MessageActionsPreserveStableId_ForwardEditsAndReplies_AndReactToServerChanges()
    {
        var (workspace, _, _, chat) = Create(); var vm = workspace.Chat;
        var peer = Guid.NewGuid(); vm.SelectedRecipient = new RecipientItemViewModel { ReceiverId = peer }; Dispatcher.UIThread.RunJobs();
        var message = new ChatMessage { MessageId = "507f1f77bcf86cd799439011", Sender = vm.CurrentUser.Id, Receiver = peer, Text = "Original" };
        var item = new ChatMessageItemViewModel(message,true,chat,Substitute.For<IFileService>(),Substitute.For<IImageService>()); vm.Messages.Add(item);
        Assert.Equal(message.MessageId,item.Id); vm.BeginEdit(item); vm.MessageDraft = "Revised";
        await vm.SendMessageCommand.ExecuteAsync(null); await chat.Received().EditMessageAsync(message,"Revised"); Assert.Null(vm.EditingMessage);
        vm.BeginReply(item); vm.MessageDraft = "Reply"; chat.SendReplyAsync(peer,"Reply",item.Id).Returns(true);
        await vm.SendMessageCommand.ExecuteAsync(null); await chat.Received().SendReplyAsync(peer,"Reply",item.Id);
        await vm.React(item,"👍"); await chat.Received().ReactAsync(item.Id,"👍");
        chat.MessageUpdated += Raise.Event<Action<ChatMessage>>(new ChatMessage { MessageId = item.Id, Sender = message.Sender, Receiver = peer, Text = "Changed remotely", IsEdited = true }); Dispatcher.UIThread.RunJobs();
        Assert.Equal("Changed remotely",vm.Messages.Single().Text); Assert.True(vm.Messages.Single().Edited);
        await vm.DeleteMessage(vm.Messages.Single()); await chat.Received().DeleteMessageAsync(item.Id);
        chat.MessageDeleted += Raise.Event<Action<string>>(item.Id); Dispatcher.UIThread.RunJobs(); Assert.Equal("Message deleted",vm.Messages.Single().Text);
        vm.UnregisterEvents(); await workspace.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task MeetingInvitationRequiresConflictAcknowledgement_ThenJoinsAcceptedMeeting()
    {
        var (vm, api, media, _) = Create(); vm.MeetingCode = "invitation";
        var id = Guid.NewGuid(); var pending = new MeetingDetail(Guid.NewGuid(), "invitation", "Planning", true,"",1,false,true,id,3);
        api.MeetingAsync("invitation",Arg.Any<CancellationToken>()).Returns(pending, pending with { InvitationPending = false });
        api.ReviewResponseAsync(Arg.Is<CalendarItem>(x => x.Id == id && x.Version == 3),Arg.Any<CancellationToken>()).Returns(new CalendarReview(new() { new CalendarConflict(Guid.NewGuid(),"Meeting","Overlap",DateTime.UtcNow,DateTime.UtcNow.AddHours(1),false) },"review"));
        await vm.JoinMeetingCommand.ExecuteAsync(null); await vm.ReviewMeetingInvitationCommand.ExecuteAsync(null);
        await vm.AcceptMeetingInvitationCommand.ExecuteAsync(null);
        await api.DidNotReceive().RespondAsync(Arg.Any<CalendarItem>(),Arg.Any<int>(),Arg.Any<string>(),Arg.Any<bool>(),Arg.Any<CancellationToken>());
        vm.AcknowledgeMeetingConflicts = true; await vm.AcceptMeetingInvitationCommand.ExecuteAsync(null);
        await api.Received(1).RespondAsync(Arg.Is<CalendarItem>(x => x.Id == id && x.Version == 3),1,"review",true,Arg.Any<CancellationToken>());
        Assert.Null(vm.PendingMeetingInvitation);
        api.MeetingTokenAsync("invitation",Arg.Any<CancellationToken>()).Returns(new MediaToken("wss://media.test","token"));
        await vm.JoinMeetingCommand.ExecuteAsync(null); Assert.True(vm.InCall);
        await media.Received().ConnectAsync(Arg.Any<MediaToken>(),Arg.Any<CancellationToken>());
        await media.DidNotReceive().SetMicrophoneAsync(Arg.Any<bool>(),Arg.Any<MediaDevices>(),Arg.Any<CancellationToken>());
        await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task IncomingAlertHasAnswerAndDeclineActions_WithoutTakingFocus()
    {
        var (vm, api, _, _) = Create(); vm.Incoming = new IncomingCall("incoming",Guid.NewGuid(),"Colleague","audio",2);
        var alert = new IncomingCallWindow(vm); alert.Show(); Save(alert,"incoming-call-alert.png");
        Assert.True(alert.Topmost); Assert.False(alert.ShowActivated);
        var buttons = alert.GetVisualDescendants().OfType<Button>().ToArray();
        Assert.Equal(Avalonia.Media.Color.Parse("#168344"), ((Avalonia.Media.ISolidColorBrush)buttons.Single(x => x.Content?.ToString() == "Answer").Background!).Color);
        var decline = buttons.Single(x => x.Content?.ToString() == "Decline");
        Assert.Equal(Avalonia.Media.Color.Parse("#D92748"), ((Avalonia.Media.ISolidColorBrush)decline.Background!).Color);
        decline.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        await api.Received().DeclineCallAsync("incoming",Arg.Any<CancellationToken>()); Assert.Null(vm.Incoming);
        await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task ScreenShareFullscreen_HidesChrome_RestoresPanelsAndWindowStateOnEscape()
    {
        var (vm, api, media, _) = Create(); await Start(vm,api);
        media.VideoReceived += Raise.Event<Action<VideoPicture>>(new VideoPicture("peer · screen · share",160,90,new byte[160*90*4])); Dispatcher.UIThread.RunJobs();
        vm.ShowCallMembers = vm.ShowCallChat = true;
        var window = new CallWindow { DataContext = vm, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark }; window.Show(); Dispatcher.UIThread.RunJobs();
        var button = window.FindControl<Button>("FullscreenButton")!; Assert.True(button.IsVisible);
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        Assert.Equal(WindowState.FullScreen,window.WindowState); Assert.Contains("share-fullscreen",window.Classes);
        Assert.False(window.FindControl<Border>("CallHeader")!.IsVisible); Assert.False(window.FindControl<Border>("CallFooter")!.IsVisible);
        Assert.False(vm.ShowCallMembers); Assert.False(vm.ShowCallChat); Assert.True(vm.InCall);
        Assert.True(window.FindControl<Button>("ExitFullscreenButton")!.IsVisible); Save(window,"fullscreen-screen-share.png");
        var escape = new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Escape };
        window.RaiseEvent(escape); Dispatcher.UIThread.RunJobs();
        Assert.True(escape.Handled); Assert.Equal(WindowState.Normal,window.WindowState); Assert.True(vm.InCall);
        Assert.True(vm.ShowCallMembers); Assert.True(vm.ShowCallChat); Assert.True(window.FindControl<Border>("CallHeader")!.IsVisible);
        await media.DidNotReceive().DisconnectAsync();
        window.CloseAfterCall(); Dispatcher.UIThread.RunJobs(); await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task FullscreenShortcut_RequiresScreenShare_AndShareEndRestoresMaximizedWindow()
    {
        var (vm, api, media, _) = Create(); await Start(vm,api);
        var window = new CallWindow { DataContext = vm }; window.Show(); window.WindowState = WindowState.Maximized;
        window.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.F11 });
        Assert.Equal(WindowState.Maximized,window.WindowState);
        media.VideoReceived += Raise.Event<Action<VideoPicture>>(new VideoPicture("peer · screen · share",2,2,new byte[16])); Dispatcher.UIThread.RunJobs();
        window.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.F11 }); Dispatcher.UIThread.RunJobs();
        Assert.Equal(WindowState.FullScreen,window.WindowState);
        media.ParticipantLeft += Raise.Event<Action<string>>("peer · screen · share"); Dispatcher.UIThread.RunJobs();
        Assert.Equal(WindowState.Maximized,window.WindowState); Assert.DoesNotContain("share-fullscreen",window.Classes); Assert.True(vm.InCall);
        await media.DidNotReceive().DisconnectAsync(); window.CloseAfterCall(); Dispatcher.UIThread.RunJobs(); await vm.DisposeAsync();
    }
    private static void Save(Window window,string name)
    {
        Dispatcher.UIThread.RunJobs(); using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        if (Environment.GetEnvironmentVariable("FIREUSER_TEST_ARTIFACTS") is {} path) { Directory.CreateDirectory(path); using var file = File.Create(Path.Combine(path,name)); frame.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
    }
}
