using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using UserIdentityDesktop.Core;
using UserIdentityDesktop.Core.Models;

namespace UserIdentityDesktop.Service.Media;

/// <summary>Explicit copy/paste only. One acknowledged chunk at a time; no ambient clipboard synchronization.</summary>
internal sealed class RemoteClipboardTransfer : IDisposable
{
    private readonly IRemoteClipboard clipboard;
    private readonly Func<RemoteClipboardMessage,Task> send;
    private readonly Func<bool> authorized;
    private readonly Func<bool,Task> shortcut;
    private readonly Action<string> status;
    private readonly ITransferTracker? transfers;
    private ITransferOperation? download;
    private ITransferOperation? upload;
    private CancellationTokenRegistration downloadCancellation;
    private string? sendingId;
    private bool peerCancelled;
    private readonly CancellationTokenSource stop;
    private readonly Channel<RemoteClipboardMessage> incoming = Channel.CreateBounded<RemoteClipboardMessage>(8);
    private readonly object ackLock = new();
    private readonly object operationLock = new();
    private TaskCompletionSource? pulled;
    private string? pullId;
    private readonly Dictionary<(string,long),TaskCompletionSource> acknowledgements=new();
    private readonly SemaphoreSlim sendGate=new(1,1);
    private readonly string root = Path.Combine(Path.GetTempPath(),"FireUser-clipboard",Guid.NewGuid().ToString("N"));
    private FileStream? receiving;
    private RemoteClipboardMessage? offer;
    private long received;
    private string? lastApplied;
    private int busy;
    private int disposed;
    private readonly bool host;
    internal Task Completion { get; private set; } = Task.CompletedTask;
    internal Task Reader { get; }
    private Task operation = Task.CompletedTask;

