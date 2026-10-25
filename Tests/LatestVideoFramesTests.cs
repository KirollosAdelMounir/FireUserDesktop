using UserIdentityDesktop.Service.Media;
using Xunit;

namespace UserIdentityDesktop.Tests;

public sealed class LatestVideoFramesTests
{
    private sealed class BurstStream : MemoryStream
    {
        internal readonly TaskCompletionSource Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal BurstStream() : base(Enumerable.Range(1, 100).SelectMany(x => Enumerable.Repeat((byte)x, 4)).ToArray()) { }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (Position >= 4) await Continue.Task.WaitAsync(ct);
            // The next read starts only after the preceding frame entered the slot.
            if (Position == Length) Drained.TrySetResult();
            var count = await base.ReadAsync(buffer, ct);
            return count;
        }
    }
    [Fact]
    public async Task SlowSender_DrainsCaptureAndReceivesNewestCompleteFrame()
    {
        using var stream = new BurstStream(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = new List<byte>();
        // Capture/sending runs on a worker in the application. Keep the simulated
        // synchronous sender out of xUnit's shared synchronization context too.
        await Assert.ThrowsAsync<EndOfStreamException>(() => Task.Run(() => LatestVideoFrames.RunAsync(stream, 4, frame =>
        {
            received.Add(frame[0]); Assert.All(frame.Take(4), value => Assert.Equal(frame[0], value));
            if (received.Count != 1) return;
            stream.Continue.TrySetResult();
            Assert.True(stream.Drained.Task.Wait(TimeSpan.FromSeconds(5)), "Capture must drain while the sender is blocked.");
        }, timeout.Token), timeout.Token));
        Assert.Equal(new byte[] { 1, 100 }, received);
    }
    [Fact]
    public async Task StoppingSender_CancelsAndJoinsCaptureReader()
    {
        using var stream = new BurstStream(); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LatestVideoFrames.RunAsync(stream, 4, _ => stop.Cancel(), stop.Token));
        Assert.False(stream.Drained.Task.IsCompleted);
    }
}
