using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using UserIdentityDesktop.Core;
using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.Data;
using UserIdentityDesktop.ViewModels;
using UserIdentityDesktop.Views;
using Xunit;
namespace UserIdentityDesktop.Tests;
public sealed class MeetingFlowTests
{
    [AvaloniaFact]
    public async Task PendingInvitationJoinsAndEmailsGuestDirectlyWithoutRespondingToRsvp()
    {
        var auth=Substitute.For<IAuthService>();auth.CurrentUser.Returns(new UserData{Id=Guid.NewGuid()});var api=Substitute.For<ICollaborationApi>();var chat=Substitute.For<IChatService>();var media=Substitute.For<INativeMediaSession>();
        api.MeetingAsync("meeting",Arg.Any<CancellationToken>()).Returns(new MeetingDetail(Guid.NewGuid(),"meeting","Team",true,"",1,false,true));api.MeetingTokenAsync("meeting",Arg.Any<CancellationToken>()).Returns(new MediaToken("wss://media.invalid","token"));
        var chatVm=new ChatViewModel(auth,chat,Substitute.For<IUserService>(),Substitute.For<IImageService>(),Substitute.For<IFileService>(),Substitute.For<INotificationService>());
        var vm=new WorkspaceViewModel(chatVm,api,media,auth,chat){MeetingCode="meeting",JoinMeetingMuted=true,ReduceMotion=true};
        await vm.JoinMeetingCommand.ExecuteAsync(null);Assert.True(vm.InCall);Assert.False(vm.MicrophoneEnabled);await chat.DidNotReceive().RespondToMeetingInvitationAsync(Arg.Any<string>(),Arg.Any<Guid>(),Arg.Any<bool>());
        vm.CallInviteEmail=" guest@example.com ";await vm.InviteCallEmailCommand.ExecuteAsync(null);await api.Received(1).InviteMeetingEmailsAsync("meeting",Arg.Is<IReadOnlyList<string>>(x=>x.SequenceEqual(new[]{"guest@example.com"})),Arg.Any<CancellationToken>());Assert.Equal("",vm.CallInviteEmail);Assert.Equal("Invitation sent.",vm.Feedback);
        vm.CallInviteEmail="invalid";await vm.InviteCallEmailCommand.ExecuteAsync(null);Assert.Contains("valid email",vm.Feedback);await api.Received(1).InviteMeetingEmailsAsync(Arg.Any<string>(),Arg.Any<IReadOnlyList<string>>(),Arg.Any<CancellationToken>());
        if(Environment.GetEnvironmentVariable("FIREUSER_TEST_ARTIFACTS")!=null){NativeAppearance.Apply(0);vm.CallInviteEmail="guest@example.com";vm.Feedback="Invitation sent.";vm.ShowCallMembers=true;var call=new CallWindow{DataContext=vm};call.Show();Save(call,"meeting-direct-invite.png");call.CloseAfterCall();}
        await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task ReconnectingBannerKeepsNavigationUsableAndRecoversTheQueuedAction()
    {
        var queue=new BackendActionQueue();queue.SetAccount("server|user");queue.SetConnected(false);var auth=Substitute.For<IAuthService>();auth.CurrentUser.Returns(new UserData{Id=Guid.NewGuid()});auth.Actions.Returns(queue);var chat=Substitute.For<IChatService>();var api=Substitute.For<ICollaborationApi>();
        var chatVm=new ChatViewModel(auth,chat,Substitute.For<IUserService>(),Substitute.For<IImageService>(),Substitute.For<IFileService>(),Substitute.For<INotificationService>());var vm=new WorkspaceViewModel(chatVm,api,Substitute.For<INativeMediaSession>(),auth,chat){ReduceMotion=true};
        var window=new Window{Width=1100,Height=760,Content=new WorkspaceView{DataContext=vm}};window.Show();var sent=0;var action=queue.RunAsync(_=>Task.FromResult(++sent));Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsEnabled);Assert.True(vm.ShowConnectionBanner);Assert.Contains("1 action(s) queued",vm.ConnectionBannerText);Assert.Contains(window.GetVisualDescendants().OfType<Button>(),x=>x.IsEffectivelyEnabled&&Avalonia.Automation.AutomationProperties.GetName(x)=="Media settings");
        Save(window,"desktop-reconnecting-queue.png");
        queue.SetConnected(true);await action;chat.ConnectionStateChanged+=Raise.Event<Action<Microsoft.AspNetCore.SignalR.Client.HubConnectionState>>(Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected);Dispatcher.UIThread.RunJobs();Assert.Equal(1,sent);Assert.False(vm.ShowConnectionBanner);window.Close();await vm.DisposeAsync();
    }
    private static void Save(Window window,string file){if(Environment.GetEnvironmentVariable("FIREUSER_TEST_ARTIFACTS") is not {} folder)return;Directory.CreateDirectory(folder);Dispatcher.UIThread.RunJobs();using var frame=window.CaptureRenderedFrame();Assert.NotNull(frame);using var output=File.Create(Path.Combine(folder,file));frame.Save(output,new Avalonia.Media.Imaging.PngBitmapEncoderOptions());}
}