    internal RemoteClipboardTransfer(IRemoteClipboard clipboard, Func<RemoteClipboardMessage,Task> send, Func<bool> authorized,
        Func<bool,Task> shortcut, Action<string> status, CancellationToken lifetime, bool host = false,ITransferTracker? transfers = null)
    {
        this.clipboard=clipboard;this.send=send;this.authorized=authorized;this.shortcut=shortcut;this.status=status;
        this.host=host;this.transfers=transfers;
        stop=CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        if(OperatingSystem.IsWindows())Directory.CreateDirectory(root);else Directory.CreateDirectory(root,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        Reader=Task.Run(ReadAsync);
    }
    private void Check() { stop.Token.ThrowIfCancellationRequested(); if(!authorized())throw new OperationCanceledException("Remote control permission ended."); }
    private static string Fingerprint(RemoteClipboardContent content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content.Paths.Count>0?string.Join('\n',content.Paths):"text:"+content.Text)));
    internal void Copy() => StartOperation(async () =>
    {
        status("Copying the remote selection…");
        pullId=Guid.NewGuid().ToString("N");pulled=new(TaskCreationOptions.RunContinuationsAsynchronously);
        await Send(new("request",pullId));
        await pulled.Task.WaitAsync(TimeSpan.FromMinutes(5),stop.Token);
    });
    internal void Paste() => StartOperation(async () =>
    {
        var content=await clipboard.ReadAsync(stop.Token);Check();
        if(content.Paths.Count==0&&content.Text==null){status("Copy text, files or folders before pasting.");return;}
        if(lastApplied==Fingerprint(content)) await Send(new("paste",Guid.NewGuid().ToString("N")));
        else await SendContent(content,Guid.NewGuid().ToString("N"),true);
    },waitForCopy:true);
    private void StartOperation(Func<Task> action,bool waitForCopy=false)
    {
        lock(operationLock)
        {
            if(Volatile.Read(ref disposed)!=0)return;
            if(waitForCopy&&Volatile.Read(ref busy)!=0)
            {
                // Coalesce repeated paste gestures behind the current copy, without blocking input.
                if(Interlocked.Exchange(ref pendingPaste,1)==0) _=PasteAfterCopy(operation);
                return;
            }
            if(Interlocked.CompareExchange(ref busy,1,0)!=0){status("A clipboard transfer is running. Wait for Clipboard ready.");return;}
            operation=Task.Run(async()=>
            {
                try{Check();await action();}
                catch(OperationCanceledException){}catch(Exception){status("Clipboard transfer failed. Copy the selection and try again.");}
                finally{Interlocked.Exchange(ref busy,0);}
            });
        }
    }
    private int pendingPaste;
    private async Task PasteAfterCopy(Task copy){try{await copy;Check();}catch{return;}finally{Interlocked.Exchange(ref pendingPaste,0);}Paste();}
    internal void Receive(RemoteClipboardMessage message)
    {
        if(!Guid.TryParseExact(message.Id,"N",out _))return;
        if(message.Operation=="ack")
        {
            lock(ackLock)if(acknowledgements.TryGetValue((message.Id,message.Offset),out var ack))ack.TrySetResult();
            return;
        }
        if(!incoming.Writer.TryWrite(message)){status("Clipboard transfer paused because its channel is busy.");Dispose();}
    }
    private async Task Send(RemoteClipboardMessage message)
    {
        Check();await sendGate.WaitAsync(stop.Token);
        try{Check();await send(message);}finally{sendGate.Release();}
    }
    private async Task SendAcknowledged(RemoteClipboardMessage message,long offset)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock(ackLock)acknowledgements.Add((message.Id,offset),completion);
        try{await Send(message);await completion.Task.WaitAsync(TimeSpan.FromSeconds(20),stop.Token);}
        finally{lock(ackLock)acknowledgements.Remove((message.Id,offset));}
    }
    private async Task SendContent(RemoteClipboardContent content,string id,bool paste)
    {
        if(content.Paths.Count==0&&content.Text==null)throw new IOException("The copied selection does not contain text, files or folders.");
        Check();status("Transferring clipboard…");
        var path=Path.Combine(root,id+".send");
        var pending=new Queue<Task>();
        using var transfer=content.Paths.Count>0?transfers?.Start(TransferDirection.Upload,content.Paths.Count,purpose:TransferPurpose.RemoteFile):null;
        upload=transfer;sendingId=id;
        using var cancellation=transfer?.CancellationToken.Register(()=>stop.Cancel())??default;
        try
        {
            if(content.Paths.Count==0)await File.WriteAllTextAsync(path,content.Text??"",new UTF8Encoding(false),stop.Token);
            else
            {
                using var zip=new ZipArchive(File.Create(path),ZipArchiveMode.Create);
                var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(var source in content.Paths)
                {
                    Check();var name=Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
                    if(string.IsNullOrWhiteSpace(name)||!names.Add(name))throw new IOException("Ambiguous copied item name.");
                    await AddItem(zip,source,name);
                }
            }
            await using var stream=File.OpenRead(path);
            transfer?.SetTotal(stream.Length);transfer?.SetPhase("Preparing remote files…");
            var hash=Convert.ToHexString(await SHA256.HashDataAsync(stream,stop.Token));stream.Position=0;
            await SendAcknowledged(new("begin",id,Length:stream.Length,Kind:content.Paths.Count==0?"text":"files",Hash:hash,Paste:paste,Items:Math.Max(1,content.Paths.Count)),0);
            transfer?.SetPhase("Uploading remote files…");
            var buffer=new byte[8192];long offset=0;int count;long lastProgress=0;
            while((count=await stream.ReadAsync(buffer,stop.Token))>0)
            {
                Check();pending.Enqueue(SendChunk(id,offset,buffer.AsSpan(0,count).ToArray(),transfer));offset+=count;
                if(pending.Count>=4)await pending.Dequeue();
                if(Environment.TickCount64-lastProgress>=500){lastProgress=Environment.TickCount64;status($"Transferring clipboard: {offset:N0} / {stream.Length:N0} bytes");}
            }
            while(pending.Count>0)await pending.Dequeue();
            transfer?.SetPhase("Waiting for remote verification and paste…");
            await SendAcknowledged(new("finish",id,Offset:offset),offset+1);
            transfer?.Complete(true);
            status("Clipboard ready.");
        }
        catch(OperationCanceledException){transfer?.Cancel();await NotifyCancellation(id);throw;}
        catch{await NotifyCancellation(id);throw;}
        finally{try{await Task.WhenAll(pending);}catch{}upload=null;sendingId=null;if(File.Exists(path))File.Delete(path);}
    }
    private async Task SendChunk(string id,long offset,byte[] bytes,ITransferOperation? transfer)
    {await SendAcknowledged(new("chunk",id,Offset:offset,Bytes:bytes),offset+bytes.Length);transfer?.Report(bytes.Length);}
    private async Task NotifyCancellation(string id)
    {if(peerCancelled||!authorized())return;try{await send(new("cancel",id));}catch{}}
    private async Task AddItem(ZipArchive archive,string path,string name)
    {
        Check();if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("Symbolic links cannot be copied through remote clipboard.");
        if(Directory.Exists(path))
        {
            await Task.Yield();Check();
            archive.CreateEntry(name.TrimEnd('/')+"/");
            foreach(var child in Directory.EnumerateFileSystemEntries(path))await AddItem(archive,child,name+"/"+Path.GetFileName(child));
        }
        else
        {
            var entry=archive.CreateEntry(name,CompressionLevel.NoCompression);
            await using var output=entry.Open();await using var input=File.OpenRead(path);await input.CopyToAsync(output,stop.Token);
        }
    }
    private async Task ReadAsync()
    {
        try
        {
            await foreach(var message in incoming.Reader.ReadAllAsync(stop.Token))
            {
                Check();
                if(message.Operation=="request")
                {
                    if(!host)throw new IOException("Only the shared desktop accepts copy requests.");
                    StartOperation(async()=>
                    {
                        try{var content=await clipboard.ReadAfterCopyAsync(()=>shortcut(false),stop.Token);Check();await SendContent(content,message.Id,false);}
                        catch{try{await Send(new("cancel",message.Id));}catch{}throw;}
                    });
                }
                else if(message.Operation=="cancel")
                {
                    if(message.Id==pullId||message.Id==offer?.Id||message.Id==sendingId)
                    {
                        peerCancelled=true;upload?.Cancel();download?.Cancel();stop.Cancel();
                        if(message.Id==pullId)pulled?.TrySetCanceled();
                    }
                }
                else if(message.Operation=="paste"){if(!host)throw new IOException("Only the shared desktop accepts paste requests.");await shortcut(true);}
                else if(message.Operation=="begin")
                {
                    if(receiving!=null||(!host&&(message.Id!=pullId||message.Paste))||message.Length<0||message.Items<1||message.Kind is not ("text" or "files")||message.Hash?.Length!=64)throw new IOException("Invalid clipboard offer.");
                    offer=message;received=0;receiving=new FileStream(Path.Combine(root,message.Id+".receive"),FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None,65536,true);
                    download=message.Kind=="files"?transfers?.Start(TransferDirection.Download,message.Items,message.Length,TransferPurpose.RemoteFile):null;
                    download?.SetPhase("Downloading remote files…");
                    downloadCancellation=download?.CancellationToken.Register(()=>stop.Cancel())??default;
                    status($"Receiving clipboard: {message.Length:N0} bytes");
                    await Send(new("ack",message.Id));
                }
                else if(message.Operation=="chunk")
                {
                    if(receiving==null||offer?.Id!=message.Id||message.Offset!=received||message.Bytes is not {Length:>0 and <=8192} bytes||received>offer.Length-bytes.Length)throw new IOException("Invalid clipboard offset.");
                    await receiving.WriteAsync(bytes,stop.Token);received+=bytes.Length;download?.Report(bytes.Length);await Send(new("ack",message.Id,Offset:received));
                }
                else if(message.Operation=="finish")
                {
                    if(receiving==null||offer?.Id!=message.Id||received!=offer.Length||message.Offset!=received)throw new IOException("Incomplete clipboard transfer.");
                    download?.SetPhase("Verifying remote files…");
                    receiving.Position=0;var hash=Convert.ToHexString(await SHA256.HashDataAsync(receiving,stop.Token));
                    if(!string.Equals(hash,offer.Hash,StringComparison.Ordinal))throw new IOException("Clipboard integrity mismatch.");
                    RemoteClipboardContent content;
                    receiving.Position=0;
                    if(offer.Kind=="text"){using var reader=new StreamReader(receiving,Encoding.UTF8,leaveOpen:true);content=new(await reader.ReadToEndAsync(stop.Token),Array.Empty<string>());}
                    else
                    {
                        download?.SetPhase("Preparing downloaded files…");
                        var directory=Path.Combine(root,message.Id+".items");Directory.CreateDirectory(directory);
                        using var archive=new ZipArchive(receiving,ZipArchiveMode.Read,true);
                        foreach(var entry in archive.Entries)
                        {
                            Check();var destination=SafeDestination(directory,entry.FullName);
                            if(((entry.ExternalAttributes>>16)&0xf000)==0xa000)throw new IOException("Symbolic links are not supported.");
                            if(entry.FullName.EndsWith('/'))Directory.CreateDirectory(destination);
                            else{Directory.CreateDirectory(Path.GetDirectoryName(destination)!);await using var target=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None);await using var input=entry.Open();await input.CopyToAsync(target,stop.Token);}
                        }
                        content=new(null,Directory.GetFileSystemEntries(directory));
                    }
                    Check();await clipboard.WriteAsync(content,stop.Token);Check();lastApplied=Fingerprint(content);
                    download?.SetPhase("Pasting downloaded files…");
                    if(offer.Paste)await shortcut(true);
                    await receiving.DisposeAsync();receiving=null;
                    await Send(new("ack",message.Id,Offset:received+1));
                    download?.Complete(true);downloadCancellation.Dispose();download?.Dispose();download=null;offer=null;
                    if(message.Id==pullId)pulled?.TrySetResult();status("Clipboard ready.");
                }
            }
        }
        catch(OperationCanceledException){download?.Cancel();}catch(Exception){status("Clipboard transfer failed integrity or path validation. Copy again to retry.");stop.Cancel();}
        finally
        {
            if(offer is {} incomplete)await NotifyCancellation(incomplete.Id);
            // Notify from the reader too: an upload may still be awaiting a transport write.
            // Cancelling must reach the peer without waiting for that write to finish.
            if(sendingId is {} sending)await NotifyCancellation(sending);
            downloadCancellation.Dispose();download?.Dispose();download=null;
            if(receiving!=null)await receiving.DisposeAsync();
            receiving=null;
            Dispose();
        }
    }
    internal static string SafeDestination(string root,string relative)
    {
        if(string.IsNullOrEmpty(relative)||relative.Contains('\\')||relative.Contains(':')||Path.IsPathRooted(relative)||relative.Split('/').Any(x=>x is ".." or "."||x.TrimEnd(' ','.')!=x))throw new IOException("Invalid clipboard path.");
        foreach(var part in relative.Split('/',StringSplitOptions.RemoveEmptyEntries))
        {
            var stem=part.Split('.')[0].ToUpperInvariant();
            if(part.IndexOfAny(new[]{'<','>','"','|','?','*','\0'})>=0||stem is "CON" or "PRN" or "AUX" or "NUL"||(stem.Length==4&&(stem.StartsWith("COM")||stem.StartsWith("LPT"))&&stem[3] is >= '1' and <= '9'))throw new IOException("Invalid clipboard file name.");
        }
        var target=Path.GetFullPath(Path.Combine(root,relative));
        if(!target.StartsWith(Path.GetFullPath(root)+Path.DirectorySeparatorChar,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))throw new IOException("Clipboard path escapes its temporary destination.");
        return target;
    }
    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        lock(operationLock){stop.Cancel();incoming.Writer.TryComplete();pulled?.TrySetCanceled();}
        Completion=Task.Run(Cleanup);
    }
    private async Task Cleanup()
    {
        try{await Reader;Task current;lock(operationLock)current=operation;await current;Directory.Delete(root,true);}
        catch{}finally{stop.Dispose();}
    }
}
