using System.Net;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using UserIdentityDesktop.Core;
using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.Service;
using UserIdentityDesktop.ViewModels;
using UserIdentityDesktop.Views;
using Xunit;
namespace UserIdentityDesktop.Tests;
public sealed class AttachmentTransferTests
{
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now=DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow()=>now;
        public void Advance(double seconds)=>now+=TimeSpan.FromSeconds(seconds);
    }
    [Fact]
    public void RealBytesSpeedStallsAndServerConfirmationRemainDistinct()
    {
        var clock=new Clock();var tracker=new TransferTracker(clock);
        using var op=tracker.Start(TransferDirection.Upload,2,2048);
        clock.Advance(1);op.Report(1024);op.FileCompleted();
        var state=Assert.Single(tracker.Snapshot());Assert.Equal(50,state.Percentage);Assert.Equal(1024,state.BytesPerSecond);Assert.Equal(1,state.CompletedFiles);
        clock.Advance(3);Assert.Equal(0,Assert.Single(tracker.Snapshot()).BytesPerSecond);
        op.Report(1024);op.FileCompleted();op.SetPhase("Waiting for server confirmation…");
        state=Assert.Single(tracker.Snapshot());Assert.False(state.Finished);Assert.Equal(99,state.Percentage);
        tracker.RemoveCompleted(state.Id);Assert.Single(tracker.Snapshot());
        op.Complete(true);state=Assert.Single(tracker.Snapshot());Assert.Equal(100,state.Percentage);Assert.True(state.Succeeded);Assert.Equal(0,state.BytesPerSecond);
        tracker.RemoveCompleted(state.Id);Assert.Empty(tracker.Snapshot());
    }
    [Fact]
    public void ConcurrentUnknownFailuresAndAccountClearDoNotResurrectTransfers()
    {
        var tracker=new TransferTracker();using var one=tracker.Start(TransferDirection.Upload,1);using var two=tracker.Start(TransferDirection.Download,3,30);
        one.Report(4);two.Report(10);two.FileCompleted();one.Dispose();
        Assert.Collection(tracker.Snapshot(),s=>{Assert.True(s.Finished);Assert.False(s.Succeeded);Assert.Null(s.TotalBytes);},s=>{Assert.Equal(10,s.Bytes);Assert.Equal(1,s.CompletedFiles);Assert.False(s.Finished);});
        tracker.Clear();two.Report(20);two.Complete(true);Assert.Empty(tracker.Snapshot());
    }
    private sealed class Handler(byte[] bytes,HttpStatusCode status=HttpStatusCode.OK):HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){
        Assert.True(request.Options.TryGetValue(AttachmentTransferRequests.UnlimitedDuration,out var unlimited)&&unlimited);
        return Task.FromResult(new HttpResponseMessage(status){Content=new ByteArrayContent(bytes)}); } }
    private static ChatService Service(byte[] bytes,HttpStatusCode status=HttpStatusCode.OK)
    {
        var auth=Substitute.For<IAuthService>();var config=Substitute.For<IConfigurationService>();config.ServerUrl.Returns("https://transfer-tests.invalid");
        auth.CreateAuthenticatedClient().Returns(_=>new HttpClient(new Handler(bytes,status)));
        return new ChatService(auth,config);
    }
    [Fact]
    public async Task StreamingDownloadReportsActualBytesAndKeepsPreviewSilent()
    {
        var bytes=new byte[200_000];Random.Shared.NextBytes(bytes);var service=Service(bytes);var path=Path.GetTempFileName();
        try
        {
            Assert.True(await service.SaveAttachmentToFileAsync(Guid.NewGuid(),path));Assert.Empty(service.Transfers.Snapshot());
            using var op=service.Transfers.Start(TransferDirection.Download,1);
            Assert.True(await service.SaveAttachmentToFileAsync(Guid.NewGuid(),path,progress:op));
            Assert.Equal(bytes,await File.ReadAllBytesAsync(path));var state=Assert.Single(service.Transfers.Snapshot());Assert.Equal(bytes.Length,state.Bytes);Assert.Equal(bytes.Length,state.TotalBytes);Assert.False(state.Finished);
            op.FileCompleted();op.Complete(true);Assert.Equal(100,Assert.Single(service.Transfers.Snapshot()).Percentage);
        }
        finally {File.Delete(path);}
    }
    private sealed class SlowDownloadHandler(string source):HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            await Task.Delay(150,ct);
            return new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(File.OpenRead(source))};
        }
    }
    [Fact]
    public async Task LargeDownloadStreamsPastRequestDeadlineWithoutMarkingBackendOffline()
    {
        var source=Path.GetTempFileName();var destination=Path.GetTempFileName();const long size=121L*1024*1024+419;
        try {
            using(var stream=File.OpenWrite(source))stream.SetLength(size);
            var queue=new UserIdentityDesktop.Data.BackendActionQueue();queue.SetAccount("download-test");queue.SetConnected(true);
            var auth=Substitute.For<IAuthService>();var config=Substitute.For<IConfigurationService>();config.ServerUrl.Returns("https://transfer-tests.invalid");
            using var transport=new SlowDownloadHandler(source);
            auth.CreateAuthenticatedClient().Returns(_=>new HttpClient(new UserIdentityDesktop.Data.QueuedAuthenticatedHandler(queue,()=>Task.FromResult<string?>("session"),transport,requestTimeout:TimeSpan.FromMilliseconds(25))){Timeout=Timeout.InfiniteTimeSpan});
            var service=new ChatService(auth,config);using var progress=service.Transfers.Start(TransferDirection.Download,1);
            Assert.True(await service.SaveAttachmentToFileAsync(Guid.NewGuid(),destination,progress:progress,cancellationToken:TestContext.Current.CancellationToken));
            Assert.True(queue.IsConnected);Assert.Equal(0,queue.PendingCount);Assert.Equal(size,new FileInfo(destination).Length);
            Assert.Equal(size,Assert.Single(service.Transfers.Snapshot()).Bytes);
        }finally{File.Delete(source);File.Delete(destination);}
    }
    [Fact]
    public async Task InvalidCiphertextNeverReplacesDestinationOrMarksDownloadSuccessful()
    {
        var service=Service(new byte[16]);var path=Path.GetTempFileName();await File.WriteAllTextAsync(path,"original");
        using var op=service.Transfers.Start(TransferDirection.Download,1);
        var file=new EncryptedFile(Guid.NewGuid().ToString(),"a.txt","text/plain",0,1048576,Convert.ToBase64String(new byte[32]),Convert.ToBase64String(new byte[8]));
        try {Assert.False(await service.SaveAttachmentToFileAsync(Guid.NewGuid(),path,file,progress:op));Assert.Equal("original",await File.ReadAllTextAsync(path));op.Dispose();Assert.False(Assert.Single(service.Transfers.Snapshot()).Succeeded);Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!,Path.GetFileName(path)+".*.partial"));}
        finally {File.Delete(path);}
    }
    [Fact]
    public async Task UploadContentReportsAfterWriteAndCountsOnlyFinishedFiles()
    {
        var tracker=new TransferTracker();using var op=tracker.Start(TransferDirection.Upload,2,150_000);
        using var first=new TransferContent(new MemoryStream(new byte[100_000]),op);using var second=new TransferContent(new MemoryStream(new byte[50_000]),op);
        using var destination=new MemoryStream();await first.CopyToAsync(destination);var state=Assert.Single(tracker.Snapshot());Assert.Equal(100_000,state.Bytes);Assert.Equal(1,state.CompletedFiles);
        await second.CopyToAsync(destination);state=Assert.Single(tracker.Snapshot());Assert.Equal(150_000,state.Bytes);Assert.Equal(2,state.CompletedFiles);Assert.Equal(99,state.Percentage);Assert.False(state.Finished);
        using var failed=new TransferContent(new MemoryStream(new byte[10]),op);
        await Assert.ThrowsAsync<NotSupportedException>(()=>failed.CopyToAsync(new MemoryStream(Array.Empty<byte>(),false)));
        Assert.Equal(150_000,Assert.Single(tracker.Snapshot()).Bytes);
    }
    private sealed class UploadHandler(TransferTracker tracker,HttpStatusCode result) : HttpMessageHandler
    {
        private static HttpResponseMessage Json(object value)=>new(HttpStatusCode.OK){Content=System.Net.Http.Json.JsonContent.Create(value)};
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            var path=request.RequestUri!.AbsolutePath;
            if(path.EndsWith("/Uploads")) return Json(new{uploadId=new string('a',32),offsets=new long[]{0,0}});
            if(path.Contains("/Files/")) {
                var index=int.Parse(path.Split('/').Last());var bytes=await request.Content!.ReadAsByteArrayAsync(token);
                Assert.Equal(index==0?116:216,bytes.Length);
                return Json(new{uploadId=new string('a',32),offsets=index==0?new long[]{116,0}:new long[]{116,216}});
            }
            if(path.EndsWith("/Complete"))
            {
                var pending=Assert.Single(tracker.Snapshot());Assert.Equal(332,pending.Bytes);Assert.Equal(332,pending.TotalBytes);Assert.Equal(2,pending.CompletedFiles);Assert.False(pending.Finished);Assert.Equal(99,pending.Percentage);
                return new HttpResponseMessage(result){Content=new StringContent("true")};
            }
            using var vector=System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"chat-vector.json"),token));
            var recovery=vector.RootElement.GetProperty("recovery");
            var json=request.RequestUri.AbsolutePath.EndsWith("/Identity") ? recovery.GetRawText() : System.Text.Json.JsonSerializer.Serialize(new {userId=recovery.GetProperty("userId").GetString(),publicKey=vector.RootElement.GetProperty("publicIdentity").GetRawText()});
            return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json,System.Text.Encoding.UTF8,"application/json")};
        }
    }
    [Theory][InlineData(HttpStatusCode.OK,true)][InlineData(HttpStatusCode.Forbidden,false)]
    public async Task FullEncryptedUploadWaitsForServerAndPublishesFinalResult(HttpStatusCode status,bool success)
    {
        using var vector=System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"chat-vector.json")));
        var owner=vector.RootElement.GetProperty("recovery").GetProperty("userId").GetGuid();var tracker=new TransferTracker();var auth=Substitute.For<IAuthService>();auth.CurrentUser.Returns(new UserData{Id=owner});
        var config=Substitute.For<IConfigurationService>();config.ServerUrl.Returns("https://transfer-tests.invalid");auth.CreateAuthenticatedClient().Returns(_=>new HttpClient(new UploadHandler(tracker,status)));
        var service=new ChatService(auth,config,transfers:tracker);var one=Path.GetTempFileName();var two=Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(one,new byte[100]);await File.WriteAllBytesAsync(two,new byte[200]);
            Assert.Equal(success,await service.SendMessageWithAttachmentsAsync(owner,"Files",new[]{one,two}));var final=Assert.Single(tracker.Snapshot());Assert.True(final.Finished);Assert.Equal(success,final.Succeeded);Assert.Equal(success?100:99,final.Percentage);
        }
        finally {File.Delete(one);File.Delete(two);}
    }
    [Fact]
    public async Task ExplicitDownloadStartsAfterPickerAndArchiveUsesOneBatch()
    {
        var tracker=new TransferTracker();var chat=Substitute.For<IChatService>();chat.Transfers.Returns(tracker);
        var picker=Substitute.For<IFileService>();var images=Substitute.For<IImageService>();
        var item=new ChatAttachmentItemViewModel(new ChatAttachment{Id=Guid.NewGuid(),FileName="report.txt",FileSize=7},chat,picker,images);
        await item.DownloadAsync();Assert.Empty(tracker.Snapshot());
        var path=Path.GetTempFileName();picker.PickSaveFileAsync(Arg.Any<string>(),Arg.Any<string>()).Returns(path);
        chat.SaveAttachmentToFileAsync(Arg.Any<Guid>(),Arg.Any<string>(),Arg.Any<EncryptedFile?>(),Arg.Any<CancellationToken>(),Arg.Any<IProgress<long>?>()).Returns(async c=>{c.ArgAt<IProgress<long>?>(4)?.Report(7);await File.WriteAllTextAsync(c.ArgAt<string>(1),"content");return true;});
        try
        {
            await item.DownloadAsync();var single=Assert.Single(tracker.Snapshot());Assert.True(single.Succeeded);Assert.Equal(7,single.Bytes);Assert.Equal(1,single.CompletedFiles);
            tracker.Clear();await AttachmentArchive.SaveAsync(new[]{item,item},path);var batch=Assert.Single(tracker.Snapshot());Assert.True(batch.Succeeded);Assert.Equal(2,batch.CompletedFiles);Assert.Equal(14,batch.Bytes);Assert.Equal(14,batch.TotalBytes);
            tracker.Clear();chat.SaveAttachmentToFileAsync(Arg.Any<Guid>(),Arg.Any<string>(),Arg.Any<EncryptedFile?>(),Arg.Any<CancellationToken>(),Arg.Any<IProgress<long>?>()).Returns(false);
            await Assert.ThrowsAsync<IOException>(()=>AttachmentArchive.SaveAsync(new[]{item},path));Assert.False(Assert.Single(tracker.Snapshot()).Succeeded);
        }
        finally {File.Delete(path);}
    }
    [AvaloniaFact]
    public async Task TransferOutlivesOrdinarySixSecondLifetimeAndUnknownTotalsStayIndeterminate()
    {
        var owner=new Window{Width=900,Height=700};owner.Show();var tracker=new TransferTracker();var presenter=new DesktopToastPresenter(owner,()=>false);presenter.ObserveTransfers(tracker);
        using var operation=tracker.Start(TransferDirection.Upload,1);Dispatcher.UIThread.RunJobs();
        var window=Assert.IsType<TransferToastWindow>(presenter.TransferWindow);
        Assert.True(Assert.Single(window.GetVisualDescendants().OfType<ProgressBar>()).IsIndeterminate);
        presenter.Show("Ordinary notification","This will expire",()=>{});
        await Task.Delay(TimeSpan.FromSeconds(6.5),TestContext.Current.CancellationToken);Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsVisible);Assert.False(Assert.Single(tracker.Snapshot()).Finished);
        // The exit animation and dispatcher can finish after the deadline on a
        // busy runner; still require the ordinary toast to close within a bound.
        var closing=System.Diagnostics.Stopwatch.StartNew();
        while(presenter.ActiveWindows.Count>0 && closing.Elapsed<TimeSpan.FromSeconds(2))
        {await Task.Delay(25,TestContext.Current.CancellationToken);Dispatcher.UIThread.RunJobs();}
        Assert.Empty(presenter.ActiveWindows);
        operation.Complete(true);Dispatcher.UIThread.RunJobs();Assert.True(window.IsVisible);Assert.Equal(100,Assert.Single(window.GetVisualDescendants().OfType<ProgressBar>()).Value);
        tracker.Clear();Dispatcher.UIThread.RunJobs();owner.Close();
    }
    [Theory]
    [InlineData(0,"0 B")][InlineData(1024,"1.0 KB")][InlineData(1048576,"1.0 MB")][InlineData(1073741824,"1.0 GB")][InlineData(1099511627776,"1.0 TB")]
    public void ByteUnitsScale(double bytes,string expected)
    { using var culture=new CultureScope();Assert.Equal(expected,TransferToastWindow.FormatBytes(bytes)); }
    private sealed class CultureScope:IDisposable
    {private readonly System.Globalization.CultureInfo saved=System.Globalization.CultureInfo.CurrentCulture;public CultureScope()=>System.Globalization.CultureInfo.CurrentCulture=System.Globalization.CultureInfo.InvariantCulture;public void Dispose()=>System.Globalization.CultureInfo.CurrentCulture=saved;}
    [AvaloniaTheory][InlineData(true)][InlineData(false)]
    public void PersistentToastSurvivesNormalNotificationsShowsBothDirectionsAndClearsWithAccount(bool dark)
    {
        var owner=new Window{Width=900,Height=700,RequestedThemeVariant=dark?ThemeVariant.Dark:ThemeVariant.Light};owner.Show();
        var tracker=new TransferTracker();var presenter=new DesktopToastPresenter(owner,()=>true);presenter.ObserveTransfers(tracker);
        using var one=tracker.Start(TransferDirection.Upload,5,1_000_000);one.Report(400_000);one.FileCompleted();one.FileCompleted();one.SetPhase("Uploading…");
        using var two=tracker.Start(TransferDirection.Download,2,2_000_000);two.Report(100_000);two.SetPhase("Downloading…");
        Dispatcher.UIThread.RunJobs();var window=Assert.IsType<TransferToastWindow>(presenter.TransferWindow);
        for(int i=0;i<5;i++)presenter.Show("Message","New chat",()=>{});Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsVisible);Assert.InRange(presenter.ActiveWindows.Count,0,3);Assert.False(window.ShowActivated);Assert.True(window.Topmost);Assert.Empty(window.GetVisualDescendants().OfType<Button>());
        var labels=window.GetVisualDescendants().OfType<TextBlock>().Select(t=>t.Text).ToArray();Assert.Contains("2 of 5 attachments uploaded",labels);Assert.Contains("0 of 2 attachments downloaded",labels);Assert.Contains("40%",labels);Assert.Contains(labels,t=>t?.Contains("Upload ")==true && t.EndsWith("/s"));
        Assert.All(window.GetVisualDescendants().OfType<ProgressBar>(),bar=>Assert.False(bar.IsIndeterminate));
        if(Environment.GetEnvironmentVariable("FIREUSER_TEST_ARTIFACTS") is {} dir){Directory.CreateDirectory(dir);using var frame=window.CaptureRenderedFrame();Assert.NotNull(frame);using var file=File.Create(Path.Combine(dir,$"transfers-{(dark?"dark":"light")}.png"));frame.Save(file,new Avalonia.Media.Imaging.PngBitmapEncoderOptions());}
        window.Close();Assert.True(window.IsVisible);
        var area=owner.Screens.Primary!.WorkingArea;Assert.True(window.Position.Y>=area.Y);Assert.True(window.Position.Y+window.Bounds.Height*owner.Screens.Primary.Scaling<=area.Bottom);
        tracker.Clear();Dispatcher.UIThread.RunJobs();Assert.False(window.IsVisible);Assert.Null(presenter.TransferWindow);
        foreach(var toast in presenter.ActiveWindows.ToArray())toast.Close();owner.Close();
    }
}
