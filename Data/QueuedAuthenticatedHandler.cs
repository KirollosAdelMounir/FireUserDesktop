using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using UserIdentityDesktop.Core;
namespace UserIdentityDesktop.Data;
internal sealed class QueuedAuthenticatedHandler(IBackendActionQueue queue,Func<Task<string?>> credential,HttpMessageHandler transport,Func<bool>? sameAccount=null,TimeSpan? requestTimeout=null) : HttpMessageHandler
{
    private readonly HttpMessageInvoker sender=new(transport,disposeHandler:false);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        if(sameAccount?.Invoke()==false)throw new OperationCanceledException("This action belongs to a previous account.");
        // JsonContent is lazy: freeze the submitted DTO now, not after the user edits another form.
        if(request.Content?.Headers.ContentType?.MediaType=="application/json"){
            var original=request.Content;var bytes=await original.ReadAsByteArrayAsync(ct);var frozen=new ByteArrayContent(bytes);
            foreach(var header in original.Headers)frozen.Headers.TryAddWithoutValidation(header.Key,header.Value);
            request.Content=frozen;original.Dispose();
        }
        return await queue.RunAsync(async token=>{
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);
        if(!request.Options.TryGetValue(AttachmentTransferRequests.UnlimitedDuration,out var transfer)||!transfer)
            timeout.CancelAfter(requestTimeout??TimeSpan.FromSeconds(100));
        if(sameAccount?.Invoke()==false)throw new OperationCanceledException("This action belongs to a previous account.");
        var bearer=await credential();timeout.Token.ThrowIfCancellationRequested();
        if(sameAccount?.Invoke()==false)throw new OperationCanceledException("This action belongs to a previous account.");
        if(string.IsNullOrEmpty(bearer))throw new InvalidOperationException("Your session expired. Sign in again before submitting this action.");
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",bearer);
        try{return await sender.SendAsync(request,timeout.Token);}
        catch(OperationCanceledException error) when(!token.IsCancellationRequested)
        {
            // A transport deadline/abort is a failed, uncertain request; it is not a user cancellation.
            queue.SetConnected(false);
            throw new HttpRequestException("The request timed out or the connection was interrupted. Its result could not be confirmed; check before retrying.", error);
        }
        catch(HttpRequestException error){queue.SetConnected(false);throw new HttpRequestException("The connection was lost. This request could not be confirmed; check its result before retrying.", error);}
    },ct);
    }
    protected override void Dispose(bool disposing){if(disposing)sender.Dispose();base.Dispose(disposing);}
}
