using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using UserIdentityDesktop.Core;
using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.Service.Media;
using UserIdentityDesktop.ViewModels;
using UserIdentityDesktop.Views;
using Xunit;
namespace UserIdentityDesktop.Tests;
public sealed class CallStageInteractionTests
{
    [AvaloniaFact]
    public async Task FullscreenFooter_RemainsOutsideSharedVideoAndUsesRemoteIcon()
    {
        var (vm,_)=Create();var screen=new VideoTileViewModel("one · screen · track"){Frame=new("one · screen · track",16,9,new byte[16*9*4])};vm.Videos.Add(screen);vm.FocusedVideo=screen;
        var window=new CallWindow{DataContext=vm,Width=1200,Height=800};window.Show();Dispatcher.UIThread.RunJobs();
        window.FindControl<Button>("FullscreenButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();
        var footer=window.FindControl<Border>("FullscreenControls")!;var video=window.FindControl<ContentControl>("FocusedStage")!.GetVisualDescendants().OfType<NativeVideoView>().Single();
        Assert.True(footer.IsVisible);Assert.True(footer.Bounds.Height>=76);
        Assert.True(video.TranslatePoint(new Point(0,video.Bounds.Height),window)!.Value.Y<=footer.TranslatePoint(default,window)!.Value.Y);
        Assert.Equal("Remote",DesktopIconography.Glyph("Request remote control"));Assert.Equal("Remote",DesktopIconography.Glyph("Share screen"));
        var point=footer.TranslatePoint(new Point(footer.Bounds.Width/2,footer.Bounds.Height-2),window)!.Value;
        window.MouseMove(point);Dispatcher.UIThread.RunJobs();Assert.Equal(1,window.FindControl<Border>("FullscreenToolbar")!.Opacity);
        window.RequestedThemeVariant=Avalonia.Styling.ThemeVariant.Light;Dispatcher.UIThread.RunJobs();Save(window,"remote-fullscreen-footer.png");
        window.RequestedThemeVariant=Avalonia.Styling.ThemeVariant.Dark;Dispatcher.UIThread.RunJobs();Save(window,"remote-fullscreen-footer-dark.png");
        window.CloseAfterCall();Dispatcher.UIThread.RunJobs();await vm.DisposeAsync();
    }
    [Theory]
    [InlineData(true,true,true,true,true)][InlineData(false,true,true,true,false)][InlineData(true,false,true,true,false)][InlineData(true,true,false,true,false)][InlineData(true,true,true,false,false)]
    public void KeyboardRouting_RequiresForegroundFocusAndGrant(bool active,bool foreground,bool focused,bool grant,bool expected)=>Assert.Equal(expected,RemoteKeyboardCapture.CanRoute(active,foreground,focused,grant));
    [AvaloniaFact]
    public void RemoteKeys_ForwardWindowsAndClipboardGesturesOnlyWhenEnabled()
    {
        var inputs=new List<RemoteInput>();var view=new NativeVideoView{RemoteControlEnabled=true,Frame=new("peer · screen · track",2,2,new byte[16]),InputCommand=new CommunityToolkit.Mvvm.Input.RelayCommand<RemoteInput>(x=>inputs.Add(x!))};
        Assert.Equal(227,RemoteKeyboardCapture.MapKey(91));Assert.Equal(231,RemoteKeyboardCapture.MapKey(92));Assert.Equal(43,RemoteKeyboardCapture.MapKey(9));
        view.ForwardKey(227,true,false);view.ForwardKey(227,false,false);view.ForwardKey(6,true,true);view.ForwardKey(25,true,true);
        Assert.Equal(new[]{2,2,4,5},inputs.Select(x=>x.Kind));Assert.Equal(227,inputs[0].Code);Assert.False(inputs[1].Down);
        view.RemoteControlEnabled=false;view.ForwardKey(227,true,false);Assert.Equal(4,inputs.Count);
    }
    [AvaloniaFact]
    public async Task IncomingControlRequest_ShowsIndependentToast_AndApproveRejectAnswerExactRequester()
    {
        var (vm, media) = Create(); var window = new CallWindow { DataContext = vm }; window.Show();
        var peer = new NativePeer("one", true, true, Guid.NewGuid().ToString("N"));
        var request = new RemoteControlStatus(new[] { peer }, "one", null, null) { RequestExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30) };
        media.RemoteControlChanged += Raise.Event<Action<RemoteControlStatus>>(request); Dispatcher.UIThread.RunJobs();
        var toast = Assert.IsType<RemoteControlRequestWindow>(window.ControlRequestToast);
        Assert.True(toast.IsVisible); Assert.True(toast.Topmost); Assert.False(toast.ShowActivated);
        Assert.Contains("Alice Example", toast.GetVisualDescendants().OfType<TextBlock>().Select(x => x.Text).First(x => x?.Contains("requests keyboard") == true));
        Assert.Contains("Automatically rejects in", toast.FindControl<TextBlock>("Countdown")!.Text);
        toast.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark; Dispatcher.UIThread.RunJobs(); Save(toast, "remote-control-request-dark.png");
        toast.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light; Dispatcher.UIThread.RunJobs(); Save(toast, "remote-control-request-light.png");
        var approve = toast.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, "Approve"));
        Assert.Same(vm.AllowControlCommand, approve.Command); await vm.AllowControlCommand.ExecuteAsync(null);
        await media.Received(1).AnswerControlAsync("one", true);
        media.RemoteControlChanged += Raise.Event<Action<RemoteControlStatus>>(request with { RequestedBy = null, Controller = "one" }); Dispatcher.UIThread.RunJobs();
        Assert.False(toast.IsVisible);
        media.RemoteControlChanged += Raise.Event<Action<RemoteControlStatus>>(request); Dispatcher.UIThread.RunJobs();
        var secondToast = Assert.IsType<RemoteControlRequestWindow>(window.ControlRequestToast);
        var reject = secondToast.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, "Reject"));
        Assert.Same(vm.DenyControlCommand, reject.Command); await vm.DenyControlCommand.ExecuteAsync(null); await media.Received(1).AnswerControlAsync("one", false);
        window.CloseAfterCall(); Dispatcher.UIThread.RunJobs(); Assert.False(secondToast.IsVisible); await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task LinuxEscapeStopsForwardingAndLeavesFullscreenWhileRevokeIsBlocked()
    {
        if(!OperatingSystem.IsLinux())return;
        var (vm,media)=Create();vm.InCall=true;
        var track="one · screen · track";
        vm.Videos.Add(new(track,media.SendRemoteInput){Frame=new(track,16,9,new byte[16*9*4])});
        var window=new CallWindow{DataContext=vm};window.Show();
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        media.RevokeControlAsync().Returns(_=>{started.TrySetResult();return release.Task;});
        try
        {
            media.RemoteControlChanged+=Raise.Event<Action<RemoteControlStatus>>(new RemoteControlStatus(new[]{new NativePeer("one",true,true,"scope")},null,null,"one"));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(WindowState.FullScreen,window.WindowState);
            var video=window.FindControl<ContentControl>("FocusedStage")!.GetVisualDescendants().OfType<NativeVideoView>().Single();
            video.ForwardKey(4,true,false);media.Received(1).SendRemoteInput(Arg.Is<RemoteInput>(x=>x.Code==4));
            video.RaiseEvent(new KeyEventArgs{RoutedEvent=InputElement.KeyDownEvent,Key=Key.Escape});
            Assert.NotEqual(WindowState.FullScreen,window.WindowState);
            Assert.False(release.Task.IsCompleted);
            video.ForwardKey(5,true,false);media.DidNotReceive().SendRemoteInput(Arg.Is<RemoteInput>(x=>x.Code==5));
            // A queued latency/status notification cannot re-enable a paused grant.
            media.RemoteControlChanged+=Raise.Event<Action<RemoteControlStatus>>(new RemoteControlStatus(new[]{new NativePeer("one",true,true,"scope")},null,null,"one"));
            Dispatcher.UIThread.RunJobs();Assert.False(vm.Videos.Single(x=>x.Identity==track).CanControl);
            Assert.NotEqual(WindowState.FullScreen,window.WindowState);
            Assert.Same(started.Task,await Task.WhenAny(started.Task,Task.Delay(3000,TestContext.Current.CancellationToken)));
        }
        finally{release.TrySetResult();window.CloseAfterCall();Dispatcher.UIThread.RunJobs();await vm.DisposeAsync();}
    }
    [AvaloniaFact]
    public async Task LinuxPointerEchoDoesNotResendTheSameSourcePixel()
    {
        if(!OperatingSystem.IsLinux())return;
        var inputs=new List<RemoteInput>();
        var video=new NativeVideoView{Width=200,Height=200,RemoteControlEnabled=true,Frame=new("peer · screen · track",2,2,new byte[16]),InputCommand=new CommunityToolkit.Mvvm.Input.RelayCommand<RemoteInput>(x=>inputs.Add(x!))};
        var window=new Window{Width=200,Height=200,Content=video};window.Show();Dispatcher.UIThread.RunJobs();
        try
        {
            using var rendered=window.CaptureRenderedFrame();
            window.MouseMove(new Point(20,20));Dispatcher.UIThread.RunJobs();
            Assert.Single(inputs,x=>x.Kind==0);
            await Task.Delay(25,TestContext.Current.CancellationToken);
            window.MouseMove(new Point(21,21));Dispatcher.UIThread.RunJobs();
            Assert.Single(inputs,x=>x.Kind==0);
            await Task.Delay(25,TestContext.Current.CancellationToken);
            window.MouseMove(new Point(150,150));Dispatcher.UIThread.RunJobs();
            Assert.Equal(2,inputs.Count(x=>x.Kind==0));Assert.True(inputs.Last().X>.5);
        }
        finally{window.Close();Dispatcher.UIThread.RunJobs();}
    }
    [AvaloniaFact]
    public async Task LinuxVideoDeliveryYieldsToQueuedKeyboardInput()
    {
        if(!OperatingSystem.IsLinux())return;
        var (vm,media)=Create();vm.InCall=true;
        var inputRan=false;
        media.VideoReceived+=Raise.Event<Action<VideoPicture>>(new VideoPicture("one · screen · new",16,9,new byte[16*9*4]));
        Dispatcher.UIThread.Post(()=>{Assert.DoesNotContain(vm.Videos,x=>x.Identity=="one · screen · new");inputRan=true;},DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs();
        Assert.True(inputRan);Assert.NotNull(Assert.Single(vm.Videos,x=>x.Identity=="one · screen · new").Frame);
        await vm.DisposeAsync();
    }
    private static (WorkspaceViewModel Vm,INativeMediaSession Media) Create()
    {
        var auth=Substitute.For<IAuthService>();auth.CurrentUser.Returns(new UserData{Id=Guid.NewGuid()});var chat=Substitute.For<IChatService>();var media=Substitute.For<INativeMediaSession>();
        var vm=new ChatViewModel(auth,chat,Substitute.For<IUserService>(),Substitute.For<IImageService>(),Substitute.For<IFileService>(),Substitute.For<INotificationService>());
        var workspace=new WorkspaceViewModel(vm,Substitute.For<ICollaborationApi>(),media,auth,chat){ReduceMotion=true};
        workspace.Videos.Add(new("one"){Name="Alice Example"});workspace.Videos.Add(new("two"){Name="Bob Example"});workspace.Videos.Add(new("three"){Name="Dina Example"});return(workspace,media);
    }
    [AvaloniaFact]
    public async Task ParticipantFocus_KeepsOtherPeopleVisibleAndSecondClickRestoresGallery()
    {
        var (vm,_)=Create();var window=new CallWindow{DataContext=vm};window.Show();Dispatcher.UIThread.RunJobs();var alice=vm.Videos[0];
        vm.FocusVideoCommand.Execute(alice);Dispatcher.UIThread.RunJobs();Assert.Same(alice,vm.FocusedVideo);Assert.Equal(new[]{"two","three"},vm.GalleryVideos.Select(x=>x.Identity));
        var gallery=window.FindControl<ScrollViewer>("ParticipantGallery")!;Assert.True(gallery.IsVisible);Assert.Equal(240,gallery.Bounds.Width);Assert.Equal(1,vm.GalleryColumns);
        Assert.Contains(gallery.GetVisualDescendants().OfType<TextBlock>(),x=>x.Text=="Bob Example");Assert.Contains(gallery.GetVisualDescendants().OfType<TextBlock>(),x=>x.Text=="Dina Example");
        Save(window,"native-participant-focus.png");
        vm.FocusVideoCommand.Execute(alice);Dispatcher.UIThread.RunJobs();Assert.Null(vm.FocusedVideo);Assert.Equal(3,vm.GalleryVideos.Length);
        vm.FocusVideoCommand.Execute(alice);vm.ClearVideoFocusCommand.Execute(null);Assert.Null(vm.FocusedVideo);
        window.CloseAfterCall();Dispatcher.UIThread.RunJobs();await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task OnlyExplicitScreenShareFullscreen_IsAllowedAndBottomControlsHandleHoverAndFocus()
    {
        var (vm,media)=Create();var window=new CallWindow{DataContext=vm,Width=1200,Height=800};window.Show();Dispatcher.UIThread.RunJobs();
        vm.FocusVideoCommand.Execute(vm.Videos[0]);window.WindowState=WindowState.FullScreen;Dispatcher.UIThread.RunJobs();Assert.NotEqual(WindowState.FullScreen,window.WindowState);
        window.RaiseEvent(new KeyEventArgs{RoutedEvent=InputElement.KeyDownEvent,Key=Key.F11});Assert.NotEqual(WindowState.FullScreen,window.WindowState);
        var screen=new VideoTileViewModel("one · screen · track"){Name="Alice's screen",Frame=new("one · screen · track",2,2,new byte[16])};vm.Videos.Add(screen);vm.FocusedVideo=screen;Dispatcher.UIThread.RunJobs();
        window.FindControl<Button>("FullscreenButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();Assert.Equal(WindowState.FullScreen,window.WindowState);
        var controls=window.FindControl<Border>("FullscreenControls")!;var toolbar=window.FindControl<Border>("FullscreenToolbar")!;Assert.Equal(0,toolbar.Opacity);Assert.False(window.FindControl<ScrollViewer>("ParticipantGallery")!.IsVisible);
        var point=controls.TranslatePoint(new Point(controls.Bounds.Width/2,controls.Bounds.Height-4),window)!.Value;window.MouseMove(point);Dispatcher.UIThread.RunJobs();Assert.Equal(1,toolbar.Opacity);
        window.MouseMove(new Point(4,4));Dispatcher.UIThread.RunJobs();Assert.Equal(0,toolbar.Opacity);
        window.FindControl<Button>("ExitFullscreenButton")!.Focus();Dispatcher.UIThread.RunJobs();Assert.Equal(1,toolbar.Opacity);Save(window,"native-share-hover-controls.png");
        media.RemoteControlChanged+=Raise.Event<Action<RemoteControlStatus>>(new RemoteControlStatus(new[]{new NativePeer("one",true,true,Guid.NewGuid().ToString("N"))},null,null,null));Dispatcher.UIThread.RunJobs();
        Assert.True(vm.CanRequestFocusedControl);window.FindControl<Button>("FullscreenRequestControl")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await media.Received(1).RequestControlAsync("one");
        window.FindControl<Button>("ExitFullscreenButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();Assert.NotEqual(WindowState.FullScreen,window.WindowState);Assert.True(window.FindControl<ScrollViewer>("ParticipantGallery")!.IsVisible);
        window.CloseAfterCall();Dispatcher.UIThread.RunJobs();await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task NamedShareContextRequestRequiresGrantThenFocusesFullscreenAndReleasesOnRevoke()
    {
        var (vm,media)=Create();vm.InCall=true;
        var owner=Guid.NewGuid().ToString();var track=owner+" · screen · track";
        var peer=new NativePeer(owner,true,true,"scope");
        media.RemoteControlChanged+=Raise.Event<Action<RemoteControlStatus>>(new RemoteControlStatus(new[]{peer},null,null,null));Dispatcher.UIThread.RunJobs();
        Assert.Equal("Participant's screen",Assert.Single(vm.RemotePeers).ScreenLabel);
        media.ParticipantJoined+=Raise.Event<Action<MediaParticipant>>(new MediaParticipant(owner,"Kirollos Adel"));Dispatcher.UIThread.RunJobs();
        Assert.Equal("Kirollos Adel's screen",Assert.Single(vm.RemotePeers).ScreenLabel);
        media.VideoReceived+=Raise.Event<Action<VideoPicture>>(new VideoPicture(track,2,2,new byte[16]));Dispatcher.UIThread.RunJobs();
        var screen=Assert.Single(vm.Videos.Where(x=>x.Identity==track));Assert.Equal("Kirollos Adel's screen",screen.Name);Assert.False(screen.CanControl);
        vm.ClearVideoFocusCommand.Execute(null);
        var window=new CallWindow{DataContext=vm,Width=1200,Height=800};window.Show();Dispatcher.UIThread.RunJobs();
        var card=window.GetVisualDescendants().OfType<Border>().Single(x=>x.Classes.Contains("participant-card")&&x.DataContext==screen);
        var point=card.TranslatePoint(new Point(24,24),window)!.Value;window.MouseMove(point);window.MouseDown(point,MouseButton.Right);window.MouseUp(point,MouseButton.Right);Dispatcher.UIThread.RunJobs();
        var menu=Assert.IsType<ContextMenu>(card.ContextMenu);var request=menu.Items.OfType<MenuItem>().Single(x=>x.Header?.ToString()=="Request remote control");
        Assert.True(request.Command!.CanExecute(request.CommandParameter));request.Command.Execute(request.CommandParameter);menu.Close();Dispatcher.UIThread.RunJobs();
        await media.Received(1).RequestControlAsync(owner);Assert.NotEqual(WindowState.FullScreen,window.WindowState);Assert.False(screen.CanControl);
        media.RemoteControlChanged+=Raise.Event<Action<RemoteControlStatus>>(new RemoteControlStatus(new[]{peer},null,null,owner));Dispatcher.UIThread.RunJobs();
        Assert.Same(screen,vm.FocusedVideo);Assert.True(screen.CanControl);Assert.Equal(WindowState.FullScreen,window.WindowState);Assert.Contains("Kirollos Adel",vm.RemoteControlLabel);Assert.DoesNotContain(owner,vm.RemoteControlLabel);
        var input=window.FindControl<ContentControl>("FocusedStage")!.GetVisualDescendants().OfType<NativeVideoView>().Single();Assert.True(input.RemoteControlEnabled);Assert.True(input.IsFocused);
        var context = new ContextRequestedEventArgs(); input.RaiseEvent(context); Dispatcher.UIThread.RunJobs();
        Assert.True(context.Handled); Assert.Null(window.FindControl<ContentControl>("FocusedStage")!.ContextMenu);
        using (var rendered = window.CaptureRenderedFrame()) Assert.NotNull(rendered);
        var remotePoint = input.TranslatePoint(new Point(input.Bounds.Width / 2, input.Bounds.Height / 2), window)!.Value;
        window.MouseMove(remotePoint); window.MouseDown(remotePoint, MouseButton.Right); window.MouseUp(remotePoint, MouseButton.Right); Dispatcher.UIThread.RunJobs();
        media.Received().SendRemoteInput(Arg.Is<RemoteInput>(x => x.Target == track && x.Kind == 1 && x.Code == 2 && x.Down));
        media.Received().SendRemoteInput(Arg.Is<RemoteInput>(x => x.Target == track && x.Kind == 1 && x.Code == 2 && !x.Down));
        Assert.Null(window.FindControl<ContentControl>("FocusedStage")!.ContextMenu);
        input.RaiseEvent(new KeyEventArgs{RoutedEvent=InputElement.KeyDownEvent,Key=Key.A});media.Received().SendRemoteInput(Arg.Is<RemoteInput>(x=>x.Target==track&&x.Kind==2&&x.Code==4&&x.Down));
        media.RemoteControlChanged+=Raise.Event<Action<RemoteControlStatus>>(new RemoteControlStatus(new[]{peer},null,null,null));Dispatcher.UIThread.RunJobs();Assert.False(screen.CanControl);Assert.NotEqual(WindowState.FullScreen,window.WindowState);
        media.ClearReceivedCalls();input.RaiseEvent(new KeyEventArgs{RoutedEvent=InputElement.KeyDownEvent,Key=Key.B});media.DidNotReceive().SendRemoteInput(Arg.Is<RemoteInput>(x=>x.Kind==2));
        window.CloseAfterCall();Dispatcher.UIThread.RunJobs();await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task GrantBeforeScreenFrameWaitsForThatShareAndRenamingNeverShowsIdentifiers()
    {
        var (vm,media)=Create();vm.InCall=true;var owner=Guid.NewGuid().ToString();var peer=new NativePeer(owner,true,true,"scope");
        var window=new CallWindow{DataContext=vm};window.Show();Dispatcher.UIThread.RunJobs();
        media.RemoteControlChanged+=Raise.Event<Action<RemoteControlStatus>>(new RemoteControlStatus(new[]{peer},null,null,owner));Dispatcher.UIThread.RunJobs();Assert.NotEqual(WindowState.FullScreen,window.WindowState);
        media.VideoReceived+=Raise.Event<Action<VideoPicture>>(new VideoPicture(owner+" · screen · track",2,2,new byte[16]));Dispatcher.UIThread.RunJobs();Assert.Equal(WindowState.FullScreen,window.WindowState);Assert.True(vm.FocusedVideo!.CanControl);
        media.ParticipantJoined+=Raise.Event<Action<MediaParticipant>>(new MediaParticipant(owner,"Dina Example"));Dispatcher.UIThread.RunJobs();Assert.Equal("Dina Example's screen",vm.FocusedVideo.Name);Assert.Equal("Dina Example's screen",Assert.Single(vm.RemotePeers).ScreenLabel);
        window.RaiseEvent(new KeyEventArgs{RoutedEvent=InputElement.KeyDownEvent,Key=Key.Escape});Dispatcher.UIThread.RunJobs();Assert.NotEqual(WindowState.FullScreen,window.WindowState);
        media.RemoteControlChanged+=Raise.Event<Action<RemoteControlStatus>>(new RemoteControlStatus(new[]{peer},null,null,owner));Dispatcher.UIThread.RunJobs();Assert.NotEqual(WindowState.FullScreen,window.WindowState);
        window.CloseAfterCall();Dispatcher.UIThread.RunJobs();await vm.DisposeAsync();
    }
    [Theory]
    [InlineData(ScreenCaptureBackend.Windows,ScreenShareKind.Screen,0,0,1920,1080,true)]
    [InlineData(ScreenCaptureBackend.X11,ScreenShareKind.Screen,0,0,1920,1080,true)]
    [InlineData(ScreenCaptureBackend.MacOS,ScreenShareKind.Screen,0,0,1920,1080,true)]
    [InlineData(ScreenCaptureBackend.Windows,ScreenShareKind.Screen,1920,0,1920,1080,false)]
    [InlineData(ScreenCaptureBackend.Windows,ScreenShareKind.Window,0,0,1920,1080,false)]
    [InlineData(ScreenCaptureBackend.Portal,ScreenShareKind.Screen,0,0,1920,1080,false)]
    [InlineData(ScreenCaptureBackend.Windows,ScreenShareKind.Screen,0,0,1280,720,false)]
    public void ControlHosting_RequiresTheSupportedPrimaryDesktop(ScreenCaptureBackend backend,ScreenShareKind kind,int x,int y,int width,int height,bool allowed)
    {
        Assert.Equal(allowed,RemoteControlPolicy.SupportsDesktopSource(new("","","",new("source","Source",kind,backend,x,y,width,height)),1920,1080));
    }
    [Fact]
    public void BrowserController_NeedsHandshakeAndExplicitGrantAndCannotOfferHosting()
    {
        var policy=new RemoteControlPolicy();policy.Sharing(true);Assert.False(policy.Request("browser"));
        policy.Hello("browser",new RemotePacket("hello",CanHost:false,Sharing:false));Assert.True(policy.Request("browser"));
        var input=new RemotePacket("input",Scope:policy.LocalScope,Sequence:1,Input:new("host · screen",0,.5,.5));Assert.False(policy.Authorize("browser",input));
        var grant=policy.Approve("browser");Assert.True(policy.Authorize("browser",input with{Grant=grant.Grant}));Assert.False(policy.Authorize("another",input with{Grant=grant.Grant,Sequence=2}));Assert.Throws<InvalidOperationException>(()=>policy.BeginRequest("browser"));
        policy.Revoke();Assert.False(policy.Authorize("browser",input with{Grant=grant.Grant,Sequence=2}));
    }
    private static void Save(Window window,string name)
    {
        if(Environment.GetEnvironmentVariable("FIREUSER_TEST_ARTIFACTS") is not {} path)return;
        Directory.CreateDirectory(path);Dispatcher.UIThread.RunJobs();using var frame=window.CaptureRenderedFrame();Assert.NotNull(frame);using var file=File.Create(Path.Combine(path,name));frame.Save(file,new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

}
