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
