using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UserIdentityDesktop.Core;
namespace UserIdentityDesktop.Service;
internal sealed class TransferContent(Stream source, ITransferOperation operation) : HttpContent
{
    protected override bool TryComputeLength(out long length) { length=source.Length; return true; }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream,context,CancellationToken.None);
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token)
    {
        operation.SetPhase("Uploading…");
        var buffer=new byte[65536]; int read;
        while((read=await source.ReadAsync(buffer,token))>0) { await stream.WriteAsync(buffer.AsMemory(0,read),token); operation.Report(read); }
        operation.FileCompleted(); operation.SetPhase("Waiting for server confirmation…");
    }
}
internal sealed class TransferReadStream(Stream source, IProgress<long>? progress) : Stream
{
    public override bool CanRead=>source.CanRead; public override bool CanSeek=>false; public override bool CanWrite=>false;
    public override long Length=>throw new NotSupportedException(); public override long Position {get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
    public override int Read(byte[] buffer,int offset,int count) {var n=source.Read(buffer,offset,count);progress?.Report(n);return n;}
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default) {var n=await source.ReadAsync(buffer,token);progress?.Report(n);return n;}
    public override Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken token)=>ReadAsync(buffer.AsMemory(offset,count),token).AsTask();
    public override void Flush()=>throw new NotSupportedException(); public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
    public override void SetLength(long length)=>throw new NotSupportedException(); public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
}
