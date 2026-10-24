using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using UserIdentityDesktop.Core;
using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.Data;
using UserIdentityDesktop.Service;
using UserIdentityDesktop.Service.Media;
using UserIdentityDesktop.ViewModels;
using UserIdentityDesktop.Views;
using Xunit;
namespace UserIdentityDesktop.Tests;

public sealed class ChatAttachmentParityTests
{
    private sealed record Vector(ChatEncryption.Recovery Recovery, ChatEncryption.PublicIdentity PublicIdentity, string Wire, EncryptedFile File, string FileCipher);
    private static Vector Read() => JsonSerializer.Deserialize<Vector>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"chat-vector.json")),ApiPayload.Json)!;
    private sealed class IdentityHandler(Vector vector) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            object body=request.RequestUri!.AbsolutePath.EndsWith("/Identity") ? vector.Recovery : new ChatEncryption.PublicResponse(vector.Recovery.UserId,JsonSerializer.Serialize(vector.PublicIdentity,ApiPayload.Json));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(body,ApiPayload.Json),Encoding.UTF8,"application/json")});
        }
    }
    [Theory]
    [InlineData("Here are the photos",2,"Here are the photos")]
    [InlineData(null,2,"Attachments")]
    [InlineData("Hello",0,"Hello")]
    public async Task SidebarShowsVerifiedTextAndAttachmentCounts(string? text,int count,string label)
    {
        var v=Read();var auth=Substitute.For<IAuthService>();auth.CurrentUser.Returns(new UserData{Id=v.Recovery.UserId});
        var config=Substitute.For<IConfigurationService>();config.ServerUrl.Returns("https://chat-preview-tests.invalid");
        auth.CreateAuthenticatedClient().Returns(_=>new HttpClient(new IdentityHandler(v)));
        var encryption=new ChatEncryption(auth,config);
        var payload=new ChatEncryption.Payload(1,"preview",v.Recovery.UserId,v.Recovery.UserId,null,new[]{v.Recovery.UserId},text,Enumerable.Repeat(v.File,count).ToArray());
        var wire=ChatEncryption.Protect(payload,v.Recovery,new Dictionary<Guid,ChatEncryption.PublicIdentity>{{v.Recovery.UserId,v.PublicIdentity}});
        var dto=await encryption.DecryptPreviewAsync(new RecipientDto{ReceiverId=v.Recovery.UserId,LastMessage=wire});
        var item=RecipientItemViewModel.FromDto(dto);Assert.Equal(label,item.PreviewText);Assert.Equal(count,item.AttachmentsCount);Assert.Equal(count>0,item.HasPreviewAttachments);
        dto=await encryption.DecryptPreviewAsync(new RecipientDto{ReceiverId=Guid.NewGuid(),LastMessage=wire,AttachmentsCount=99});
        Assert.Equal("Unlock chat to view this message",dto.LastMessage);Assert.Equal(0,dto.AttachmentsCount);
        dto=await encryption.DecryptPreviewAsync(new RecipientDto{ReceiverId=v.Recovery.UserId,LastMessage="FU1.invalid"});Assert.DoesNotContain("FU1",dto.LastMessage!);
    }
    [Fact]
    public void PreviewRejectsAnotherConversationOrAudience()
    {
        var owner=Guid.NewGuid();var payload=new ChatEncryption.Payload(1,"n",owner,Guid.Empty,"group",new[]{owner},"Private",Array.Empty<EncryptedFile>());
        Assert.Throws<CryptographicException>(()=>ChatEncryption.ValidatePreviewDestination(payload,new RecipientDto{ConversationId="different"},owner));
        Assert.Throws<CryptographicException>(()=>ChatEncryption.ValidatePreviewDestination(payload,new RecipientDto{ConversationId="group"},Guid.NewGuid()));
        ChatEncryption.ValidatePreviewDestination(payload,new RecipientDto{ConversationId="group"},owner);
    }
    private static ChatAttachmentItemViewModel FileItem(IChatService chat,string name,string mime="application/pdf")=>new(new ChatAttachment{Id=Guid.NewGuid(),FileName=name,MimeType=mime},chat,Substitute.For<IFileService>(),Substitute.For<IImageService>());
    [Fact]
    public async Task DownloadAllCreatesVerifiedZipWithSafeDistinctNamesAndPreservesDestinationOnFailure()
    {
        var chat=Substitute.For<IChatService>();chat.SaveAttachmentToFileAsync(Arg.Any<Guid>(),Arg.Any<string>(),Arg.Any<EncryptedFile?>(),Arg.Any<CancellationToken>(),Arg.Any<IProgress<long>?>()).Returns(async call=>{await File.WriteAllTextAsync(call.ArgAt<string>(1),"verified contents");return true;});
        var dir=Path.Combine(Path.GetTempPath(),"fireuser-archive-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);var destination=Path.Combine(dir,"files.zip");
        try
        {
            await AttachmentArchive.SaveAsync(new[]{FileItem(chat,"../report.pdf"),FileItem(chat,"report.pdf")},destination);
            using(var zip=ZipFile.OpenRead(destination)){Assert.Equal(new[]{"report.pdf","report (1).pdf"},zip.Entries.Select(x=>x.FullName));foreach(var entry in zip.Entries){using var reader=new StreamReader(entry.Open());Assert.Equal("verified contents",await reader.ReadToEndAsync());}}
            var original=await File.ReadAllBytesAsync(destination);
            chat.SaveAttachmentToFileAsync(Arg.Any<Guid>(),Arg.Any<string>(),Arg.Any<EncryptedFile?>(),Arg.Any<CancellationToken>(),Arg.Any<IProgress<long>?>()).Returns(false);
            await Assert.ThrowsAsync<IOException>(()=>AttachmentArchive.SaveAsync(new[]{FileItem(chat,"failed.pdf")},destination));
            Assert.Equal(original,await File.ReadAllBytesAsync(destination));Assert.Single(Directory.GetFiles(dir));
        }
        finally{Directory.Delete(dir,true);}
    }
    [Fact]
    public async Task CancelledPreviewDeletesItsTemporaryPlaintext()
    {
        var chat=Substitute.For<IChatService>();string? temporary=null;var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        chat.SaveAttachmentToFileAsync(Arg.Any<Guid>(),Arg.Any<string>(),Arg.Any<EncryptedFile?>(),Arg.Any<CancellationToken>(),Arg.Any<IProgress<long>?>()).Returns(async call=>{temporary=call.ArgAt<string>(1);await File.WriteAllTextAsync(temporary,"partial plaintext");started.SetResult();await Task.Delay(Timeout.Infinite,call.ArgAt<CancellationToken>(3));return true;});
        using var stop=new CancellationTokenSource();var pending=FileItem(chat,"photo.png","image/png").PreparePreviewAsync(stop.Token);await started.Task;Assert.True(File.Exists(temporary));stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);Assert.False(File.Exists(temporary));
    }
    [AvaloniaFact]
    public async Task ReactionsAreFiveButtonsAndDownloadAllIsDirect()
    {
        var auth=Substitute.For<IAuthService>();auth.CurrentUser.Returns(new UserData{Id=Guid.NewGuid()});var chat=Substitute.For<IChatService>();
        var model=new ChatViewModel(auth,chat,Substitute.For<IUserService>(),Substitute.For<IImageService>(),Substitute.For<IFileService>(),Substitute.For<INotificationService>());
        var message=new ChatMessage{MessageId="msg",Text="Look",Attachments=new(){new(){FileName="report.pdf"}}};
        var item=new ChatMessageItemViewModel(message,false,chat,Substitute.For<IFileService>(),Substitute.For<IImageService>());
        var menu=MessageMenus.Create(model,item,new Border());var reactions=Assert.IsType<StackPanel>(menu.Items.OfType<MenuItem>().First().Header);
        Assert.Equal(new[]{"👍","❤️","😂","😢","😡"},reactions.Children.OfType<Button>().Select(x=>x.Content));
        reactions.Children.OfType<Button>().ElementAt(2).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await chat.Received(1).ReactAsync("msg","😂");Assert.Contains(menu.Items.OfType<MenuItem>(),x=>x.Header?.ToString()=="Download All"&&x.Command==item.DownloadAllCommand);
    }
    [AvaloniaFact]
    public async Task StackedAttachmentsOpenOwnedGalleryAndNavigateWithoutDownloadingDocuments()
    {
        var chat=Substitute.For<IChatService>();var item=new ChatMessageItemViewModel(new ChatMessage{Text="Review",Attachments=new(){new(){FileName="one.pdf"},new(){FileName="two.pdf"}}},false,chat,Substitute.For<IFileService>(),Substitute.For<IImageService>());
        var bundle=new AttachmentBundle{Message=item};var owner=new Window{Width=900,Height=700,Content=bundle};owner.Show();Dispatcher.UIThread.RunJobs();
        var open=Assert.Single(bundle.GetVisualDescendants().OfType<Button>());Assert.Equal("View 2 attachments",Avalonia.Automation.AutomationProperties.GetName(open));open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();
        var gallery=Assert.Single(owner.OwnedWindows.OfType<AttachmentGalleryWindow>());Assert.Same(owner,gallery.Owner);await gallery.SelectAsync(1);Assert.Equal(1,gallery.SelectedIndex);Assert.Contains(gallery.GetVisualDescendants().OfType<TextBlock>(),x=>x.Text=="two.pdf");await gallery.SelectAsync(2);Assert.Equal(1,gallery.SelectedIndex);await gallery.SelectAsync(0);Assert.Equal(0,gallery.SelectedIndex);
        await chat.DidNotReceive().SaveAttachmentToFileAsync(Arg.Any<Guid>(),Arg.Any<string>(),Arg.Any<EncryptedFile?>(),Arg.Any<CancellationToken>(),Arg.Any<IProgress<long>?>());
        Save(gallery,"chat-attachment-gallery.png");gallery.Close();owner.Close();
    }
    [Fact]
    public async Task NativeVideoDecoderReadsSyntheticFramesAndCancelsWithoutDeviceCapture()
    {
        var path=Path.Combine(Path.GetTempPath(),"fireuser-video-test-"+Guid.NewGuid().ToString("N")+".mp4");
        var start=new System.Diagnostics.ProcessStartInfo(NativeCapture.ResolveExecutable("ffmpeg")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};
        foreach(var arg in new[]{"-nostdin","-hide_banner","-loglevel","error","-f","lavfi","-i","color=c=red:s=160x90:r=30","-t","1","-c:v","mpeg4",path})start.ArgumentList.Add(arg);
        try
        {
            using(var process=System.Diagnostics.Process.Start(start)!){await process.WaitForExitAsync();Assert.Equal(0,process.ExitCode);}
            using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(10));VideoPicture? frame=null;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>VideoAttachmentPlayback.DecodeAsync(path,0,picture=>{frame=picture;stop.Cancel();},_=>{},stop.Token));
            Assert.NotNull(frame);Assert.Equal(960,frame.Width);Assert.Equal(540,frame.Height);Assert.Equal(960*540*4,frame.Rgba.Length);Assert.True(frame.Rgba[4*(270*960+480)]>200);Assert.True(frame.Rgba[4*(270*960+480)+1]<30);
        }
        finally{File.Delete(path);}
    }
    [AvaloniaFact]
    public async Task GalleryPreviewsImageAndAudioAndFullscreenVideoThenCleansTemporaryFiles()
    {
        var source=Path.Combine(Path.GetTempPath(),"fireuser-gallery-video-"+Guid.NewGuid().ToString("N")+".mp4");
        var info=new System.Diagnostics.ProcessStartInfo(NativeCapture.ResolveExecutable("ffmpeg")){UseShellExecute=false,CreateNoWindow=true};
        foreach(var arg in new[]{"-nostdin","-hide_banner","-loglevel","error","-f","lavfi","-i","color=c=blue:s=160x90:r=30","-t","1","-c:v","mpeg4",source})info.ArgumentList.Add(arg);
        using(var process=System.Diagnostics.Process.Start(info)!){await process.WaitForExitAsync();Assert.Equal(0,process.ExitCode);}
        var chat=Substitute.For<IChatService>();var paths=new List<string>();var png=PhotoFixture();
        var photo=FileItem(chat,"photo.png","image/png");var sound=FileItem(chat,"voice.mp3","audio/mpeg");var movie=FileItem(chat,"clip.mp4","video/mp4");
        chat.SaveAttachmentToFileAsync(Arg.Any<Guid>(),Arg.Any<string>(),Arg.Any<EncryptedFile?>(),Arg.Any<CancellationToken>(),Arg.Any<IProgress<long>?>()).Returns(async call=>{var path=call.ArgAt<string>(1);paths.Add(path);if(call.ArgAt<Guid>(0)==movie.Attachment.Id)File.Copy(source,path,true);else await File.WriteAllBytesAsync(path,png);return true;});
        var owner=new Window();owner.Show();var gallery=new AttachmentGalleryWindow(new[]{photo,sound,movie});gallery.Show(owner);
        try
        {
            await gallery.SelectAsync(0);Dispatcher.UIThread.RunJobs();Assert.True(gallery.GetVisualDescendants().OfType<Image>().Any(x=>x.Source!=null),string.Join(" | ",gallery.GetVisualDescendants().OfType<TextBlock>().Select(x=>x.Text)));
            await gallery.SelectAsync(1);Dispatcher.UIThread.RunJobs();Assert.Single(gallery.GetVisualDescendants().OfType<InlineAudioPlayer>());Assert.All(paths,x=>Assert.False(File.Exists(x)));
            await gallery.SelectAsync(2);Dispatcher.UIThread.RunJobs();Assert.Single(gallery.GetVisualDescendants().OfType<InlineVideoPlayer>());
            var fullscreen=gallery.GetVisualDescendants().OfType<Button>().Single(x=>x.Content?.ToString()=="Fullscreen");fullscreen.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.Equal(WindowState.FullScreen,gallery.WindowState);
            gallery.GetVisualDescendants().OfType<Button>().Single(x=>x.Content?.ToString()=="Exit fullscreen").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.NotEqual(WindowState.FullScreen,gallery.WindowState);
            await gallery.SelectAsync(0);Dispatcher.UIThread.RunJobs();Assert.Empty(gallery.GetVisualDescendants().OfType<InlineVideoPlayer>());Assert.All(paths.Take(paths.Count-1),x=>Assert.False(File.Exists(x)));
        }
        finally{var closed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);gallery.Closed+=(_,_)=>closed.TrySetResult();gallery.Close();await closed.Task;owner.Close();File.Delete(source);}
        Assert.All(paths,x=>Assert.False(File.Exists(x)));
    }

    [AvaloniaFact]
    public async Task OpeningGroupChatObservesCancelledReadReceiptsAndReportsRealFailuresOnlyForActiveConversation()
    {
        var auth=Substitute.For<IAuthService>();auth.CurrentUser.Returns(new UserData{Id=Guid.NewGuid()});var chat=Substitute.For<IChatService>();
        chat.GetConversationHistoryAsync(Arg.Any<string>(),Arg.Any<int>()).Returns(new List<ChatMessage>());
        var model=new ChatViewModel(auth,chat,Substitute.For<IUserService>(),Substitute.For<IImageService>(),Substitute.For<IFileService>(),Substitute.For<INotificationService>());
        var group=new RecipientItemViewModel{IsGroup=true,ConversationId="group",ReceiverName="Team"};
        chat.MarkConversationSeenAsync("group").Returns(Task.FromCanceled(new CancellationToken(true)));
        model.SelectedRecipient=group;await model.MarkConversationSeenAsync(group);Dispatcher.UIThread.RunJobs();Assert.Null(model.SendError);
        chat.MarkConversationSeenAsync("group").Returns(Task.FromException(new HttpRequestException("connection unavailable")));
        await model.MarkConversationSeenAsync(group);Dispatcher.UIThread.RunJobs();Assert.Contains("Unable to update read status",model.SendError);
        model.SelectedRecipient=null;model.SendError=null;await model.MarkConversationSeenAsync(group);Dispatcher.UIThread.RunJobs();Assert.Null(model.SendError);model.UnregisterEvents();
    }
    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task OwnPhotoBundleUsesContentSizedOrangeBubbleAndWebGalleryDesign(bool dark)
    {
        var auth=Substitute.For<IAuthService>();auth.CurrentUser.Returns(new UserData{Id=Guid.NewGuid(),FirstName="Example"});
        var chat=Substitute.For<IChatService>();var peer=Guid.NewGuid();chat.GetChatHistoryAsync(peer,0).Returns(new List<ChatMessage>());
        var png=PhotoFixture();chat.SaveAttachmentToFileAsync(Arg.Any<Guid>(),Arg.Any<string>(),Arg.Any<EncryptedFile?>(),Arg.Any<CancellationToken>(),Arg.Any<IProgress<long>?>()).Returns(async call=>{await File.WriteAllBytesAsync(call.ArgAt<string>(1),png,call.ArgAt<CancellationToken>(3));return true;});
        var model=new ChatViewModel(auth,chat,Substitute.For<IUserService>(),Substitute.For<IImageService>(),Substitute.For<IFileService>(),Substitute.For<INotificationService>());
        model.SelectedRecipient=new RecipientItemViewModel{ReceiverId=peer,ReceiverName="Example Colleague"};
        var view=new ChatView{DataContext=model};var owner=new Window{Width=1100,Height=760,Content=view,RequestedThemeVariant=dark?Avalonia.Styling.ThemeVariant.Dark:Avalonia.Styling.ThemeVariant.Light};owner.Show();
        chat.MessageReceived+=Raise.Event<Action<ChatMessage>>(new ChatMessage{MessageId="own-photos",Sender=auth.CurrentUser.Id,Receiver=peer,Text="hi",Attachments=Enumerable.Range(1,3).Select(i=>new ChatAttachment{Id=Guid.NewGuid(),FileName=$"Photo {i}.png",MimeType="image/png",FileSize=png.Length}).ToList()});
        Dispatcher.UIThread.RunJobs();await Task.Yield();Dispatcher.UIThread.RunJobs();
        var row=view.GetVisualDescendants().OfType<Grid>().Single(x=>x.Classes.Contains("msg-row"));var item=Assert.IsType<ChatMessageItemViewModel>(row.DataContext);Assert.True(item.IsMine);Assert.Equal("3 photos",item.AttachmentSummary);
        var bubble=row.GetVisualDescendants().OfType<Border>().Single(x=>x.Classes.Contains("msg-bubble"));Assert.True(bubble.Bounds.Width<90);Assert.Equal(Avalonia.Layout.HorizontalAlignment.Right,bubble.HorizontalAlignment);Assert.IsType<Avalonia.Media.LinearGradientBrush>(bubble.Background);
        var bundle=Assert.Single(row.GetVisualDescendants().OfType<AttachmentBundle>());Assert.Contains(bundle.GetVisualDescendants().OfType<TextBlock>(),x=>x.Text=="3 photos");
        Save(owner,$"desktop-photo-bundle-web-parity-{(dark?"dark":"light")}.png");bundle.GetVisualDescendants().OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();
        var gallery=Assert.Single(owner.OwnedWindows.OfType<AttachmentGalleryWindow>());await gallery.ShowOverviewAsync();Dispatcher.UIThread.RunJobs();
        Assert.True(gallery.IsOverview);Assert.Equal(WindowDecorations.Full,gallery.WindowDecorations);
        Assert.DoesNotContain(gallery.GetVisualDescendants().OfType<Button>(),x=>x.Content?.ToString() is "Close" or "×" or "X");Assert.Contains(gallery.GetVisualDescendants().OfType<TextBlock>(),x=>x.Text=="Attachments from this message");
        Assert.Equal(3,gallery.GetVisualDescendants().OfType<Button>().Count(x=>Avalonia.Automation.AutomationProperties.GetName(x)?.StartsWith("View Photo ")==true));
        Assert.Contains(gallery.GetVisualDescendants().OfType<Button>(),x=>Avalonia.Automation.AutomationProperties.GetName(x)=="Download All"&&x.Command==item.DownloadAllCommand);
        Save(gallery,$"desktop-attachment-overview-web-parity-{(dark?"dark":"light")}.png");await gallery.SelectAsync(1);Dispatcher.UIThread.RunJobs();Assert.False(gallery.IsOverview);Assert.Contains(gallery.GetVisualDescendants().OfType<TextBlock>(),x=>x.Text=="2 / 3");
        Save(gallery,$"desktop-media-overlay-web-parity-{(dark?"dark":"light")}.png");
        await gallery.ShowOverviewAsync();Assert.True(gallery.IsOverview);
        var closed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);gallery.Closed+=(_,_)=>closed.TrySetResult();gallery.Close();await closed.Task;owner.Close();model.UnregisterEvents();
    }
    [Theory]
    [InlineData("image/png","3 photos")]
    [InlineData("video/mp4","3 videos")]
    [InlineData("audio/mpeg","3 audio files")]
    [InlineData("application/pdf","3 attachments")]
    public void BundleSummaryNamesTheMediaKind(string mime,string expected)
    {
        var message=new ChatMessage{Attachments=Enumerable.Range(0,3).Select(_=>new ChatAttachment{MimeType=mime}).ToList()};
        var item=new ChatMessageItemViewModel(message,false,Substitute.For<IChatService>(),Substitute.For<IFileService>(),Substitute.For<IImageService>());Assert.Equal(expected,item.AttachmentSummary);
        message.Attachments!.Add(new ChatAttachment{MimeType="application/zip"});var mixed=new ChatMessageItemViewModel(message,false,Substitute.For<IChatService>(),Substitute.For<IFileService>(),Substitute.For<IImageService>());Assert.Equal("4 attachments",mixed.AttachmentSummary);
    }
    [AvaloniaFact]
    public void MessageDetailsUseTitleBarWithoutAnExtraCloseButton()
    {
        var dialog=new MessageDialog(new MessageDialogModel("Message details","Example","Hello","Sent"));dialog.Show();Dispatcher.UIThread.RunJobs();
        Assert.NotEqual(WindowDecorations.None,dialog.WindowDecorations);
        Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<Button>(),button=>button.IsEffectivelyVisible&&button.Content?.ToString() is "Close" or "Cancel" or "×" or "X");
        dialog.Close();
    }
    private static byte[] PhotoFixture()
    {
        using var bitmap=new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(320,180),new Avalonia.Vector(96,96),Avalonia.Platform.PixelFormat.Rgba8888,Avalonia.Platform.AlphaFormat.Opaque);
        using(var frame=bitmap.Lock()){var pixels=new byte[320*180*4];for(var i=0;i<pixels.Length;i+=4){pixels[i]=230;pixels[i+1]=(byte)(80+i/4/320/2);pixels[i+2]=35;pixels[i+3]=255;}System.Runtime.InteropServices.Marshal.Copy(pixels,0,frame.Address,pixels.Length);}
        using var output=new MemoryStream();bitmap.Save(output,new Avalonia.Media.Imaging.PngBitmapEncoderOptions());return output.ToArray();
    }
    private static void Save(Window window,string name){if(Environment.GetEnvironmentVariable("FIREUSER_TEST_ARTIFACTS") is not {} dir)return;Directory.CreateDirectory(dir);Dispatcher.UIThread.RunJobs();using var bitmap=window.CaptureRenderedFrame();Assert.NotNull(bitmap);using var file=File.Create(Path.Combine(dir,name));bitmap.Save(file,new Avalonia.Media.Imaging.PngBitmapEncoderOptions());}
}
