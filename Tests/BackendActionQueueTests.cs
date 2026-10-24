using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UserIdentityDesktop.Data;
using Xunit;
namespace UserIdentityDesktop.Tests;
public sealed class BackendActionQueueTests
{
    private static BackendActionQueue Offline(){var queue=new BackendActionQueue();queue.SetAccount("server|alice");queue.SetConnected(false);return queue;}
    [Fact]
    public async Task OfflineActionsWaitAndResumeInOrderWithoutBlockingTheCaller()
    {
        var queue=Offline();var sent=new List<int>();
        var tasks=Enumerable.Range(1,3).Select(i=>queue.RunAsync(_=>{sent.Add(i);return Task.FromResult(i);})).ToArray();
        Assert.Empty(sent);Assert.Equal(3,queue.PendingCount);Assert.All(tasks,task=>Assert.False(task.IsCompleted));
        queue.SetConnected(true);Assert.Equal(new[]{1,2,3},await Task.WhenAll(tasks));Assert.Equal(new[]{1,2,3},sent);Assert.Equal(0,queue.PendingCount);
    }
    [Fact]
    public async Task CallerCancellationAndSignOutNeverReplayUnderAnotherAccount()
    {
        var queue=Offline();using var stop=new CancellationTokenSource();var calls=0;
        var cancelled=queue.RunAsync(_=>Task.FromResult(++calls),stop.Token);stop.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>cancelled);
        var old=queue.RunAsync(_=>Task.FromResult(++calls));queue.SetAccount("server|bob");await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>old);
        queue.SetConnected(true);Assert.Equal(0,calls);Assert.Equal(0,queue.PendingCount);
        Assert.Equal(1,await queue.RunAsync(_=>Task.FromResult(++calls)));
        queue.SetAccount("");await Assert.ThrowsAsync<InvalidOperationException>(()=>queue.RunAsync(_=>Task.FromResult(++calls)));Assert.Equal(1,calls);
    }
    [Fact]
    public async Task SubmittedJsonIsFrozenAndCredentialsAreResolvedOnlyWhenItIsSent()
    {
        var queue=Offline();var token="expired";string? body=null,bearer=null;var queued=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);queue.Changed+=()=>{if(queue.PendingCount>0)queued.TrySetResult();};
        using var transport=new Handler(async request=>{body=await request.Content!.ReadAsStringAsync();bearer=request.Headers.Authorization?.Parameter;return new(HttpStatusCode.OK){Content=JsonContent.Create(new{ok=true})};});
        using var client=new HttpClient(new QueuedAuthenticatedHandler(queue,()=>Task.FromResult<string?>(token),transport)){Timeout=Timeout.InfiniteTimeSpan};
        var payload=new Dictionary<string,string>{{"name","Submitted"}};var sending=client.PostAsJsonAsync("https://fire.invalid/api/action",payload);await queued.Task;
        payload["name"]="Edited later";token="fresh";Assert.Null(body);queue.SetConnected(true);using var response=await sending;
        Assert.Equal("Submitted",JsonDocument.Parse(body!).RootElement.GetProperty("name").GetString());Assert.Equal("fresh",bearer);
    }
    [Fact]
    public async Task UncertainTransportFailureIsReportedOnceAndOnlyUnsentWorkResumes()
    {
        var queue=Offline();var calls=0;
        using var transport=new Handler(_=>{calls++;return calls==1?Task.FromException<HttpResponseMessage>(new HttpRequestException("connection lost")):Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));});
        using var client=new HttpClient(new QueuedAuthenticatedHandler(queue,()=>Task.FromResult<string?>("session"),transport)){Timeout=Timeout.InfiniteTimeSpan};
        var first=client.PostAsJsonAsync("https://fire.invalid/api/action",new{action="first"});queue.SetConnected(true);
        var error=await Assert.ThrowsAsync<HttpRequestException>(()=>first);Assert.Contains("could not be confirmed",error.Message);Assert.False(queue.IsConnected);Assert.Equal(1,calls);
        var next=client.PostAsJsonAsync("https://fire.invalid/api/action",new{action="next"});Assert.False(next.IsCompleted);queue.SetConnected(true);using var response=await next;
        Assert.Equal(2,calls);Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.Equal(0,queue.PendingCount);
    }
    [Fact]
    public async Task ClientCreatedForAnOldAccountCannotSendAfterCredentialsChange()
    {
        var queue=Offline();queue.SetConnected(true);var same=true;var calls=0;
        using var transport=new Handler(_=>{calls++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));});
        using var client=new HttpClient(new QueuedAuthenticatedHandler(queue,()=>{same=false;return Task.FromResult<string?>("new-session");},transport,()=>same));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>client.PostAsJsonAsync("https://fire.invalid/api/action",new{action="old account"}));Assert.Equal(0,calls);
    }
    [Fact]
    public async Task TransportCancellationIsReportedAsUncertainConnectionFailureWithoutReplay()
    {
        var queue=Offline();queue.SetConnected(true);var calls=0;
        using var transport=new Handler(_=>{calls++;return Task.FromException<HttpResponseMessage>(new TaskCanceledException("Operation canceled",new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.OperationAborted)));});
        using var client=new HttpClient(new QueuedAuthenticatedHandler(queue,()=>Task.FromResult<string?>("session"),transport)){Timeout=Timeout.InfiniteTimeSpan};
        var error=await Assert.ThrowsAsync<HttpRequestException>(()=>client.GetAsync("https://fire.invalid/api/Chat/ChatOf",TestContext.Current.CancellationToken));
        Assert.Contains("timed out or the connection was interrupted",error.Message);Assert.IsType<TaskCanceledException>(error.InnerException);Assert.False(queue.IsConnected);
        queue.SetConnected(true);Assert.Equal(1,calls);Assert.Equal(0,queue.PendingCount);
    }
    [Fact]
    public async Task CallerCancellationRemainsCancellationAndDoesNotMarkTheConnectionOffline()
    {
        var queue=Offline();queue.SetConnected(true);var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport=new TokenHandler(async token=>{started.SetResult();await Task.Delay(Timeout.Infinite,token);return new HttpResponseMessage(HttpStatusCode.OK);});
        using var client=new HttpClient(new QueuedAuthenticatedHandler(queue,()=>Task.FromResult<string?>("session"),transport)){Timeout=Timeout.InfiniteTimeSpan};
        using var stop=new CancellationTokenSource();var request=client.GetAsync("https://fire.invalid/api/Chat/ChatOf",stop.Token);await started.Task;stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>request);Assert.True(queue.IsConnected);Assert.Equal(0,queue.PendingCount);
    }
    [Theory][InlineData(true)][InlineData(false)]
    public async Task TransferDurationDoesNotTriggerOrdinaryRequestDeadlineOrOfflineState(bool transfer)
    {
        var queue=Offline();queue.SetConnected(true);
        using var transport=new TokenHandler(async token=>{await Task.Delay(150,token);return new HttpResponseMessage(HttpStatusCode.OK);});
        using var client=new HttpClient(new QueuedAuthenticatedHandler(queue,()=>Task.FromResult<string?>("session"),transport,requestTimeout:TimeSpan.FromMilliseconds(25))){Timeout=Timeout.InfiniteTimeSpan};
        using var request=new HttpRequestMessage(HttpMethod.Get,"https://fire.invalid/transfer");
        request.Options.Set(UserIdentityDesktop.Core.AttachmentTransferRequests.UnlimitedDuration,transfer);
        if(transfer){using var response=await client.SendAsync(request,TestContext.Current.CancellationToken);Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.True(queue.IsConnected);}
        else{await Assert.ThrowsAsync<HttpRequestException>(()=>client.SendAsync(request,TestContext.Current.CancellationToken));Assert.False(queue.IsConnected);}
    }
    private sealed class TokenHandler(Func<CancellationToken,Task<HttpResponseMessage>> handle):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>handle(ct);}
    private sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> handle):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>handle(request);}
}
