using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UserIdentityDesktop.Core;

public sealed record RemoteClipboardContent(string? Text, IReadOnlyList<string> Paths);
public interface IRemoteClipboard
{
    Task<RemoteClipboardContent> ReadAsync(CancellationToken ct);
    Task WriteAsync(RemoteClipboardContent content, CancellationToken ct);
    async Task<RemoteClipboardContent> ReadAfterCopyAsync(System.Func<Task> copy,CancellationToken ct)
    {
        await copy();await Task.Delay(100,ct);return await ReadAsync(ct);
    }
}
