using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using UserIdentityDesktop.Core;
using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.ViewModels;
using UserIdentityDesktop.Views;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(UserIdentityDesktop.Tests.TestApplication))]

namespace UserIdentityDesktop.Tests;

public sealed class TestApplication : Avalonia.Application
{
    public override void Initialize()
    {
        var resources = new UserIdentityDesktop.App(); resources.Initialize();
        var dictionary = resources.Resources;
        resources.Resources = new ResourceDictionary();
        Resources = dictionary;
        var styles = resources.Styles.ToArray(); resources.Styles.Clear();
        foreach (var style in styles) Styles.Add(style);
        DataTemplates.Add(new ViewLocator());
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApplication>().UseSkia().ConfigureFonts(DesktopFonts.Configure).UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class WorkspaceTests
{
    [Theory]
    [InlineData("fireuserconnect://chat/11111111-1111-1111-1111-111111111111")]
    [InlineData("fireuserconnect:///chat/11111111-1111-1111-1111-111111111111")]
    public void NotificationActivation_OpensStandardAndLegacyChatLinks(string link) =>
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), App.ParseDeepLinkUserId(link));
    [Theory]
    [InlineData("https://chat/11111111-1111-1111-1111-111111111111")]
    [InlineData("fireuserconnect://chat/not-a-user")]
    public void NotificationActivation_RejectsUnrelatedSchemesAndInvalidUsers(string link) =>
        Assert.Null(App.ParseDeepLinkUserId(link));
    [AvaloniaFact]
    public async Task MessageHistory_VirtualizesLongConversations()
    {
        var (vm, _, _) = Workspace(); vm.ReduceMotion = true;
        vm.Chat.SelectedRecipient = new RecipientItemViewModel { ReceiverId = Guid.NewGuid(), ReceiverName = "Colleague" };
        for (var i = 0; i < 1000; i++)
            vm.Chat.Messages.Add(new ChatMessageItemViewModel(new ChatMessage { Text = "Message " + i, TimeSent = DateTime.UtcNow }, false, Substitute.For<IChatService>(), Substitute.For<IFileService>(), Substitute.For<IImageService>()));
        var view = new ChatView { DataContext = vm.Chat };
        var window = new Window { Width = 1100, Height = 750, Content = view };
        window.Show(); Dispatcher.UIThread.RunJobs();
        var list = view.FindControl<ListBox>("MessageStream")!;
        Assert.Equal(1000, list.ItemCount);
        var realized = list.GetVisualDescendants().OfType<ListBoxItem>().Count();
        Assert.InRange(realized, 1, 80);
        list.ScrollIntoView(999); Dispatcher.UIThread.RunJobs();
        Assert.Contains(list.GetVisualDescendants().OfType<TextBlock>(), x => x.Text == "Message 999");
        window.Close(); await vm.DisposeAsync();
    }
    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task ForegroundToast_BoundsVisibleCardsAndRendersBranding(bool reduced)
    {
        var owner = new Window { Width = 900, Height = 650, Content = new Grid() };
        owner.Show(); Dispatcher.UIThread.RunJobs();
        var opened = 0;
        var toasts = new ToastPresenter(owner, () => reduced);
        for (var i = 0; i < 5; i++) toasts.Show("Team update " + i, "Ready to review?", () => opened++);
        await Task.Delay(350, TestContext.Current.CancellationToken); Dispatcher.UIThread.RunJobs();
        var cards = owner.GetVisualDescendants().OfType<Avalonia.Controls.Notifications.NotificationCard>().Where(x => !x.IsClosing).ToArray();
        Assert.Equal(3, cards.Length);
        Assert.All(cards, card => Assert.Equal(reduced, card.Classes.Contains("reduced-motion")));
        Assert.Contains(owner.GetVisualDescendants().OfType<TextBlock>(), x => x.Text == "FIRE USER");
        using var frame = owner.CaptureRenderedFrame(); Assert.NotNull(frame);
        var output = Environment.GetEnvironmentVariable("FIREUSER_TEST_ARTIFACTS");
        if (output != null) { Directory.CreateDirectory(output); using var file = File.Create(Path.Combine(output, "foreground-toasts.png")); frame.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
        var openButton = cards[0].GetVisualDescendants().OfType<Button>().Single(x => x.Classes.Contains("fire-toast-action"));
        openButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        openButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, opened);
        owner.Close();
    }
    [AvaloniaFact]
    public void ForegroundToast_RoutesSanitizedContentAndActivationOnUiThread()
    {
        var service = new UserIdentityDesktop.Service.SystemNotificationService();
        var sender = Guid.NewGuid();
        (string Title, string Body, Guid Sender)? observed = null;
        service.TryShowForeground = (title, message, id) => { observed = (title, message, id); return true; };
        Guid? activated = null; service.NotificationActivated += id => activated = id;
        service.ShowNotification("  Team update  ", "  Ready to review?  ", sender);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(("Team update", "Ready to review?", sender), observed);
        service.Activate(sender); Assert.Equal(sender, activated);
    }
    [AvaloniaFact]
    public async Task AnnouncementPublication_UsesChosenDatesCategoryAndEmailPreference()
    {
        var (vm, api, _) = Workspace();
        var person = new PersonSelection(new WorkplacePerson(Guid.NewGuid(), "Colleague")) { Selected = true }; vm.Audience.Add(person);
        vm.AnnouncementTitle = "Maintenance"; vm.AnnouncementBody = "Scheduled update"; vm.AnnouncementCategory = "Important action";
        vm.PublishDate = DateTimeOffset.Now.Date.AddDays(1); vm.PublishTime = new TimeSpan(10, 30, 0);
        vm.ExpiryDate = DateTimeOffset.Now.Date.AddDays(3); vm.ExpiryTime = new TimeSpan(17, 0, 0); vm.SendAnnouncementEmail = false;
        var expectedPublish = DateTime.SpecifyKind(vm.PublishDate.Value.Date.AddHours(10.5), DateTimeKind.Local).ToUniversalTime();
        var expectedExpiry = DateTime.SpecifyKind(vm.ExpiryDate.Value.Date.AddHours(17), DateTimeKind.Local).ToUniversalTime();
        await vm.PublishCommand.ExecuteAsync(null);
        await api.Received().PublishAsync(Arg.Is<AnnouncementWrite>(x => x.Category == "Important action" && x.PublishUtc == expectedPublish && x.ExpiresUtc == expectedExpiry && !x.SendEmail && x.Targets.Single().TargetId == person.Id), Arg.Any<CancellationToken>());
        Assert.Equal("Announcement scheduled.", vm.Feedback);
        await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task AnnouncementPublication_RejectsExpiryBeforePublication()
    {
        var (vm, api, _) = Workspace();
        vm.Audience.Add(new PersonSelection(new WorkplacePerson(Guid.NewGuid(), "Colleague")) { Selected = true });
        vm.AnnouncementTitle = "Update"; vm.AnnouncementBody = "Details";
        vm.PublishDate = DateTimeOffset.Now.Date.AddDays(3); vm.ExpiryDate = DateTimeOffset.Now.Date.AddDays(1);
        await vm.PublishCommand.ExecuteAsync(null);
        await api.DidNotReceive().PublishAsync(Arg.Any<AnnouncementWrite>(), Arg.Any<CancellationToken>());
        Assert.Equal("Expiry must be after publication.", vm.Feedback);
        await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public void MonthCalendar_MapsOvernightEventsAndClearsStaleChips()
    {
        var entries = new System.Collections.ObjectModel.ObservableCollection<CalendarItem>();
        var day = new DateTime(2026, 10, 19);
        entries.Add(new CalendarItem(Guid.NewGuid(), "Meeting", "Overnight planning", null,
            DateTime.SpecifyKind(day.AddHours(23), DateTimeKind.Local).ToUniversalTime(),
            DateTime.SpecifyKind(day.AddDays(1).AddHours(1), DateTimeKind.Local).ToUniversalTime(), Guid.NewGuid(), 0, 1));
        var view = new ScheduleCalendar { FocusDate = new DateTimeOffset(day), Items = entries };
        var owner = new Window { Width = 1000, Height = 800, Content = view };
        owner.Show(); Dispatcher.UIThread.RunJobs();
        var buttons = view.GetVisualDescendants().OfType<Button>().Where(x => x.Tag is DateTime).ToArray();
        Assert.Equal(42, buttons.Length);
        Assert.Equal(DayOfWeek.Sunday, ((DateTime)buttons[0].Tag!).DayOfWeek);
        foreach (var date in new[] { day, day.AddDays(1) })
        {
            var button = buttons.Single(x => Equals(x.Tag, date));
            Assert.Contains(button.GetVisualDescendants().OfType<TextBlock>(), x => x.Text?.Contains("Overnight planning") == true);
        }
        entries.Clear(); Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(), x => x.Text?.Contains("Overnight planning") == true);
        owner.Close();
    }
    [AvaloniaFact]
    public async Task SelectedCalendarDay_UpdatesAgendaWithoutNetworkReloadInsideMonth()
    {
        var (vm, api, _) = Workspace();
        var date = DateTime.Today;
        vm.RangeDate = new DateTimeOffset(date);
        vm.Calendar.Add(new CalendarItem(Guid.NewGuid(), "Meeting", "Daily standup", null,
            DateTime.SpecifyKind(date.AddHours(9), DateTimeKind.Local).ToUniversalTime(),
            DateTime.SpecifyKind(date.AddHours(10), DateTimeKind.Local).ToUniversalTime(), Guid.NewGuid(), 0, 1));
        Assert.Single(vm.DaySchedule);
        api.ClearReceivedCalls();
        var other = date.Day == 1 ? date.AddDays(1) : date.AddDays(-1);
        vm.RangeDate = new DateTimeOffset(other); Dispatcher.UIThread.RunJobs();
        Assert.Empty(vm.DaySchedule);
        await api.DidNotReceive().CalendarAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task NavigationMotion_RapidChangesCancelWithoutLeavingInvisibleContent()
    {
        var (vm, _, _) = Workspace(); vm.ReduceMotion = false;
        var view = new WorkspaceView { DataContext = vm, ShowReviewAsync = (_, _, _) => Task.FromResult(true) };
        var window = new Window { Width = 1200, Height = 850, Content = view };
        window.Show(); Dispatcher.UIThread.RunJobs();
        var tabs = view.FindControl<TabControl>("WorkspaceTabs")!;
        tabs.SelectedIndex = 1; tabs.SelectedIndex = 2; tabs.SelectedIndex = 0;
        await Task.Delay(350); Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, ((Control)((TabItem)tabs.SelectedItem!).Content!).Opacity);
        vm.ReduceMotion = true;
        window.Close(); await vm.DisposeAsync();
    }
    [AvaloniaTheory]
    [InlineData(FontWeight.Normal)] [InlineData(FontWeight.SemiBold)] [InlineData(FontWeight.Bold)]
    public void BundledTypography_UsesRequestedWeight(FontWeight weight)
    {
        Assert.True(FontManager.Current.TryGetGlyphTypeface(new Typeface(new FontFamily("fonts:Outfit#Outfit"), FontStyle.Normal, weight), out var face));
        Assert.Equal(weight, face!.Weight);
    }
    [AvaloniaFact]
    public async Task DeviceDiscovery_SelectsDefaultsWithoutStartingChecksOrCapture()
    {
        var (vm, _, media) = Workspace();
        media.GetDevicesAsync(Arg.Any<CancellationToken>()).Returns(new MediaDeviceCatalog(
            new[] { new MediaDeviceOption("", "System default"), new MediaDeviceOption("mic", "USB microphone") },
            new[] { new MediaDeviceOption("", "System default"), new MediaDeviceOption("speaker", "Headphones") },
            new[] { new MediaDeviceOption("camera", "USB camera") }));
        await vm.RefreshDevicesCommand.ExecuteAsync(null);
        Assert.Equal("camera", vm.CameraDevice); Assert.Equal("", vm.MicrophoneDevice);
        vm.SelectedSpeaker = vm.Speakers[1]; await media.Received().SetSpeakerAsync("speaker");
        await media.DidNotReceive().CheckCameraAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await media.DidNotReceive().CheckMicrophoneAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await media.DidNotReceive().SetCameraAsync(Arg.Any<bool>(), Arg.Any<MediaDevices>(), Arg.Any<CancellationToken>());
        await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task CallUsesIndependentWindow_AndClosingItLeavesAndHidesRemoteControlWithoutNativeShare()
    {
        var (vm, _, media) = Workspace(); vm.ReduceMotion = true; vm.InCall = true; vm.CallTitle = "Team call";
        var view = new WorkspaceView { DataContext = vm, ShowReviewAsync = (_, _, _) => Task.FromResult(true) };
        var owner = new Window { Width = 1200, Height = 850, Content = view };
        owner.Show(); Dispatcher.UIThread.RunJobs();
        var child = Assert.IsType<CallWindow>(view.ActiveCallWindow);
        Assert.Null(child.Owner); Assert.Empty(owner.OwnedWindows);
        owner.WindowState = WindowState.Minimized; Assert.Equal(WindowState.Normal, child.WindowState);
        Assert.False(vm.RemoteDesktopAvailable);
        Assert.False(child.GetVisualDescendants().OfType<Expander>().Single().IsVisible);
        using (var frame = child.CaptureRenderedFrame())
        {
            var output = Environment.GetEnvironmentVariable("FIREUSER_TEST_ARTIFACTS");
            if (output != null) { Directory.CreateDirectory(output); using var file = File.Create(Path.Combine(output, "call-window.png")); frame!.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
        }
        child.Close(); Dispatcher.UIThread.RunJobs();
        await media.Received().DisconnectAsync(); Assert.False(vm.InCall);
        owner.Close(); await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task CalendarSave_ForwardsBoundReviewAndSelectedPeople()
    {
        var (vm, api, _) = Workspace();
        var person = new PersonSelection(new WorkplacePerson(Guid.NewGuid(), "Colleague")) { Selected = true };
        vm.People.Add(person); vm.EventTitle = "Planning"; vm.IsMeeting = false;
        api.ReviewAsync(Arg.Any<CalendarWrite>(), Arg.Any<CancellationToken>()).Returns(new CalendarReview(new(), "review-token"));
        api.SaveCalendarAsync(Arg.Any<CalendarWrite>(), Arg.Any<CancellationToken>()).Returns(new SavedCalendar(Guid.NewGuid(), 1));
        await vm.ReviewEventCommand.ExecuteAsync(null); await vm.SaveEventCommand.ExecuteAsync(null);
        await api.Received().SaveCalendarAsync(Arg.Is<CalendarWrite>(x => x.ConflictToken == "review-token" && x.Participants.Contains(person.Id) && !x.AcknowledgeConflicts), Arg.Any<CancellationToken>());
        await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task RemoteControlUi_RequiresNativeSharingCapability()
    {
        var (vm, _, media) = Workspace(); vm.InCall = true;
        var native = new NativePeer("native", true, true, Guid.NewGuid().ToString("N"));
        media.RemoteControlChanged += Raise.Event<Action<RemoteControlStatus>>(new RemoteControlStatus(new[] { native }, "native", null, null));
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.CanRequestControl);
        vm.SelectedRemotePeer = vm.RemotePeers.Single(); Assert.True(vm.CanRequestControl);
        vm.SelectedRemotePeer = new NativePeer("browser", false, false, null); Assert.False(vm.CanRequestControl);
        Assert.True(vm.RemoteDesktopAvailable);
        Assert.Equal("native", vm.ControlRequester);
        var window = new Window { Width = 1200, Height = 900, Content = new WorkspaceView { DataContext = vm, ShowReviewAsync = (_, _, _) => Task.FromResult(true) } };
        window.Show(); Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        window.Close(); await vm.DisposeAsync();
    }
    private static (WorkspaceViewModel Vm, ICollaborationApi Api, INativeMediaSession Media) Workspace()
    {
        var auth = Substitute.For<IAuthService>(); auth.CurrentUser.Returns(new UserData { Id = Guid.NewGuid() });
        var chat = Substitute.For<IChatService>();
        var api = Substitute.For<ICollaborationApi>(); var media = Substitute.For<INativeMediaSession>();
        api.CatalogAsync(Arg.Any<CancellationToken>()).Returns(new WorkplaceCatalog(new()));
        api.CalendarAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(new List<CalendarItem>());
        api.AnnouncementsAsync(Arg.Any<CancellationToken>()).Returns(new List<Announcement>());
        var chatVm = new ChatViewModel(auth, chat, Substitute.For<IUserService>(), Substitute.For<IImageService>(), Substitute.For<IFileService>(), Substitute.For<INotificationService>());
        return (new WorkspaceViewModel(chatVm, api, media, auth, chat) { ConfirmActionAsync = (_, _) => Task.FromResult(true) }, api, media);
    }

    [AvaloniaFact]
    public async Task Save_RequiresReviewOfCurrentDraft_AndExplicitConflictAcknowledgement()
    {
        var (vm, api, _) = Workspace();
        vm.EventTitle = "Planning";
        var conflict = new CalendarConflict(Guid.NewGuid(), "Meeting", "Existing meeting", DateTime.UtcNow, DateTime.UtcNow.AddHours(1), false);
        api.ReviewAsync(Arg.Any<CalendarWrite>(), Arg.Any<CancellationToken>()).Returns(new CalendarReview(new() { conflict }, "bound-review"));
        await vm.ReviewEventCommand.ExecuteAsync(null);
        await vm.SaveEventCommand.ExecuteAsync(null);
        await api.DidNotReceive().SaveCalendarAsync(Arg.Any<CalendarWrite>(), Arg.Any<CancellationToken>());
        vm.AcknowledgeConflicts = true; vm.EventTitle = "Changed after review";
        await vm.SaveEventCommand.ExecuteAsync(null);
        await api.DidNotReceive().SaveCalendarAsync(Arg.Any<CalendarWrite>(), Arg.Any<CancellationToken>());
        Assert.Contains("Review", vm.Feedback);
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Leave_StopsLocalMedia_WhenServerCannotAcknowledge()
    {
        var (vm, api, media) = Workspace();
        vm.Chat.SelectedRecipient = new RecipientItemViewModel { ReceiverId = Guid.NewGuid(), ReceiverName = "Colleague" };
        vm.CallingEnabled = true;
        api.CreateCallAsync(Arg.Any<Guid>(), false, Arg.Any<CancellationToken>()).Returns(new CallSession("call-1", "audio", new(), null));
        api.CallTokenAsync("call-1", Arg.Any<CancellationToken>()).Returns(new MediaToken("wss://media.test", "token"));
        await vm.StartVoiceCommand.ExecuteAsync(null);
        Assert.True(vm.InCall); Assert.True(vm.MicrophoneEnabled); Assert.False(vm.CameraEnabled);
        api.LeaveCallAsync("call-1", Arg.Any<CancellationToken>()).Returns(Task.FromException(new HttpRequestException("Server unavailable")));
        await vm.LeaveCommand.ExecuteAsync(null);
        await media.Received().DisconnectAsync(); Assert.False(vm.InCall); Assert.Empty(vm.Videos);
        Assert.Contains("Server unavailable", vm.Feedback);
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public void VideoControl_RendersFrames_AndRejectsInvalidBuffer()
    {
        var pixels = Enumerable.Repeat(new byte[] { 255, 0, 0, 255 }, 16 * 16).SelectMany(x => x).ToArray();
        var video = new NativeVideoView { Width = 160, Height = 160, Frame = new VideoPicture("test", 16, 16, pixels) };
        var window = new Window { Width = 200, Height = 200, Content = video };
        window.Show();
        using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        video.Frame = new VideoPicture("malformed", 16, 16, new byte[3]);
        using var second = window.CaptureRenderedFrame(); Assert.NotNull(second);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public void Workspace_RendersEveryTab_InBothThemes(bool dark)
    {
        var (vm, _, _) = Workspace();
        vm.ReduceMotion = true; // Capture completed states; animation timing is not a layout assertion.
        var colleague = new RecipientItemViewModel { ReceiverId = Guid.NewGuid(), ReceiverName = "Maya Hassan", LastMessage = "Shall we review the design together?", LastSent = DateTime.UtcNow, Status = UserStatus.Online };
        vm.Chat.FilteredRecipients.Add(colleague); vm.Chat.SelectedRecipient = colleague;
        vm.Chat.Messages.Add(new ChatMessageItemViewModel(new ChatMessage { Text = "Shall we review the design together?", TimeSent = DateTime.UtcNow }, false, Substitute.For<IChatService>(), Substitute.For<IFileService>(), Substitute.For<IImageService>()));
        vm.Chat.Messages.Add(new ChatMessageItemViewModel(new ChatMessage { Text = "Absolutely. I’ll share my screen.", TimeSent = DateTime.UtcNow }, true, Substitute.For<IChatService>(), Substitute.For<IFileService>(), Substitute.For<IImageService>()));
        Avalonia.Application.Current!.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        vm.Calendar.Add(new CalendarItem(Guid.NewGuid(), "Meeting", "Product planning", "Team sync", DateTime.UtcNow, DateTime.UtcNow.AddHours(1), Guid.NewGuid(), 0, 1));
        vm.Announcements.Add(new Announcement(Guid.NewGuid(), "Welcome to Fire User", "Team updates appear here in real time.", "General", DateTime.UtcNow, DateTime.UtcNow.AddDays(7), true, true, 1, false));
        var view = new WorkspaceView { DataContext = vm, ShowReviewAsync = (_, _, _) => Task.FromResult(true) };
        var window = new Window { Width = 1200, Height = 850, Content = view };
        window.Show(); Dispatcher.UIThread.RunJobs();
        var tabs = view.FindControl<TabControl>("WorkspaceTabs")!;
        for (var index = 0; index < tabs.ItemCount; index++)
        {
            tabs.SelectedIndex = index; Dispatcher.UIThread.RunJobs();
            var expected = new[] { "Maya Hassan", "Join meeting", "Calendar", "Announcements", "Media & preferences", "Notifications" }[index];
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == expected && text.IsVisible);
            using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            var output = Environment.GetEnvironmentVariable("FIREUSER_TEST_ARTIFACTS");
            if (output != null) { Directory.CreateDirectory(output); using var file = File.Create(Path.Combine(output, $"workspace-{(dark ? "dark" : "light")}-{index}.png")); frame.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
        }
        tabs.SelectedIndex = 0;
        view.GetVisualDescendants().OfType<Button>().First(x => x.Tag?.ToString() == "0").Focus(Avalonia.Input.NavigationMethod.Tab); Dispatcher.UIThread.RunJobs();
        Assert.Equal(210, view.FindControl<Border>("NavigationRail")!.Bounds.Width);
        using (var expanded = window.CaptureRenderedFrame())
        {
            var output = Environment.GetEnvironmentVariable("FIREUSER_TEST_ARTIFACTS");
            if (output != null) { using var file = File.Create(Path.Combine(output, $"workspace-{(dark ? "dark" : "light")}-rail.png")); expanded!.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
        }
        window.Close();
    }
}
