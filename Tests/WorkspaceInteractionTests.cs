using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using UserIdentityDesktop.Core;
using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.ViewModels;
using UserIdentityDesktop.Views;
using Xunit;

namespace UserIdentityDesktop.Tests;
public sealed class WorkspaceInteractionTests
{
    private static (WorkspaceViewModel Vm, ICollaborationApi Api, IChatService Chat) Create()
    {
        var auth = Substitute.For<IAuthService>(); auth.CurrentUser.Returns(new UserData { Id = Guid.NewGuid() });
        var api = Substitute.For<ICollaborationApi>(); var chat = Substitute.For<IChatService>();
        var vm = new ChatViewModel(auth, chat, Substitute.For<IUserService>(), Substitute.For<IImageService>(), Substitute.For<IFileService>(), Substitute.For<INotificationService>());
        var self = auth.CurrentUser!.Id;
        api.CatalogAsync(Arg.Any<CancellationToken>()).Returns(new WorkplaceCatalog(new() { new(self, "Me") }));
        api.CalendarAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(new List<CalendarItem>());
        api.AnnouncementsAsync(Arg.Any<CancellationToken>()).Returns(new List<Announcement>());
        api.VacationsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new VacationPage(new(), false, 1));
        return (new WorkspaceViewModel(vm, api, Substitute.For<INativeMediaSession>(), auth, chat) { ReduceMotion = true }, api, chat);
    }
    [AvaloniaFact]
    public async Task VacationActions_RequireTheRightOwnerReportingDistanceAndState()
    {
        var (vm, api, _) = Create(); var self = vm.Chat.CurrentUser.Id; var child = Guid.NewGuid(); var grandchild = Guid.NewGuid();
        api.CatalogAsync(Arg.Any<CancellationToken>()).Returns(new WorkplaceCatalog(new() { new(self,"Me"), new(child,"Direct report",ReportingDistance:1), new(grandchild,"Indirect report",ReportingDistance:2) })); await vm.RefreshAsync();
        vm.SelectedVacation = new(Guid.NewGuid(), self, 0, "Family", 2, 1);
        Assert.Equal("Pending", vm.SelectedVacation.StateLabel); Assert.False(vm.CanApproveVacation); Assert.True(vm.CanWithdrawVacation);
        await vm.DecideVacationCommand.ExecuteAsync("Approve"); await api.DidNotReceive().DecideVacationAsync(Arg.Any<VacationRequest>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        vm.SelectedVacation = vm.SelectedVacation with { UserId = child }; Assert.True(vm.CanApproveVacation); Assert.False(vm.CanWithdrawVacation);
        var request = vm.SelectedVacation; await vm.DecideVacationCommand.ExecuteAsync("Approve"); await api.Received(1).DecideVacationAsync(request, "Approve", Arg.Any<CancellationToken>());
        vm.SelectedVacation = request with { UserId = grandchild }; Assert.False(vm.CanApproveVacation);
        vm.SelectedVacation = request with { UserId = self, State = 1 }; Assert.True(vm.CanRequestVacationCancellation); Assert.False(vm.CanWithdrawVacation);
        vm.SelectedVacation = request with { State = 4 }; Assert.True(vm.CanConfirmVacationCancellation); Assert.False(vm.CanApproveVacation);
        vm.SelectedVacation = request with { State = 2 }; Assert.False(vm.CanConfirmVacationCancellation); Assert.False(vm.CanApproveVacation);
        vm.Chat.UnregisterEvents(); await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task VacationQueue_RecoversOnWorkplaceEventsWithoutPolling()
    {
        var (vm, api, chat) = Create(); var child = Guid.NewGuid(); var request = new VacationRequest(Guid.NewGuid(), child, 0, "Family", 2, 1);
        vm.SelectedCalendarTab = 1; Dispatcher.UIThread.RunJobs(); api.ClearReceivedCalls();
        api.VacationsAsync(1, Arg.Any<CancellationToken>()).Returns(new VacationPage(new() { request }, false, 1));
        chat.WorkplaceChanged += Raise.Event<Action>(); Dispatcher.UIThread.RunJobs(); Assert.Equal(request, Assert.Single(vm.Vacations));
        await api.Received(1).VacationsAsync(1, Arg.Any<CancellationToken>());
        Dispatcher.UIThread.RunJobs(); await api.Received(1).VacationsAsync(1, Arg.Any<CancellationToken>());
        vm.Chat.UnregisterEvents(); await vm.DisposeAsync();
    }
    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task AllCreationSurfaces_OpenOwnedWindows_AndShowSuccessAfterSavingAndRefresh(bool light)
    {
        var (vm, api, _) = Create(); vm.SelectedWorkspaceTab = 2;
        var view = new WorkspaceView { DataContext = vm }; var messages = new List<string>(); view.ShowSubmissionMessageAsync = (_, text) => { messages.Add(text); return Task.CompletedTask; };
        var owner = new Window { Content = view, Width = 1200, Height = 850, RequestedThemeVariant = light ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark }; owner.Show(); Dispatcher.UIThread.RunJobs();
        foreach (var tab in new[] { 0, 2 })
        {
            vm.SelectedCalendarTab = tab; Dispatcher.UIThread.RunJobs(); vm.ShowCalendarEditor = true; Dispatcher.UIThread.RunJobs();
            var editor = Assert.Single(view.EditorWindows); Assert.Equal(WorkspaceFormKind.Calendar, editor.Kind); Assert.Same(owner, editor.Owner);
            Save(editor, $"calendar-form-{tab}-{light}.png"); await editor.CloseAnimatedAsync();
        }
        vm.EventTitle = "New appointment"; vm.IsMeeting = false; vm.ShowCalendarEditor = true;
        api.ReviewAsync(Arg.Any<CalendarWrite>(), Arg.Any<CancellationToken>()).Returns(new CalendarReview(new(), "review"));
        api.SaveCalendarAsync(Arg.Any<CalendarWrite>(), Arg.Any<CancellationToken>()).Returns(new SavedCalendar(Guid.NewGuid(), 1));
        await vm.SaveEventCommand.ExecuteAsync(null); Assert.Empty(messages); Assert.Single(view.EditorWindows);
        await vm.ReviewEventCommand.ExecuteAsync(null); await vm.SaveEventCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs(); Assert.Empty(view.EditorWindows); Assert.Single(messages);
        vm.SelectedCalendarTab = 1; Dispatcher.UIThread.RunJobs(); vm.ShowVacationEditor = true; Dispatcher.UIThread.RunJobs();
        var vacationWindow = Assert.Single(view.EditorWindows); Assert.Equal(WorkspaceFormKind.Vacation, vacationWindow.Kind); Save(vacationWindow, $"vacation-form-{light}.png");
        api.PreviewVacationAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), 1, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new VacationPreview(new(), "vacation-review", new()));
        await vm.PreviewVacationCommand.ExecuteAsync(null); await vm.SubmitVacationCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs(); Assert.Empty(view.EditorWindows); Assert.Equal(2, messages.Count); Assert.Contains("Pending manager approval", messages[1]);
        vm.SelectedWorkspaceTab = 3; Dispatcher.UIThread.RunJobs(); vm.ShowAnnouncementEditor = true; Dispatcher.UIThread.RunJobs();
        Save(Assert.Single(view.EditorWindows), $"announcement-form-{light}.png"); vm.AnnouncementTitle = "Notice"; vm.AnnouncementBody = "Updated schedule"; vm.ExpiryDate = DateTimeOffset.Now.AddDays(3); vm.Audience.Add(new(new(Guid.NewGuid(), "Audience")) { Selected = true });
        await vm.PublishCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs(); Assert.Empty(view.EditorWindows); Assert.Equal(3, messages.Count);
        await api.Received().CalendarAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>()); await api.Received().AnnouncementsAsync(Arg.Any<CancellationToken>());
        owner.Close(); vm.Chat.UnregisterEvents(); await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task Submission_WaitsForAnInProgressSnapshotAndItsRecoveryBeforeConfirming()
    {
        var (vm, api, _) = Create(); vm.EventTitle = "New appointment"; vm.IsMeeting = false;
        api.ReviewAsync(Arg.Any<CalendarWrite>(), Arg.Any<CancellationToken>()).Returns(new CalendarReview(new(), "review"));
        api.SaveCalendarAsync(Arg.Any<CalendarWrite>(), Arg.Any<CancellationToken>()).Returns(new SavedCalendar(Guid.NewGuid(), 1));
        await vm.ReviewEventCommand.ExecuteAsync(null);
        var pending = new TaskCompletionSource<List<CalendarItem>>(); var snapshots = 0; var confirmations = 0;
        api.CalendarAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(_ => ++snapshots == 1 ? pending.Task : Task.FromResult(new List<CalendarItem>()));
        vm.SubmissionCompleted += (_, _) => confirmations++;
        var initial = vm.RefreshAsync(); var save = vm.SaveEventCommand.ExecuteAsync(null);
        Assert.False(save.IsCompleted); Assert.Equal(0, confirmations);
        pending.SetResult(new()); await Task.WhenAll(initial, save); Assert.Equal(2, snapshots); Assert.Equal(1, confirmations);
        vm.Chat.UnregisterEvents(); await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public void ReceiverAvatar_HasACircularClip()
    {
        var (vm, _, _) = Create(); vm.Chat.SelectedRecipient = new() { ReceiverId = Guid.NewGuid(), ReceiverName = "Peer" };
        var view = new ChatView { DataContext = vm.Chat }; var owner = new Window { Content = view }; owner.Show(); Dispatcher.UIThread.RunJobs();
        var avatar = view.FindControl<Border>("ReceiverAvatar")!; Assert.True(avatar.ClipToBounds); Assert.IsType<EllipseGeometry>(avatar.Clip); Assert.Equal(new CornerRadius(20), avatar.CornerRadius); owner.Close(); vm.Chat.UnregisterEvents();
    }
    private static void Save(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("FIREUSER_TEST_ARTIFACTS") is not {} directory) return;
        using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window); using var file = File.Create(Path.Combine(directory, name)); frame!.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
