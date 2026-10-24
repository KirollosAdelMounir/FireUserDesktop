using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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
public sealed class IncomingCallAlertTests
{
    private static (WorkspaceViewModel Vm,IChatService Chat,IImageService Images,INotificationService Notifications) Create()
    {
        var auth=Substitute.For<IAuthService>();auth.CurrentUser.Returns(new UserData{Id=Guid.NewGuid()});
        var chat=Substitute.For<IChatService>();var images=Substitute.For<IImageService>();var notices=Substitute.For<INotificationService>();
        chat.GetChatHistoryAsync(Arg.Any<Guid>(),0).Returns(new List<ChatMessage>());
        var vm=new ChatViewModel(auth,chat,Substitute.For<IUserService>(),images,Substitute.For<IFileService>(),notices);
        return(new WorkspaceViewModel(vm,Substitute.For<ICollaborationApi>(),Substitute.For<INativeMediaSession>(),auth,chat,notices){ReduceMotion=true},chat,images,notices);
    }
    private static Bitmap Picture() => new WriteableBitmap(new PixelSize(2,2),new Vector(96,96),PixelFormat.Bgra8888,AlphaFormat.Premul);
    [AvaloniaTheory]
    [InlineData(UserStatus.Online)] [InlineData(UserStatus.Busy)] [InlineData(UserStatus.DoNotDisturb)]
    public async Task IncomingCall_ShowsOneActionableAlertWithCallerPhoto_WithoutHistoryOrGenericToasts(UserStatus status)
    {
        var (vm,chat,images,notices)=Create();var caller=Guid.NewGuid();var photo=Picture();images.GetBitmapFromBase64("compressed-photo").Returns(photo);
        vm.Chat.CurrentUserStatus=status;vm.Chat.SelectedRecipient=new RecipientItemViewModel{ReceiverId=caller,ReceiverName="Caller Name"};
        var view=new WorkspaceView{DataContext=vm};var owner=new Window{Content=view};owner.Show();Dispatcher.UIThread.RunJobs();
        ChatMessage History(string id)=>new(){MessageId=id,Sender=caller,Receiver=vm.Chat.CurrentUser.Id,SenderName="Caller Name",Text="Caller Name called you.",CallSummary=new("call","audio","ringing","Caller Name",true,null)};
        chat.MessageReceived+=Raise.Event<Action<ChatMessage>>(History("before"));
        var call=new IncomingCall("call",caller,"Caller Name","audio",1,CallerAvatar:"compressed-photo");
        chat.IncomingCallReceived+=Raise.Event<Action<IncomingCall>>(call);chat.MessageReceived+=Raise.Event<Action<ChatMessage>>(History("after"));Dispatcher.UIThread.RunJobs();
        var alert=Assert.IsType<IncomingCallWindow>(view.ActiveIncomingWindow);Assert.True(alert.IsVisible);Assert.False(alert.ShowActivated);
        if(alert.Screens.Primary is {} screen) Assert.Equal(IncomingCallWindow.RightCenter(screen.WorkingArea,alert.Bounds.Width,alert.Bounds.Height,screen.Scaling),alert.Position);
        if(Environment.GetEnvironmentVariable("FIREUSER_TEST_ARTIFACTS") is {} folder){Directory.CreateDirectory(folder);using var frame=alert.CaptureRenderedFrame();Assert.NotNull(frame);using var output=File.Create(Path.Combine(folder,"incoming-call-right-center.png"));frame.Save(output,new PngBitmapEncoderOptions());}
        Assert.Contains(alert.GetVisualDescendants().OfType<TextBlock>(),x=>x.Text=="Caller Name");Assert.Same(photo,alert.GetVisualDescendants().OfType<Image>().Single().Source);
        Assert.Contains(alert.GetVisualDescendants().OfType<Button>(),x=>Equals(x.Content,"Answer"));Assert.Contains(alert.GetVisualDescendants().OfType<Button>(),x=>Equals(x.Content,"Decline"));
        Assert.Equal(2,vm.Chat.Messages.Count(x=>x.IsCall));
        notices.DidNotReceive().ShowNotification(Arg.Any<string>(),Arg.Any<string>(),Arg.Any<string>());notices.DidNotReceive().ShowNotification(Arg.Any<string>(),Arg.Any<string>(),Arg.Any<Guid>());
        chat.IncomingCallReceived+=Raise.Event<Action<IncomingCall>>(call with{});Dispatcher.UIThread.RunJobs();Assert.Same(alert,view.ActiveIncomingWindow);
        chat.MessageReceived+=Raise.Event<Action<ChatMessage>>(new ChatMessage{MessageId="text",Sender=caller,Receiver=vm.Chat.CurrentUser.Id,SenderName="Caller Name",Text="A normal message"});Dispatcher.UIThread.RunJobs();
        notices.Received(1).ShowNotification("Caller Name","A normal message",caller);
        vm.Incoming=null;Dispatcher.UIThread.RunJobs();Assert.Null(view.ActiveIncomingWindow);Assert.Null(vm.IncomingAvatar);
        owner.Close();vm.Chat.UnregisterEvents();await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task CompressedPhotoFallback_IgnoresLateImageFromReplacedCaller()
    {
        var (vm,chat,images,_)=Create();var first=Guid.NewGuid();var pending=new TaskCompletionSource<Bitmap?>();images.GetProfilePictureAsync(first).Returns(pending.Task);
        chat.IncomingCallReceived+=Raise.Event<Action<IncomingCall>>(new IncomingCall("first",first,"First Caller","audio",1));Dispatcher.UIThread.RunJobs();Assert.Null(vm.IncomingAvatar);Assert.Equal("FC",vm.IncomingCallerInitials);
        var secondPhoto=Picture();images.GetBitmapFromBase64("second-photo").Returns(secondPhoto);
        chat.IncomingCallReceived+=Raise.Event<Action<IncomingCall>>(new IncomingCall("second",Guid.NewGuid(),"Second Caller","video",1,CallerAvatar:"second-photo"));Dispatcher.UIThread.RunJobs();
        using var latePhoto=Picture();pending.SetResult(latePhoto);await Task.Yield();Dispatcher.UIThread.RunJobs();Assert.Same(secondPhoto,vm.IncomingAvatar);Assert.Equal("Second Caller",vm.IncomingCallerName);
        vm.Chat.UnregisterEvents();await vm.DisposeAsync();
    }
    [Theory]
    [InlineData(0,0,1920,1080,1,1500,446)]
    [InlineData(-2560,100,2560,1400,1.5,-630,659)]
    public void IncomingAlertIsCenteredVerticallyAndInsetFromRightAtDisplayScale(int x,int y,int width,int height,double scale,int expectedX,int expectedY)
    {
        Assert.Equal(new PixelPoint(expectedX,expectedY),IncomingCallWindow.RightCenter(new PixelRect(x,y,width,height),400,188,scale));
    }
    [Fact]
    public void IncomingPayload_PreservesServerCompressedCallerAvatar()
    {
        var caller=Guid.NewGuid();var payload=System.Text.Json.JsonSerializer.Serialize(new{callId="call",callerId=caller,callerName="Caller",callType="audio",participantCount=1,callerAvatar="compressed-base64"},ApiPayload.Json);
        var incoming=System.Text.Json.JsonSerializer.Deserialize<IncomingCall>(payload,ApiPayload.Json)!;
        Assert.Equal(caller,incoming.CallerId);Assert.Equal("compressed-base64",incoming.CallerAvatar);
    }
}
