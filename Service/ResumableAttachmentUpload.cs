using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using UserIdentityDesktop.Core;
using UserIdentityDesktop.Data;
namespace UserIdentityDesktop.Service;

internal sealed record AttachmentUploadFile(string Name, string ContentType, long Size);
internal sealed record AttachmentUploadRequest(Guid Sender, Guid Receiver, string? ConversationId, string? Text,
    string? ReplyToMessageId, List<AttachmentUploadFile> Files, List<UserIdentityDesktop.Core.Models.EncryptedNotificationPreview>? NotificationPreviews=null);
internal sealed record AttachmentUploadStatus(string UploadId, List<long> Offsets);

/// <summary>Only chunk writes are recovered. Creation and final message publication are never replayed.</summary>
internal static class ResumableAttachmentUpload
{
    internal const int ChunkSize = 1024 * 1024;
    public static async Task SendAsync(HttpClient client, string server, AttachmentUploadRequest request,
        IReadOnlyList<string> paths, ITransferOperation progress, CancellationToken ct = default)
    {
        if (paths.Count != request.Files.Count || paths.Count == 0) throw new InvalidDataException("Invalid attachment batch.");
        long total = 0;
        foreach (var file in request.Files) total = checked(total + file.Size);
        progress.SetTotal(total);
        progress.SetPhase("Waiting for connection…");
        var root = server.TrimEnd('/') + "/api/Chat/Uploads";
        using var created = await Send(client, HttpMethod.Post, root, JsonContent.Create(request, options: ApiPayload.Json), ct);
        created.EnsureSuccessStatusCode();
        var state = await ApiPayload.ReadAsync<AttachmentUploadStatus>(created, ct);
        if (!Guid.TryParseExact(state.UploadId, "N", out _)) throw new InvalidDataException("Invalid upload session.");
        Validate(state, state.UploadId, request.Files);
        if (state.Offsets.Exists(x => x != 0)) throw new InvalidDataException("A new upload must start empty.");
        var url = root + "/" + state.UploadId;
        var buffer = new byte[ChunkSize];
        for (var index = 0; index < paths.Count; index++)
        {
            await using var stream = File.OpenRead(paths[index]);
            if (stream.Length != request.Files[index].Size) throw new IOException("The prepared attachment changed.");
            long offset = 0;
            var failures = 0;
            while (offset < stream.Length)
            {
                ct.ThrowIfCancellationRequested();
                stream.Position = offset;
                var count = (int)Math.Min(buffer.Length, stream.Length - offset);
                await stream.ReadExactlyAsync(buffer.AsMemory(0, count), ct);
                progress.SetPhase("Uploading…");
                AttachmentUploadStatus next;
                var recovered = false;
                try
                {
                    using var content = new ByteArrayContent(buffer, 0, count);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    using var response = await Send(client, HttpMethod.Put, $"{url}/Files/{index}?offset={offset}", content, ct);
                    response.EnsureSuccessStatusCode();
                    next = await ApiPayload.ReadAsync<AttachmentUploadStatus>(response, ct);
                }
                catch (Exception error) when (!ct.IsCancellationRequested && Recoverable(error) && failures++ < 3)
                {
                    // The response may have been lost after commit. Ask the server before sending any bytes again.
                    progress.SetPhase("Recovering upload…");
                    recovered = true;
                    using var status = await Send(client, HttpMethod.Get, url, null, ct);
                    status.EnsureSuccessStatusCode();
                    next = await ApiPayload.ReadAsync<AttachmentUploadStatus>(status, ct);
                }
                Validate(next, state.UploadId, request.Files);
                for (var other = 0; other < next.Offsets.Count; other++)
                    if (other != index && next.Offsets[other] != state.Offsets[other]) throw new InvalidDataException("Unexpected attachment offset.");
                var confirmed = next.Offsets[index];
                if (confirmed < offset || confirmed > offset + count) throw new InvalidDataException("Unexpected upload offset.");
                if (!recovered && confirmed != offset + count) throw new InvalidDataException("The server did not confirm this chunk.");
                progress.Report(confirmed - offset);
                if (confirmed > offset) failures = 0;
                offset = confirmed;
                state = next;
            }
            progress.FileCompleted();
        }
        progress.SetPhase("Waiting for server confirmation…");
        using var complete = await Send(client, HttpMethod.Post, url + "/Complete", null, ct);
        complete.EnsureSuccessStatusCode();
        if (!await ApiPayload.ReadAsync<bool>(complete, ct)) throw new IOException("The server did not confirm the attachment message.");
    }
    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string url, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Options.Set(AttachmentTransferRequests.UnlimitedDuration, true);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }
    private static bool Recoverable(Exception error) => error is OperationCanceledException ||
        error is HttpRequestException http && (http.StatusCode == null || http.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)http.StatusCode >= 500);
    private static void Validate(AttachmentUploadStatus status, string id, List<AttachmentUploadFile> files)
    {
        if (status.UploadId != id || status.Offsets == null || status.Offsets.Count != files.Count) throw new InvalidDataException("Invalid upload status.");
        for (var i = 0; i < files.Count; i++)
            if (status.Offsets[i] < 0 || status.Offsets[i] > files[i].Size) throw new InvalidDataException("Invalid upload offset.");
    }
}
