using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
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
public sealed class MessageDetailsTests
{
    private static readonly Guid Sender=Guid.NewGuid(), Jane=Guid.NewGuid(), Sam=Guid.NewGuid(), Voice=Guid.NewGuid(), OtherVoice=Guid.NewGuid();
    private static readonly DateTime Sent=new(2026,10,23,10,15,17,DateTimeKind.Utc);
    private static ChatMessage Message()=>new(){MessageId="message",Sender=Sender,Receiver=Jane,SenderName="Author",ConversationId="group",TimeSent=Sent,Attachments=new(){new(){Id=Voice,FileName="voice-note-one.wav",MimeType="audio/wav"},new(){Id=OtherVoice,FileName="second.mp3",MimeType="audio/mpeg"}},SeenDetails=new(){new(Jane,Sent.AddMinutes(1)),new(Sam,Sent.AddMinutes(2))},PlayedDetails=new(){new(Jane,Voice,Sent.AddMinutes(3)),new(Sam,OtherVoice,Sent.AddMinutes(4))}};
    private static Task<string?> Name(Guid id)=>Task.FromResult<string?>(id==Jane?"Jane Doe":id==Sam?"Sam Lee":"Author");
    [AvaloniaFact]
    public async Task DetailsShowEachReaderAndAudioListenerWithTheirOwnServerTimestamp()
    {
        var chat=Substitute.For<IChatService>();var message=Message();using var model=new MessageDetailsViewModel(message,chat,Sender,Name);await model.NamesLoaded;
        Assert.Equal("You",model.Sender);Assert.Contains(Sent.ToLocalTime().ToString("ss"),model.SentAt);Assert.Equal(new[]{"Jane Doe","Sam Lee"},model.SeenRows.Select(x=>x.Name));Assert.NotEqual(model.SeenRows[0].Time,model.SeenRows[1].Time);
        Assert.Collection(model.Playback,section=>{Assert.Contains("Voice note",section.Title);var row=Assert.Single(section.Rows);Assert.Equal("Jane Doe",row.Name);Assert.NotEqual(row.Time,model.SeenRows[0].Time);},section=>{Assert.Contains("second.mp3",section.Title);Assert.Equal("Sam Lee",Assert.Single(section.Rows).Name);});
    }
    [AvaloniaFact]
    public async Task EmptyLegacyAndSuppressedReceiptsNeverInventPeopleOrTimes()
    {
        var chat=Substitute.For<IChatService>();var m=Message();m.SeenDetails=null;m.PlayedDetails=null;using(var empty=new MessageDetailsViewModel(m,chat,Sender,Name)){Assert.True(empty.NoReaders);Assert.All(empty.Playback,x=>Assert.True(x.IsEmpty));}
        m.ConversationId=null;m.Seen=true;using(var legacy=new MessageDetailsViewModel(m,chat,Sender,Name)){await legacy.NamesLoaded;var row=Assert.Single(legacy.SeenRows);Assert.Equal("Jane Doe",row.Name);Assert.Equal("Seen · time unavailable",row.Time);}
        m.ConversationId="group";using(var group=new MessageDetailsViewModel(m,chat,Sender,Name)){Assert.Empty(group.SeenRows);}
        m.SuppressReadReceipts=true;using var suppressed=new MessageDetailsViewModel(m,chat,Sender,Name);Assert.False(suppressed.ShowReceipts);Assert.Empty(suppressed.SeenRows);Assert.Empty(suppressed.Playback);
    }
    [AvaloniaFact]
    public async Task OpenDetailsMergeLivePlaybackIgnoreOtherMessagesAndUnsubscribeWhenClosed()
    {
        var chat=Substitute.For<IChatService>();var message=Message();message.PlayedDetails=null;using var model=new MessageDetailsViewModel(message,chat,Sender,Name);await model.NamesLoaded;
        chat.AttachmentPlayed+=Raise.Event<Action<PlaybackReceipt>>(new PlaybackReceipt("other",Voice,Jane,Sent));Dispatcher.UIThread.RunJobs();Assert.All(model.Playback,x=>Assert.True(x.IsEmpty));
        var receipt=new PlaybackReceipt("message",Voice,Jane,Sent.AddMinutes(5));chat.AttachmentPlayed+=Raise.Event<Action<PlaybackReceipt>>(receipt);chat.AttachmentPlayed+=Raise.Event<Action<PlaybackReceipt>>(receipt);Dispatcher.UIThread.RunJobs();Assert.Equal("Jane Doe",Assert.Single(model.Playback[0].Rows).Name);
        var refreshed=Message();refreshed.PlayedDetails=null;chat.MessageUpdated+=Raise.Event<Action<ChatMessage>>(refreshed);Dispatcher.UIThread.RunJobs();Assert.Single(model.Playback[0].Rows);
        model.Dispose();chat.AttachmentPlayed+=Raise.Event<Action<PlaybackReceipt>>(new PlaybackReceipt("message",OtherVoice,Sam,Sent));Dispatcher.UIThread.RunJobs();Assert.Empty(model.Playback[1].Rows);
    }
    [AvaloniaFact]
    public async Task DetailsRespectClockPreferenceAndLegacyReadBoundaryIsNotASeenTimestamp()
    {
        var old=ClockDisplaySettings.Shared.TwelveHour;var chat=Substitute.For<IChatService>();var m=Message();m.ConversationId=null;m.SeenDetails=null;m.PlayedDetails=null;
        using var model=new MessageDetailsViewModel(m,chat,Sender,Name);
        try
        {
            ClockDisplaySettings.Shared.TwelveHour=false;Dispatcher.UIThread.RunJobs();Assert.EndsWith(Sent.ToLocalTime().ToString("HH:mm:ss"),model.SentAt);
            ClockDisplaySettings.Shared.TwelveHour=true;Dispatcher.UIThread.RunJobs();Assert.EndsWith(Sent.ToLocalTime().ToString("h:mm:ss tt"),model.SentAt);
            chat.ReadReceiptReceived+=Raise.Event<Action<Guid,Guid,DateTime>>(Jane,Sender,Sent);Dispatcher.UIThread.RunJobs();await model.NamesLoaded;Assert.Equal("Seen · time unavailable",Assert.Single(model.SeenRows).Time);
        }
        finally{ClockDisplaySettings.Shared.TwelveHour=old;Dispatcher.UIThread.RunJobs();}
    }
    [AvaloniaFact]
    public async Task VoicePlaybackReportingExcludesSelfPreviewsDownloadsAndNonAudio()
    {
        var service=Substitute.For<IChatService>();var files=Substitute.For<IFileService>();var images=Substitute.For<IImageService>();var m=Message();m.SuppressReadReceipts=false;
        service.SaveAttachmentToFileAsync(Arg.Any<Guid>(),Arg.Any<string>(),Arg.Any<EncryptedFile?>(),Arg.Any<CancellationToken>(),Arg.Any<IProgress<long>?>()).Returns(async c=>{await File.WriteAllBytesAsync(c.ArgAt<string>(1),new byte[]{1,2,3});return true;});
        var received=new ChatMessageItemViewModel(m,false,service,files,images);var preview=await received.Attachments[0].PrepareAudioPlaybackAsync();File.Delete(preview);
        var destination=Path.GetTempFileName();files.PickSaveFileAsync(Arg.Any<string>(),Arg.Any<string>()).Returns(destination);try{await received.Attachments[0].DownloadAsync();}finally{File.Delete(destination);}
        await service.DidNotReceive().MarkAttachmentPlayedAsync(Arg.Any<string>(),Arg.Any<Guid>());
        await received.Attachments[0].ReportPlaybackStartedAsync();await service.Received(1).MarkAttachmentPlayedAsync("message",Voice);
        service.ClearReceivedCalls();var sent=new ChatMessageItemViewModel(m,true,service,files,images);await sent.Attachments[0].ReportPlaybackStartedAsync();
        m.SuppressReadReceipts=true;var suppressed=new ChatMessageItemViewModel(m,false,service,files,images);await suppressed.Attachments[0].ReportPlaybackStartedAsync();
        var document=new ChatAttachmentItemViewModel(new ChatAttachment{Id=Guid.NewGuid(),MimeType="application/pdf"},service,files,images){ReceiptMessageId="message",CanReportPlayback=true};await document.ReportPlaybackStartedAsync();await service.DidNotReceive().MarkAttachmentPlayedAsync(Arg.Any<string>(),Arg.Any<Guid>());
    }
    [AvaloniaFact]
    public async Task LatestSnapshotAndDirectReadEventsLoadExactReceiptTimesWithoutPolling()
    {
        var chat=Substitute.For<IChatService>();var original=Message();original.ConversationId=null;original.SeenDetails=null;original.Seen=false;
        var snapshot=Message();snapshot.ConversationId=null;snapshot.SeenDetails=new(){new(Jane,Sent.AddMinutes(10))};
        chat.GetMessageAsync("message",Arg.Any<CancellationToken>()).Returns(snapshot);
        using var model=new MessageDetailsViewModel(original,chat,Sender,Name);await model.RefreshTask;await model.NamesLoaded;
        var first=Assert.Single(model.SeenRows);Assert.Equal("Jane Doe",first.Name);Assert.DoesNotContain("unavailable",first.Time);
        await chat.Received(1).GetMessageAsync("message",Arg.Any<CancellationToken>());
        chat.ReadReceiptReceived+=Raise.Event<Action<Guid,Guid,DateTime>>(Jane,Sender,Sent);Dispatcher.UIThread.RunJobs();await model.RefreshTask;
        await chat.Received(2).GetMessageAsync("message",Arg.Any<CancellationToken>());Assert.Equal(first.Time,Assert.Single(model.SeenRows).Time);
    }
    [AvaloniaFact]
    public async Task SnapshotErrorsAndLateNameLookupsAfterCloseDoNotInventReceiptsOrUpdateClosedView()
    {
        var chat=Substitute.For<IChatService>();chat.GetMessageAsync(Arg.Any<string>(),Arg.Any<CancellationToken>()).Returns(Task.FromException<ChatMessage?>(new IOException("offline")));
        var pending=new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var m=Message();m.SeenDetails=null;m.PlayedDetails=null;var model=new MessageDetailsViewModel(m,chat,Sender,_=>pending.Task);
        await model.RefreshTask;Assert.NotNull(model.RefreshError);Assert.True(model.NoReaders);model.Dispose();pending.SetResult("Late name");await model.NamesLoaded;Assert.Empty(model.SeenRows);
    }
    [AvaloniaFact]
    public async Task ChatCacheRetainsPlaybackEventsAndFactoryResolvesMissingNamesThroughProfiles()
    {
        var auth=Substitute.For<IAuthService>();auth.CurrentUser.Returns(new UserData{Id=Sender});var chat=Substitute.For<IChatService>();var users=Substitute.For<IUserService>();
        users.GetUserProfileAsync(Jane).Returns(new ViewUserProfileDto{FirstName="Jane",LastName="Doe"});users.GetUserProfileAsync(Sam).Returns(new ViewUserProfileDto{FirstName="Sam",LastName="Lee"});
        var vm=new ChatViewModel(auth,chat,users,Substitute.For<IImageService>(),Substitute.For<IFileService>(),Substitute.For<INotificationService>());
        var m=Message();m.PlayedDetails=null;var item=new ChatMessageItemViewModel(m,true,chat,Substitute.For<IFileService>(),Substitute.For<IImageService>());vm.Messages.Add(item);
        try
        {
            chat.AttachmentPlayed+=Raise.Event<Action<PlaybackReceipt>>(new PlaybackReceipt("message",Voice,Jane,Sent.AddMinutes(5)));Dispatcher.UIThread.RunJobs();Assert.Single(item.Message.PlayedDetails!);
            var fresh=Message();fresh.PlayedDetails=null;chat.MessageUpdated+=Raise.Event<Action<ChatMessage>>(fresh);Dispatcher.UIThread.RunJobs();Assert.Single(vm.Messages[0].Message.PlayedDetails!);
            using var details=vm.CreateMessageDetails(vm.Messages[0]);await details.NamesLoaded;Assert.Equal("Jane Doe",details.SeenRows[0].Name);Assert.Equal("Sam Lee",details.SeenRows[1].Name);await users.Received(1).GetUserProfileAsync(Jane);
        }
        finally{vm.UnregisterEvents();}
    }
    [AvaloniaTheory][InlineData(true)][InlineData(false)]
    public async Task NativeDetailsWindowRendersSentSeenAndPlayedNamesAndTimesWithoutBodyCloseButton(bool dark)
    {
        using var model=new MessageDetailsViewModel(Message(),Substitute.For<IChatService>(),Sender,Name);await model.NamesLoaded;var window=new MessageDetailsWindow(model){RequestedThemeVariant=dark?ThemeVariant.Dark:ThemeVariant.Light};window.Show();Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.Equal(WindowDecorations.Full,window.WindowDecorations);Assert.Empty(window.GetVisualDescendants().OfType<Button>());var labels=window.GetVisualDescendants().OfType<TextBlock>().Select(x=>x.Text).ToArray();Assert.Contains("Sent",labels);Assert.Contains("Seen by",labels);Assert.Contains("Jane Doe",labels);Assert.Contains("Sam Lee",labels);Assert.Contains(labels,x=>x?.StartsWith("Played by")==true);
            if(Environment.GetEnvironmentVariable("FIREUSER_TEST_ARTIFACTS") is {} dir){Directory.CreateDirectory(dir);using var frame=window.CaptureRenderedFrame();Assert.NotNull(frame);using var file=File.Create(Path.Combine(dir,$"message-details-{(dark?"dark":"light")}.png"));frame.Save(file,new Avalonia.Media.Imaging.PngBitmapEncoderOptions());}
        }
        finally{window.Close();}
    }
    [Fact]
    public void ReceiptWireContractsKeepUserAttachmentAndTimestamp()
    {
        var m=JsonSerializer.Deserialize<ChatMessage>("{\"playedDetails\":[{\"userId\":\""+Jane+"\",\"attachmentId\":\""+Voice+"\",\"playedAt\":\"2026-10-23T10:15:17Z\"}]}",ApiPayload.Json)!;var row=Assert.Single(m.PlayedDetails!);Assert.Equal(Jane,row.UserId);Assert.Equal(Voice,row.AttachmentId);Assert.Equal(Sent,row.PlayedAt);
    }
}
