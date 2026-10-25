using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace UserIdentityDesktop.Service.Media;

/// <summary>Continuously drains raw capture while a paced sender consumes only the newest frame.</summary>
internal static class LatestVideoFrames
{
    internal static async Task RunAsync(Stream input, int frameBytes, Action<byte[]> submit, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true
        }, dropped => ArrayPool<byte>.Shared.Return(dropped));
        var reader = Task.Run(async () =>
        {
            Exception? failure = null;
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var buffer = ArrayPool<byte>.Shared.Rent(frameBytes);
                    try { await input.ReadExactlyAsync(buffer.AsMemory(0, frameBytes), stop.Token).ConfigureAwait(false); }
                    catch { ArrayPool<byte>.Shared.Return(buffer); throw; }
                    if (!frames.Writer.TryWrite(buffer)) ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception ex) { failure = ex; }
            finally { frames.Writer.TryComplete(failure); }
        }, CancellationToken.None);
        try
        {
            await foreach (var frame in frames.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try { submit(frame); }
                finally { ArrayPool<byte>.Shared.Return(frame); }
            }
        }
        finally
        {
            stop.Cancel(); await reader.ConfigureAwait(false);
            while (frames.Reader.TryRead(out var pending)) ArrayPool<byte>.Shared.Return(pending);
        }
    }
}
