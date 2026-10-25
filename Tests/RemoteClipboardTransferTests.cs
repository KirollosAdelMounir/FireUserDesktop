using UserIdentityDesktop.Core;
using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.Service.Media;
using UserIdentityDesktop.Service;
using Xunit;

namespace UserIdentityDesktop.Tests;

public sealed class RemoteClipboardTransferTests
{
    private static Task Deliver(RemoteClipboardTransfer target,RemoteClipboardMessage message)
    {
        var packet=new RemotePacket("clipboard",Scope:new string('a',32),Grant:new string('b',32),Clipboard:message);
        var wire=System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(packet,UserIdentityDesktop.Data.ApiPayload.Json);
        Assert.InRange(wire.Length,1,15360);
        target.Receive(System.Text.Json.JsonSerializer.Deserialize<RemotePacket>(wire,UserIdentityDesktop.Data.ApiPayload.Json)!.Clipboard!);
        return Task.CompletedTask;
    }
    private sealed class Clipboard(RemoteClipboardContent content) : IRemoteClipboard
    {
        internal RemoteClipboardContent Content=content;
        internal readonly TaskCompletionSource<RemoteClipboardContent> Written=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<RemoteClipboardContent> ReadAsync(CancellationToken ct){ct.ThrowIfCancellationRequested();return Task.FromResult(Content);}
        public Task WriteAsync(RemoteClipboardContent value,CancellationToken ct){ct.ThrowIfCancellationRequested();Content=value;Written.TrySetResult(value);return Task.CompletedTask;}
        public async Task<RemoteClipboardContent> ReadAfterCopyAsync(Func<Task> copy,CancellationToken ct){await copy();return await ReadAsync(ct);}
    }
    [Fact]
    public async Task Paste_StreamsTextAndAppliesBeforeRemoteShortcut()
    {
        var local=new Clipboard(new("Unicode text: مرحباً 🌍",Array.Empty<string>()));var remote=new Clipboard(new("old",Array.Empty<string>()));
        RemoteClipboardTransfer? sender=null,receiver=null;
        var pasted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tracker=new TransferTracker();
        try
        {
            sender=new(local,m=>Deliver(receiver!,m),()=>true,_=>Task.CompletedTask,_=>{},TestContext.Current.CancellationToken,transfers:tracker);
            receiver=new(remote,m=>Deliver(sender!,m),()=>true,paste=>{Assert.True(paste);Assert.Equal(local.Content.Text,remote.Content.Text);pasted.TrySetResult();return Task.CompletedTask;},_=>{},TestContext.Current.CancellationToken,true,tracker);
            sender.Paste();await pasted.Task.WaitAsync(TimeSpan.FromSeconds(10),TestContext.Current.CancellationToken);
            Assert.Equal(local.Content.Text,remote.Content.Text);
            Assert.Empty(tracker.Snapshot());
        }
        finally{sender?.Dispose();receiver?.Dispose();if(sender!=null)await sender.Completion;if(receiver!=null)await receiver.Completion;}
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task FilesAndFolders_TransferBothDirectionsAndCleanOnRevoke(bool paste)
    {
        var directory=Path.Combine(Path.GetTempPath(),"clipboard-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(directory,"nested","empty"));
        var expected=Enumerable.Range(0,180000).Select(x=>(byte)(x%251)).ToArray();await File.WriteAllBytesAsync(Path.Combine(directory,"nested","data.bin"),expected,TestContext.Current.CancellationToken);
        var source=new RemoteClipboardContent(null,new[]{directory});var empty=new RemoteClipboardContent(null,Array.Empty<string>());
        var local=new Clipboard(paste?source:empty);var remote=new Clipboard(paste?empty:source);
        RemoteClipboardTransfer? sender=null,receiver=null;string? receivedRoot=null;
        var localTracker=new TransferTracker();var remoteTracker=new TransferTracker();
        var readyLocal=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var readyRemote=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress=new System.Collections.Concurrent.ConcurrentBag<TransferSnapshot>();
        Task Wire(RemoteClipboardTransfer target,RemoteClipboardMessage message){foreach(var state in localTracker.Snapshot().Concat(remoteTracker.Snapshot()))progress.Add(state);return Deliver(target,message);}
        try
        {
            sender=new(local,m=>Wire(receiver!,m),()=>true,_=>Task.CompletedTask,s=>{if(s=="Clipboard ready.")readyLocal.TrySetResult();},TestContext.Current.CancellationToken,transfers:localTracker);
            receiver=new(remote,m=>Wire(sender!,m),()=>true,_=>Task.CompletedTask,s=>{if(s=="Clipboard ready.")readyRemote.TrySetResult();},TestContext.Current.CancellationToken,true,remoteTracker);
            if(paste)sender.Paste();else sender.Copy();var content=await (paste?remote:local).Written.Task.WaitAsync(TimeSpan.FromSeconds(10),TestContext.Current.CancellationToken);
            receivedRoot=Assert.Single(content.Paths);Assert.True(Directory.Exists(Path.Combine(receivedRoot,"nested","empty")));
            Assert.Equal(expected,await File.ReadAllBytesAsync(Path.Combine(receivedRoot,"nested","data.bin"),TestContext.Current.CancellationToken));
            await Task.WhenAll(readyLocal.Task,readyRemote.Task).WaitAsync(TimeSpan.FromSeconds(10),TestContext.Current.CancellationToken);
            var sent=Assert.Single((paste?localTracker:remoteTracker).Snapshot());var downloaded=Assert.Single((paste?remoteTracker:localTracker).Snapshot());
            Assert.Equal(TransferDirection.Upload,sent.Direction);Assert.Equal(TransferDirection.Download,downloaded.Direction);
            Assert.True(sent.Succeeded);Assert.True(downloaded.Succeeded);Assert.Equal(sent.TotalBytes,sent.Bytes);Assert.Equal(sent.Bytes,downloaded.Bytes);
            Assert.Equal(TransferPurpose.RemoteFile,sent.Purpose);Assert.Equal(TransferPurpose.RemoteFile,downloaded.Purpose);
            Assert.Contains(progress,s=>s.Direction==TransferDirection.Upload&&!s.Finished&&s.Bytes>0&&s.Bytes<s.TotalBytes);
            Assert.Contains(progress,s=>s.Direction==TransferDirection.Download&&!s.Finished&&s.Bytes>0&&s.Bytes<s.TotalBytes);
        }
        finally{sender?.Dispose();receiver?.Dispose();if(sender!=null)await sender.Completion;if(receiver!=null)await receiver.Completion;Directory.Delete(directory,true);}
        Assert.False(Directory.Exists(receivedRoot));
    }
    [Fact]
    public async Task IntegrityFailure_DoesNotWriteClipboardOrPaste()
    {
        var clipboard=new Clipboard(new("keep",Array.Empty<string>()));var id=Guid.NewGuid().ToString("N");var pastes=0;
        using var transfer=new RemoteClipboardTransfer(clipboard,_=>Task.CompletedTask,()=>true,_=>{pastes++;return Task.CompletedTask;},_=>{},TestContext.Current.CancellationToken,true);
        transfer.Receive(new("begin",id,Length:1,Kind:"text",Hash:new string('0',64),Paste:true));
        transfer.Receive(new("chunk",id,Bytes:new byte[]{42}));transfer.Receive(new("finish",id,Offset:1));
        await transfer.Reader.WaitAsync(TimeSpan.FromSeconds(10),TestContext.Current.CancellationToken);
        Assert.Equal("keep",clipboard.Content.Text);Assert.False(clipboard.Written.Task.IsCompleted);Assert.Equal(0,pastes);
        transfer.Dispose();await transfer.Completion;
    }
    [Fact]
    public async Task RevokedGrant_CannotReadOrApplyClipboard()
    {
        var clipboard=new Clipboard(new("private",Array.Empty<string>()));var sent=0;
        using var transfer=new RemoteClipboardTransfer(clipboard,_=>{sent++;return Task.CompletedTask;},()=>false,_=>throw new Exception("Must not inject"),_=>{},TestContext.Current.CancellationToken,true);
        transfer.Receive(new("request",Guid.NewGuid().ToString("N")));
        await transfer.Reader.WaitAsync(TimeSpan.FromSeconds(10),TestContext.Current.CancellationToken);
        Assert.Equal(0,sent);Assert.False(clipboard.Written.Task.IsCompleted);transfer.Dispose();await transfer.Completion;
    }
    [Fact]
    public async Task UnsolicitedReverseCopy_DoesNotExposeControllerClipboard()
    {
        var clipboard=new Clipboard(new("local private clipboard",Array.Empty<string>()));var sent=0;var shortcuts=0;
        using var transfer=new RemoteClipboardTransfer(clipboard,_=>{sent++;return Task.CompletedTask;},()=>true,_=>{shortcuts++;return Task.CompletedTask;},_=>{},TestContext.Current.CancellationToken);
        transfer.Receive(new("request",Guid.NewGuid().ToString("N")));
        await transfer.Reader.WaitAsync(TimeSpan.FromSeconds(10),TestContext.Current.CancellationToken);
        Assert.Equal(0,sent);Assert.Equal(0,shortcuts);Assert.False(clipboard.Written.Task.IsCompleted);transfer.Dispose();await transfer.Completion;
    }
    [Fact]
    public async Task ValidHashWithTraversalArchive_DoesNotExposeOrPasteFiles()
    {
        using var bytes=new MemoryStream();
        using(var zip=new System.IO.Compression.ZipArchive(bytes,System.IO.Compression.ZipArchiveMode.Create,true))
        {await using var entry=zip.CreateEntry("../escaped.txt").Open();await entry.WriteAsync(new byte[]{1,2,3},TestContext.Current.CancellationToken);}
        var payload=bytes.ToArray();var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload));
        var clipboard=new Clipboard(new("keep",Array.Empty<string>()));var pastes=0;var id=Guid.NewGuid().ToString("N");
        using var transfer=new RemoteClipboardTransfer(clipboard,_=>Task.CompletedTask,()=>true,_=>{pastes++;return Task.CompletedTask;},_=>{},TestContext.Current.CancellationToken,true);
        transfer.Receive(new("begin",id,Length:payload.Length,Kind:"files",Hash:hash,Paste:true));
        transfer.Receive(new("chunk",id,Bytes:payload));transfer.Receive(new("finish",id,Offset:payload.Length));
        await transfer.Reader.WaitAsync(TimeSpan.FromSeconds(10),TestContext.Current.CancellationToken);
        Assert.False(clipboard.Written.Task.IsCompleted);Assert.Equal(0,pastes);Assert.Equal("keep",clipboard.Content.Text);
        transfer.Dispose();await transfer.Completion;
    }
    [Fact]
    public void ClipboardChunk_RoundTripsWithinTransportPacketBudget()
    {
        var content=Enumerable.Range(0,8192).Select(x=>(byte)(x%251)).ToArray();
        var packet=new RemotePacket("clipboard",Scope:Guid.NewGuid().ToString("N"),Grant:Guid.NewGuid().ToString("N"),Clipboard:new("chunk",Guid.NewGuid().ToString("N"),Offset:5_000_000_000,Bytes:content));
        var bytes=System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(packet,UserIdentityDesktop.Data.ApiPayload.Json);
        Assert.True(bytes.Length<15360);
        var copy=System.Text.Json.JsonSerializer.Deserialize<RemotePacket>(bytes,UserIdentityDesktop.Data.ApiPayload.Json)!;
        Assert.Equal(content,copy.Clipboard!.Bytes);Assert.Equal(5_000_000_000,copy.Clipboard.Offset);Assert.Equal(packet.Grant,copy.Grant);
    }
    [Theory]
    [InlineData("../outside")][InlineData("/absolute")][InlineData("C:/outside")][InlineData("nested/../../outside")][InlineData("folder\\outside")][InlineData("file:stream")][InlineData("NUL.txt")][InlineData("folder/COM1")]
    public void UnsafeArchivePaths_AreRejected(string path) => Assert.Throws<IOException>(()=>RemoteClipboardTransfer.SafeDestination(Path.Combine(Path.GetTempPath(),"clipboard-root"),path));

