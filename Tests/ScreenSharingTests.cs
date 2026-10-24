using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NSubstitute;
using UserIdentityDesktop.Core;
using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.Service.Media;
using UserIdentityDesktop.ViewModels;
using UserIdentityDesktop.Views;
using Xunit;

namespace UserIdentityDesktop.Tests;

public sealed class ScreenSharingTests
{
    private static (WorkspaceViewModel Vm, INativeMediaSession Media) Create()
    {
        var auth = Substitute.For<IAuthService>(); auth.CurrentUser.Returns(new UserData { Id = Guid.NewGuid() }); var chat = Substitute.For<IChatService>();
        var vm = new ChatViewModel(auth, chat, Substitute.For<IUserService>(), Substitute.For<IImageService>(), Substitute.For<IFileService>(), Substitute.For<INotificationService>());
        var media = Substitute.For<INativeMediaSession>();
        return (new WorkspaceViewModel(vm, Substitute.For<ICollaborationApi>(), media, auth, chat) { InCall = true, ReduceMotion = true }, media);
    }
    private static readonly ScreenShareSource Screen = new("monitor-0", "Screen 1", ScreenShareKind.Screen, ScreenCaptureBackend.Windows);
    private static readonly ScreenShareSource Window = new("123", "Window", ScreenShareKind.Window, ScreenCaptureBackend.Windows);
    [AvaloniaFact]
    public async Task PortalRemoteControlIsOptInAndPassedToTheNativeShare()
    {
        var (vm,media)=Create();media.GetScreenSourcesAsync(Arg.Any<CancellationToken>()).Returns(new[]{Screen with{Backend=ScreenCaptureBackend.Portal}});
        vm.ChooseScreenShareSourceAsync=_=>Task.FromResult<ScreenShareSource?>(null);
        Assert.False(vm.AllowWaylandRemoteControl);vm.AllowWaylandRemoteControl=true;
        await vm.ToggleScreenCommand.ExecuteAsync(null);
        await media.Received(1).SetScreenAsync(true,Arg.Is<MediaDevices>(d=>d.AllowRemoteControl&&d.ShareSource!.Backend==ScreenCaptureBackend.Portal),Arg.Any<CancellationToken>());
        await vm.LeaveAsync();Assert.False(vm.AllowWaylandRemoteControl);await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task SharePicker_ChoosesWindowBeforeCapture_AndStopDoesNotOpenPickerAgain()
    {
        var (vm, media) = Create(); media.GetScreenSourcesAsync(Arg.Any<CancellationToken>()).Returns(new[] { Screen, Window });
        var owner = new CallWindow { DataContext = vm }; owner.Show();
        try
        {
            var sharing = vm.ToggleScreenCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs();
            var picker = Assert.Single(owner.OwnedWindows.OfType<ScreenSharePicker>()); Assert.False(vm.ScreenEnabled);
            await media.DidNotReceive().SetScreenAsync(true, Arg.Any<MediaDevices>(), Arg.Any<CancellationToken>());
            var model = Assert.IsType<ScreenSharePickerViewModel>(picker.DataContext); Assert.False(model.UsesPortal);
            picker.FindControl<TabControl>("SourceTabs")!.SelectedIndex = 1; Dispatcher.UIThread.RunJobs(); Assert.Equal(Window, model.SelectedSource);
            if (Environment.GetEnvironmentVariable("FIREUSER_TEST_ARTIFACTS") is {} directory)
            {
                Directory.CreateDirectory(directory); using var image = picker.CaptureRenderedFrame(); using var file = File.Create(Path.Combine(directory, "screen-share-picker.png")); image!.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            picker.FindControl<Button>("ShareButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await sharing;
            Assert.True(vm.ScreenEnabled);
            await media.Received(1).SetScreenAsync(true, Arg.Is<MediaDevices>(x => x.ShareSource == Window), Arg.Any<CancellationToken>());
            await vm.ToggleScreenCommand.ExecuteAsync(null); Assert.False(vm.ScreenEnabled); Assert.Empty(owner.OwnedWindows);
            await media.Received(1).GetScreenSourcesAsync(Arg.Any<CancellationToken>());
            await media.Received(1).SetScreenAsync(false, Arg.Any<MediaDevices>(), Arg.Any<CancellationToken>());
        }
        finally { owner.CloseAfterCall(); await vm.DisposeAsync(); }
    }
    [AvaloniaFact]
    public async Task SharePicker_EscapeCancelsWithoutStartingCapture()
    {
        var (vm, media) = Create(); media.GetScreenSourcesAsync(Arg.Any<CancellationToken>()).Returns(new[] { Screen, Window });
        var owner = new CallWindow { DataContext = vm }; owner.Show();
        try
        {
            var sharing = vm.ToggleScreenCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs(); var picker = Assert.Single(owner.OwnedWindows.OfType<ScreenSharePicker>());
            picker.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape }); await sharing;
            Assert.False(vm.ScreenEnabled); Assert.Empty(vm.Feedback);
            await media.DidNotReceive().SetScreenAsync(Arg.Any<bool>(), Arg.Any<MediaDevices>(), Arg.Any<CancellationToken>());
        }
        finally { owner.CloseAfterCall(); await vm.DisposeAsync(); }
    }
    [AvaloniaTheory]
    [InlineData(true)] [InlineData(false)]
    public async Task PortalCancelOrFailure_DoesNotEnableSharingOrFallback(bool cancel)
    {
        var (vm, media) = Create(); media.GetScreenSourcesAsync(Arg.Any<CancellationToken>()).Returns(new[] { Screen with { Backend = ScreenCaptureBackend.Portal } });
        vm.ChooseScreenShareSourceAsync = _ => Task.FromResult<ScreenShareSource?>(Screen);
        media.SetScreenAsync(true, Arg.Any<MediaDevices>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(cancel ? new ScreenShareCanceledException() : new IOException("Portal unavailable")));
        await vm.ToggleScreenCommand.ExecuteAsync(null); Assert.False(vm.ScreenEnabled); Assert.False(vm.MediaBusy);
        Assert.Equal(cancel ? "" : "Portal unavailable", vm.Feedback);
        await media.Received(1).SetScreenAsync(true, Arg.Is<MediaDevices>(x => x.ShareSource!.Backend == ScreenCaptureBackend.Portal), Arg.Any<CancellationToken>());
        await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task LeaveDuringPortalApproval_CancelsPendingStartAndLeavesCall()
    {
        var (vm, media) = Create(); media.GetScreenSourcesAsync(Arg.Any<CancellationToken>()).Returns(new[] { Screen with { Backend = ScreenCaptureBackend.Portal } });
        vm.ChooseScreenShareSourceAsync = _ => Task.FromResult<ScreenShareSource?>(Screen);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        media.SetScreenAsync(true, Arg.Any<MediaDevices>(), Arg.Any<CancellationToken>()).Returns(call => { var ct = call.Arg<CancellationToken>(); ct.Register(() => pending.TrySetCanceled(ct)); return pending.Task; });
        var sharing = vm.ToggleScreenCommand.ExecuteAsync(null); Assert.True(vm.MediaBusy);
        await vm.LeaveCommand.ExecuteAsync(null); await sharing;
        Assert.False(vm.InCall); Assert.False(vm.ScreenEnabled); Assert.False(vm.MediaBusy);
        await media.Received(1).DisconnectAsync(); await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task SourceStopsDuringStartup_SharingRemainsOff_AndPendingPreviewDoesNotReappear()
    {
        var (vm, media) = Create(); media.GetScreenSourcesAsync(Arg.Any<CancellationToken>()).Returns(new[] { Screen with { Backend = ScreenCaptureBackend.Portal } });
        vm.ChooseScreenShareSourceAsync = _ => Task.FromResult<ScreenShareSource?>(Screen);
        media.SetScreenAsync(true, Arg.Any<MediaDevices>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            media.VideoReceived += Raise.Event<Action<VideoPicture>>(new VideoPicture("You · screen", 2, 2, new byte[16]));
            media.CaptureStopped += Raise.Event<Action<string>>("screen");
            return Task.CompletedTask;
        });
        await vm.ToggleScreenCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs();
        Assert.False(vm.ScreenEnabled); Assert.DoesNotContain(vm.Videos, x => x.IsScreen); Assert.Null(vm.FocusedVideo);
        await vm.DisposeAsync();
    }
    [Theory]
    [InlineData("windows", ScreenCaptureBackend.Windows)] [InlineData("linux", ScreenCaptureBackend.X11)]
    public void SelectedScreen_UsesOnlyItsBounds_AndSelectedWindowUsesItsStableId(string platform, ScreenCaptureBackend backend)
    {
        var screen = new ScreenShareSource("monitor-1", "Second screen", ScreenShareKind.Screen, backend, -1920, 0, 1920, 1080);
        var args = NativeCapture.Input("screen", new("", "", "", screen), platform, ":1", false);
        Assert.Contains("1920x1080", args);
        if (platform == "windows") Assert.Equal(new[] { "-f", "gdigrab", "-framerate", "60", "-video_size", "1920x1080", "-offset_x", "-1920", "-offset_y", "0", "-i", "desktop" }, args);
        else Assert.Equal(new[] { "-f", "x11grab", "-framerate", "60", "-video_size", "1920x1080", "-i", ":1+-1920,0" }, args);
        var window = screen with { Id = "1234", Kind = ScreenShareKind.Window };
        args = NativeCapture.Input("screen", new("", "", "", window), platform, ":1", false);
        Assert.DoesNotContain("-video_size", args); Assert.DoesNotContain("desktop", args);
        Assert.Contains(platform == "windows" ? "hwnd=1234" : "1234", args);
    }
    [Fact]
    public void CaptureWithoutSelectionOrWithForeignProvider_IsRejected()
    {
        Assert.Throws<InvalidOperationException>(() => NativeCapture.Input("screen", new("", "", ""), "windows", null, false));
        Assert.Throws<InvalidOperationException>(() => NativeCapture.Input("screen", new("", "", "", Screen), "linux", ":0", false));
        Assert.Throws<InvalidOperationException>(() => NativeCapture.Input("screen", new("", "", "", new("1", "Window", ScreenShareKind.Window, ScreenCaptureBackend.Windows)), "linux", ":0", false));
    }
}
