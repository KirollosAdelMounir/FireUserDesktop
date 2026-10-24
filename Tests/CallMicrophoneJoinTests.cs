using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using UserIdentityDesktop.Core;
using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.ViewModels;
using UserIdentityDesktop.Views;
using Xunit;
namespace UserIdentityDesktop.Tests;
public sealed class CallMicrophoneJoinTests
{
    private static (WorkspaceViewModel Vm, ICollaborationApi Api, INativeMediaSession Media) Create()
    {
        var auth = Substitute.For<IAuthService>(); auth.CurrentUser.Returns(new UserData { Id = Guid.NewGuid() });
        var chat = Substitute.For<IChatService>(); var api = Substitute.For<ICollaborationApi>(); var media = Substitute.For<INativeMediaSession>();
        var vm = new ChatViewModel(auth, chat, Substitute.For<IUserService>(), Substitute.For<IImageService>(), Substitute.For<IFileService>(), Substitute.For<INotificationService>());
        api.CallTokenAsync("call", Arg.Any<CancellationToken>()).Returns(new MediaToken("wss://media.test", "token"));
        api.MeetingAsync("meeting", Arg.Any<CancellationToken>()).Returns(new MeetingDetail(Guid.NewGuid(), "meeting", "Team", true, "", 1, false, false));
        api.MeetingTokenAsync("meeting", Arg.Any<CancellationToken>()).Returns(new MediaToken("wss://media.test", "token"));
        return (new WorkspaceViewModel(vm, api, media, auth, chat) { ReduceMotion = true }, api, media);
    }
    [AvaloniaFact]
    public async Task RecoverableDisconnect_RejoinsSameCallWithFreshTokenWithoutLeavingOrResumingScreen()
    {
        var (vm, api, media) = Create();
        vm.Incoming = new IncomingCall("call", Guid.NewGuid(), "Caller", "audio", 1);
        await vm.AcceptCommand.ExecuteAsync(null);
        vm.ScreenEnabled = true;
        media.ConnectionChanged += Raise.Event<Action<MediaConnectionState>>(new MediaConnectionState("disconnected", "14", true));
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.InCall); Assert.False(vm.ConnectingCall); Assert.True(vm.MicrophoneEnabled); Assert.False(vm.ScreenEnabled);
        Assert.Contains("reconnected", vm.Feedback);
        await api.Received(2).CallTokenAsync("call", Arg.Any<CancellationToken>());
        await media.Received(2).ConnectAsync(Arg.Any<MediaToken>(), Arg.Any<CancellationToken>());
        await media.Received(1).DisconnectAsync();
        await api.DidNotReceive().LeaveCallAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await media.DidNotReceive().SetScreenAsync(true, Arg.Any<MediaDevices>(), Arg.Any<CancellationToken>());
        await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task LeavingDuringRecoveryCancelsThePendingTokenAndDoesNotRejoin()
    {
        var (vm, api, media) = Create();
        vm.Incoming = new IncomingCall("call", Guid.NewGuid(), "Caller", "audio", 1);
        await vm.AcceptCommand.ExecuteAsync(null);
        api.CallTokenAsync("call", Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await Task.Delay(Timeout.Infinite, call.ArgAt<CancellationToken>(1));
            return new MediaToken("wss://media.test", "unused");
        });
        media.ConnectionChanged += Raise.Event<Action<MediaConnectionState>>(new MediaConnectionState("disconnected", "14", true));
        Dispatcher.UIThread.RunJobs(); Assert.True(vm.ConnectingCall);
        await vm.LeaveCommand.ExecuteAsync(null);
        Assert.False(vm.InCall); Assert.False(vm.ConnectingCall);
        await media.Received(1).ConnectAsync(Arg.Any<MediaToken>(), Arg.Any<CancellationToken>());
        await api.Received(1).LeaveCallAsync("call", Arg.Any<CancellationToken>());
        await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task ServerRemoval_DoesNotAutomaticallyRejoinOrLeaveTheServerCall()
    {
        var (vm, api, media) = Create();
        vm.Incoming = new IncomingCall("call", Guid.NewGuid(), "Caller", "audio", 1);
        await vm.AcceptCommand.ExecuteAsync(null);
        media.ConnectionChanged += Raise.Event<Action<MediaConnectionState>>(new MediaConnectionState("disconnected", "4", false));
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.InCall); Assert.False(vm.MicrophoneEnabled); Assert.Contains("reason 4", vm.Feedback);
        await api.Received(1).CallTokenAsync("call", Arg.Any<CancellationToken>());
        await media.Received(1).ConnectAsync(Arg.Any<MediaToken>(), Arg.Any<CancellationToken>());
        await api.DidNotReceive().LeaveCallAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task SdkReconnection_PreservesCallAndDoesNotCreateAnotherRoom()
    {
        var (vm, _, media) = Create();
        vm.Incoming = new IncomingCall("call", Guid.NewGuid(), "Caller", "audio", 1);
        await vm.AcceptCommand.ExecuteAsync(null);
        media.ConnectionChanged += Raise.Event<Action<MediaConnectionState>>(new MediaConnectionState("reconnecting"));
        Dispatcher.UIThread.RunJobs(); Assert.True(vm.InCall); Assert.True(vm.ConnectingCall);
        media.ConnectionChanged += Raise.Event<Action<MediaConnectionState>>(new MediaConnectionState("connected"));
        Dispatcher.UIThread.RunJobs(); Assert.True(vm.InCall); Assert.False(vm.ConnectingCall);
        await media.Received(1).ConnectAsync(Arg.Any<MediaToken>(), Arg.Any<CancellationToken>());
        await media.DidNotReceive().DisconnectAsync();
        await vm.DisposeAsync();
    }
    [AvaloniaTheory]
    [InlineData("audio")] [InlineData("video")]
    public async Task AnswerDirectCall_EnablesMicrophoneRegardlessOfMeetingPreference(string type)
    {
        var (vm, _, media) = Create(); vm.JoinMeetingMuted = true;
        vm.Incoming = new IncomingCall("call", Guid.NewGuid(), "Caller", type, 1);
        await vm.AcceptCommand.ExecuteAsync(null);
        Assert.True(vm.InCall); Assert.True(vm.MicrophoneEnabled); Assert.False(vm.CameraEnabled); Assert.Null(vm.Incoming);
        Assert.True(vm.Videos.Single(x => x.Identity == "You").MicrophoneOn);
        await media.Received(1).SetMicrophoneAsync(true, Arg.Any<MediaDevices>(), Arg.Any<CancellationToken>());
        await vm.DisposeAsync();
    }
    [AvaloniaTheory]
    [InlineData(true, false)] [InlineData(false, false)] [InlineData(true, true)] [InlineData(false, true)]
    public async Task MeetingJoin_HonorsMutedChoiceForRegularAndIncomingMeetings(bool muted, bool incoming)
    {
        var (vm, _, media) = Create(); vm.JoinMeetingMuted = muted; vm.MeetingCode = "meeting";
        if (incoming) { vm.Incoming = new IncomingCall("call", Guid.NewGuid(), "Caller", "audio", 1, MeetingLocator: "meeting"); await vm.AcceptCommand.ExecuteAsync(null); }
        else await vm.JoinMeetingCommand.ExecuteAsync(null);
        Assert.True(vm.InCall); Assert.Equal(!muted, vm.MicrophoneEnabled);
        if (muted) await media.DidNotReceive().SetMicrophoneAsync(Arg.Any<bool>(), Arg.Any<MediaDevices>(), Arg.Any<CancellationToken>());
        else await media.Received(1).SetMicrophoneAsync(true, Arg.Any<MediaDevices>(), Arg.Any<CancellationToken>());
        await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task CalendarMeetingJoinsDirectlyUsingTheSelectedMicrophonePreference()
    {
        var (vm, api, _) = Create();
        vm.SelectedCalendar = new CalendarItem(Guid.NewGuid(), "Meeting", "Team", null, DateTime.UtcNow, DateTime.UtcNow.AddHours(1), Guid.NewGuid(), null, 1, MeetingLink: "https://fire.test/meeting/meeting");
        await vm.JoinSelectedMeetingCommand.ExecuteAsync(null);
        Assert.Equal("meeting",vm.MeetingCode);Assert.True(vm.InCall);
        await api.Received(1).MeetingTokenAsync("meeting",Arg.Any<CancellationToken>());
        Assert.Equal(!vm.JoinMeetingMuted,vm.MicrophoneEnabled);
        await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task AnswerMicrophoneFailure_KeepsCallConnectedAndShowsMutedDeviceError()
    {
        var (vm, api, media) = Create();
        media.SetMicrophoneAsync(true, Arg.Any<MediaDevices>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new InvalidOperationException("Microphone permission denied")));
        vm.Incoming = new IncomingCall("call", Guid.NewGuid(), "Caller", "audio", 1);
        await vm.AcceptCommand.ExecuteAsync(null);
        Assert.True(vm.InCall); Assert.False(vm.MicrophoneEnabled); Assert.Null(vm.Incoming);
        Assert.Contains("Joined with microphone muted", vm.Feedback); Assert.Contains("permission denied", vm.Feedback);
        await api.DidNotReceive().LeaveCallAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task MeetingMicrophoneCheckbox_BindsChoiceAndIsHiddenForDirectCallAlerts()
    {
        var (vm, _, _) = Create(); vm.Incoming = new IncomingCall("call", Guid.NewGuid(), "Caller", "audio", 1);
        var alert = new IncomingCallWindow(vm); alert.Show(); Dispatcher.UIThread.RunJobs();
        var choice = alert.GetVisualDescendants().OfType<CheckBox>().Single(); Assert.False(choice.IsVisible);
        vm.Incoming = vm.Incoming with { MeetingLocator = "meeting" }; Dispatcher.UIThread.RunJobs();
        Assert.True(choice.IsVisible); Assert.True(choice.IsChecked); choice.IsChecked = false; Assert.False(vm.JoinMeetingMuted);
        alert.Close(); await vm.DisposeAsync();
    }
}