    [Theory][InlineData(false,false)][InlineData(false,true)][InlineData(true,false)][InlineData(true,true)]
    public async Task CancelRemoteFileTransfer_FromEitherDeviceStopsBothAndNeverPastes(bool cancelDownload,bool paste)
    {
        var path=Path.GetTempFileName();await File.WriteAllBytesAsync(path,new byte[180000],TestContext.Current.CancellationToken);
        var source=new RemoteClipboardContent(null,new[]{path});var original=new RemoteClipboardContent("original",Array.Empty<string>());
        var local=new Clipboard(paste?source:original);var remote=new Clipboard(paste?original:source);
        var localTracker=new TransferTracker();var remoteTracker=new TransferTracker();
        var blocked=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RemoteClipboardTransfer? sender=null,receiver=null;var pasted=0;
        async Task Wire(RemoteClipboardTransfer target,RemoteClipboardMessage message){await Deliver(target,message);if(message.Operation=="chunk"&&message.Offset==0){blocked.TrySetResult();await release.Task;}}
        try
        {
            sender=new(local,m=>Wire(receiver!,m),()=>true,_=>Task.CompletedTask,_=>{},TestContext.Current.CancellationToken,transfers:localTracker);
            receiver=new(remote,m=>Wire(sender!,m),()=>true,isPaste=>{if(isPaste)pasted++;return Task.CompletedTask;},_=>{},TestContext.Current.CancellationToken,true,remoteTracker);
            if(paste)sender.Paste();else sender.Copy();await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5),TestContext.Current.CancellationToken);
            var tracker=cancelDownload?(paste?remoteTracker:localTracker):(paste?localTracker:remoteTracker);tracker.Cancel(Assert.Single(tracker.Snapshot()).Id);
            await Task.WhenAll(sender.Reader,receiver.Reader).WaitAsync(TimeSpan.FromSeconds(5),TestContext.Current.CancellationToken);release.TrySetResult();
            sender.Dispose();receiver.Dispose();await Task.WhenAll(sender.Completion,receiver.Completion).WaitAsync(TimeSpan.FromSeconds(5),TestContext.Current.CancellationToken);
            var destination=paste?remote:local;Assert.Equal(0,pasted);Assert.False(destination.Written.Task.IsCompleted);Assert.Equal("original",destination.Content.Text);
            Assert.All(localTracker.Snapshot().Concat(remoteTracker.Snapshot()),s=>{Assert.True(s.Finished);Assert.True(s.CancellationRequested);Assert.False(s.Succeeded);Assert.Equal("Cancelled",s.Phase);});
        }
        finally{release.TrySetResult();sender?.Dispose();receiver?.Dispose();if(sender!=null)await sender.Completion;if(receiver!=null)await receiver.Completion;File.Delete(path);}
    }
    [Fact]
    public async Task InvalidFileHash_FinishesDownloadToastAsFailedWithoutPasting()
    {
        var tracker=new TransferTracker();var clipboard=new Clipboard(new("original",Array.Empty<string>()));var id=Guid.NewGuid().ToString("N");
        using var transfer=new RemoteClipboardTransfer(clipboard,_=>Task.CompletedTask,()=>true,_=>throw new InvalidOperationException("Must never paste"),_=>{},TestContext.Current.CancellationToken,true,tracker);
        transfer.Receive(new("begin",id,Length:3,Kind:"files",Hash:new string('0',64)));
        transfer.Receive(new("chunk",id,Bytes:new byte[]{1,2,3}));transfer.Receive(new("finish",id,Offset:3));
        await transfer.Reader.WaitAsync(TimeSpan.FromSeconds(5),TestContext.Current.CancellationToken);await transfer.Completion;
        var state=Assert.Single(tracker.Snapshot());Assert.True(state.Finished);Assert.False(state.Succeeded);Assert.False(state.CancellationRequested);Assert.False(clipboard.Written.Task.IsCompleted);
    }
}
