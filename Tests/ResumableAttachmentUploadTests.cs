using System.Net;
using System.Net.Http.Json;
using UserIdentityDesktop.Core;
using UserIdentityDesktop.Service;
using Xunit;
namespace UserIdentityDesktop.Tests;
public sealed class ResumableAttachmentUploadTests
{
    private const string Id="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private sealed class Server : HttpMessageHandler
    {
        public long Confirmed, Expected;
        public int Puts, StatusReads, Completions;
        public int? LostPrefix;
        public bool AlwaysLose;
        public bool Reject, LoseCompletion, BadOffset, BadSession;
        public readonly List<long> SentOffsets=[];
        public readonly MemoryStream Received=new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.True(request.Options.TryGetValue(AttachmentTransferRequests.UnlimitedDuration,out var unlimited)&&unlimited);
            var path=request.RequestUri!.AbsolutePath;
            if(request.Method==HttpMethod.Post&&path.EndsWith("/Uploads")) {
                var create=await request.Content!.ReadFromJsonAsync<AttachmentUploadRequest>(ct);
                Assert.NotNull(create);Assert.Equal(Expected,Assert.Single(create.Files).Size);
                return Json(new AttachmentUploadStatus(BadSession?"../other":Id,[0]));
            }
            Assert.StartsWith("/api/Chat/Uploads/"+Id,path);
            if(request.Method==HttpMethod.Get){StatusReads++;return Json(new AttachmentUploadStatus(Id,[Confirmed]));}
            if(request.Method==HttpMethod.Put){
                Puts++;var offset=long.Parse(request.RequestUri.Query.Split('=')[1]);SentOffsets.Add(offset);Assert.Equal(Confirmed,offset);
                var bytes=await request.Content!.ReadAsByteArrayAsync(ct);Assert.InRange(bytes.Length,1,1024*1024);
                if(Reject)return new(HttpStatusCode.Forbidden);
                var count=AlwaysLose?0:LostPrefix is {} prefix?Math.Min(prefix,bytes.Length):bytes.Length;
                Received.Write(bytes,0,count);Confirmed+=count;
                if(AlwaysLose||LostPrefix!=null){LostPrefix=null;throw new HttpRequestException("response lost");}
                return Json(new AttachmentUploadStatus(Id,[BadOffset?Expected+1:Confirmed]));
            }
            Assert.EndsWith("/Complete",path);Completions++;
            Assert.Equal(Expected,Confirmed);
            if(LoseCompletion)throw new HttpRequestException("completion response lost");
            return Json(true);
        }
        private static HttpResponseMessage Json(object value)=>new(HttpStatusCode.OK){Content=JsonContent.Create(value)};
    }
    private static AttachmentUploadRequest Request(long size)=>new(Guid.NewGuid(),Guid.NewGuid(),null,"encrypted",null,[new("opaque.fua","application/octet-stream",size)]);
    [Fact]
    public async Task UploadAbove121MiBUsesBoundedRequestsAndOnlyCompletesAfterAllConfirmedBytes()
    {
        var path=Path.GetTempFileName();const long size=121L*1024*1024+417;
        try{
            using(var file=File.OpenWrite(path))file.SetLength(size);
            using var server=new Server{Expected=size};using var client=new HttpClient(server);
            var tracker=new TransferTracker();using var progress=tracker.Start(TransferDirection.Upload,1);
            await ResumableAttachmentUpload.SendAsync(client,"https://upload.invalid",Request(size),[path],progress,TestContext.Current.CancellationToken);
            Assert.Equal(122,server.Puts);Assert.Equal(0,server.StatusReads);Assert.Equal(1,server.Completions);Assert.Equal(size,server.Received.Length);
            var state=Assert.Single(tracker.Snapshot());Assert.Equal(size,state.Bytes);Assert.Equal(1,state.CompletedFiles);Assert.False(state.Finished);Assert.Equal(99,state.Percentage);
        }finally{File.Delete(path);}
    }
    [Theory][InlineData(0)][InlineData(333)][InlineData(1048576)]
    public async Task LostChunkResponseQueriesConfirmedOffsetWithoutDuplicatingBytes(int committed)
    {
        var path=Path.GetTempFileName();var bytes=new byte[1048576+431];Random.Shared.NextBytes(bytes);
        try{
            await File.WriteAllBytesAsync(path,bytes);using var server=new Server{Expected=bytes.Length,LostPrefix=committed};using var client=new HttpClient(server);
            var tracker=new TransferTracker();using var progress=tracker.Start(TransferDirection.Upload,1);
            await ResumableAttachmentUpload.SendAsync(client,"https://upload.invalid",Request(bytes.Length),[path],progress,TestContext.Current.CancellationToken);
            Assert.Equal(bytes,server.Received.ToArray());Assert.Equal(1,server.StatusReads);Assert.Equal(committed,server.SentOffsets[1]);Assert.Equal(1,server.Completions);
            Assert.Equal(bytes.Length,Assert.Single(tracker.Snapshot()).Bytes);
        }finally{File.Delete(path);}
    }
    [Theory][InlineData(true,false)][InlineData(false,true)]
    public async Task InvalidSessionOrOffsetNeverPublishes(bool badSession,bool badOffset)
    {
        var path=Path.GetTempFileName();
        try{
            await File.WriteAllBytesAsync(path,new byte[19]);using var server=new Server{Expected=19,BadSession=badSession,BadOffset=badOffset};using var client=new HttpClient(server);
            using var progress=new TransferTracker().Start(TransferDirection.Upload,1);
            await Assert.ThrowsAsync<InvalidDataException>(()=>ResumableAttachmentUpload.SendAsync(client,"https://upload.invalid",Request(19),[path],progress));
            Assert.Equal(0,server.Completions);Assert.Equal(0,server.StatusReads);
        }finally{File.Delete(path);}
    }
    [Fact]
    public async Task ForbiddenChunkIsNotRetriedAndLostCompletionIsNeverReplayed()
    {
        var path=Path.GetTempFileName();
        try{
            await File.WriteAllBytesAsync(path,new byte[19]);
            foreach(var reject in new[]{true,false}){
                using var server=new Server{Expected=19,Reject=reject,LoseCompletion=!reject};using var client=new HttpClient(server);
                using var progress=new TransferTracker().Start(TransferDirection.Upload,1);
                await Assert.ThrowsAsync<HttpRequestException>(()=>ResumableAttachmentUpload.SendAsync(client,"https://upload.invalid",Request(19),[path],progress));
                Assert.Equal(1,server.Puts);Assert.Equal(0,server.StatusReads);Assert.Equal(reject?0:1,server.Completions);
            }
        }finally{File.Delete(path);}
    }
    [Fact]
    public async Task RepeatedNetworkFailuresStopAfterBoundedRecoveryWithoutPublishing()
    {
        var path=Path.GetTempFileName();
        try {
            await File.WriteAllBytesAsync(path,new byte[19]);using var server=new Server{Expected=19,AlwaysLose=true};using var client=new HttpClient(server);
            using var progress=new TransferTracker().Start(TransferDirection.Upload,1);
            await Assert.ThrowsAsync<HttpRequestException>(()=>ResumableAttachmentUpload.SendAsync(client,"https://upload.invalid",Request(19),[path],progress,TestContext.Current.CancellationToken));
            Assert.Equal(4,server.Puts);Assert.Equal(3,server.StatusReads);Assert.Equal(0,server.Completions);Assert.Equal(0,server.Confirmed);
        }finally{File.Delete(path);}
    }
    [Fact]
    public async Task CallerCancellationDoesNotCreateOrRetryAnUpload()
    {
        using var server=new Server();using var client=new HttpClient(server);using var progress=new TransferTracker().Start(TransferDirection.Upload,1);using var stop=new CancellationTokenSource();stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>ResumableAttachmentUpload.SendAsync(client,"https://upload.invalid",Request(1),["unused"],progress,stop.Token));
        Assert.Equal(0,server.Puts);Assert.Equal(0,server.Completions);Assert.Equal(0,server.StatusReads);
    }
}
