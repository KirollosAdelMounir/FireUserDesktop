using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.Service.Media;
using Xunit;

namespace UserIdentityDesktop.Tests;

public sealed class FfmpegVideoCaptureTests
{
    private static readonly MediaDevices Monitor = new("", "", "", new("dxgi:1", "Selected monitor", ScreenShareKind.Screen, ScreenCaptureBackend.Windows, -1920, 0, 1920, 1080));
    private sealed class FakeProcess(Stream frames) : IVideoCaptureProcess
    {
        public Stream Frames => frames;
        internal bool Stopped, Disposed;
        public void Stop() => Stopped = true;
        public void Dispose() { Stop(); Disposed = true; frames.Dispose(); }
    }
    private static FakeProcess Frame(byte value) => new(new MemoryStream(Enumerable.Repeat(value, 1920 * 1080 * 3 / 2).ToArray()));
    private sealed class SyntheticProcess(System.Diagnostics.Process process) : IVideoCaptureProcess
    {
        public Stream Frames => process.StandardOutput.BaseStream;
        public void Stop() => NativeCapture.Stop(process);
        public void Dispose() { Stop(); process.Dispose(); }
    }

    [Fact]
    public async Task StartupFailure_RetriesSameMonitorWithGdi()
    {
        var failed = new FakeProcess(Stream.Null); var fallback = Frame(7);
        var attempts = new List<IReadOnlyList<string>>();
        using var capture = new FfmpegVideoCapture("screen", Monitor, "windows", args => { attempts.Add(args); return attempts.Count == 1 ? failed : fallback; });
        await capture.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(capture.UsedFallback); Assert.True(failed.Disposed); Assert.False(fallback.Disposed);
        Assert.Contains(attempts[0], value => value.StartsWith("ddagrab=output_idx=1:"));
        Assert.Contains("gdigrab", attempts[1]); Assert.Contains("-1920", attempts[1]); Assert.Contains("1920x1080", attempts[1]); Assert.Contains("desktop", attempts[1]);
        Assert.DoesNotContain(attempts[1], value => value.StartsWith("ddagrab="));
        capture.Dispose(); Assert.True(fallback.Stopped); Assert.True(fallback.Disposed);
    }
    [Fact]
    public async Task MidShareFailure_RecoversAndKeepsSubmittingToSameSender()
    {
        var preferred = Frame(1); var fallback = Frame(2); var starts = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var capture = new FfmpegVideoCapture("screen", Monitor, "windows", _ => ++starts == 1 ? preferred : fallback);
        await capture.StartAsync(stop.Token);
        var received = new List<byte>();
        try { await capture.RunAsync(frame => { received.Add(frame[0]); if (received.Count == 2) stop.Cancel(); }, stop.Token); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        Assert.Equal(new byte[] { 1, 2 }, received); Assert.Equal(2, starts);
        Assert.True(capture.UsedFallback); Assert.True(preferred.Disposed);
    }
    [Fact]
    public async Task BothProvidersFail_StopsAndDisposesBothAttempts()
    {
        var processes = new List<FakeProcess>();
        using var capture = new FfmpegVideoCapture("screen", Monitor, "windows", _ => { var process = new FakeProcess(Stream.Null); processes.Add(process); return process; });
        var failure = await Assert.ThrowsAsync<IOException>(() => capture.StartAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, processes.Count); Assert.All(processes, process => { Assert.True(process.Stopped); Assert.True(process.Disposed); });
        Assert.Contains("Reselect", failure.Message); Assert.DoesNotContain("Native capture stopped", failure.Message);
    }
    [Fact]
    public async Task CancelledStartup_DoesNotRetryOrLeakProcess()
    {
        using var stop = new CancellationTokenSource(); var starts = 0;
        var process = Frame(3);
        using var capture = new FfmpegVideoCapture("screen", Monitor, "windows", _ => { starts++; stop.Cancel(); return process; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture.StartAsync(stop.Token));
        Assert.Equal(1, starts); Assert.False(capture.UsedFallback); Assert.True(process.Disposed);
    }
    [Fact]
    public async Task CancelledFallback_DisposesSecondProcessWithoutAnotherRetry()
    {
        using var stop = new CancellationTokenSource(); var starts = 0;
        var first = new FakeProcess(Stream.Null); var second = Frame(6);
        using var capture = new FfmpegVideoCapture("screen", Monitor, "windows", _ =>
        {
            if (++starts == 1) return first;
            stop.Cancel(); return second;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture.StartAsync(stop.Token));
        Assert.Equal(2, starts); Assert.True(first.Disposed); Assert.True(second.Disposed);
    }
    [Fact]
    public async Task WorkingProvider_DoesNotStartAnotherCapture()
    {
        var starts = 0; using var capture = new FfmpegVideoCapture("screen", Monitor, "windows", _ => { starts++; return Frame(4); });
        await capture.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, starts); Assert.False(capture.UsedFallback);
    }
    [Fact]
    public async Task WindowFailure_DoesNotFallBackToEntireDesktop()
    {
        var window = Monitor with { ShareSource = Monitor.ShareSource! with { Kind = ScreenShareKind.Window, Id = "1234" } };
        var starts = 0; using var capture = new FfmpegVideoCapture("screen", window, "windows", _ => { starts++; return new FakeProcess(Stream.Null); });
        await Assert.ThrowsAsync<IOException>(() => capture.StartAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, starts); Assert.False(capture.UsedFallback);
    }
    [Fact]
    public void ScreenPipe_UsesPlanarFramesWithImmediateOutput()
    {
        using var screen = new FfmpegVideoCapture("screen", Monitor, "windows");
        using var camera = new FfmpegVideoCapture("camera", Monitor with { Camera = "camera" }, "windows");
        Assert.Equal(3_110_400, screen.FrameBytes); Assert.Equal(8_294_400, camera.FrameBytes);
        var args = FfmpegVideoCapture.Arguments("screen", Monitor, "windows");
        Assert.Contains("yuv420p", args); Assert.Contains("passthrough", args); Assert.Contains("-flush_packets", args);
        Assert.Contains("nobuffer", args); Assert.Equal("1", args[args.ToList().IndexOf("-thread_queue_size") + 1]);
        Assert.DoesNotContain("rgba", args);
    }
    [Theory]
    [InlineData(3840,2160,1920,1080)]
    [InlineData(1080,1920,606,1080)]
    public void DesktopGpuResize_PreservesWholeMonitorAspect(int width, int height, int outputWidth, int outputHeight)
    {
        var devices = Monitor with { ShareSource = Monitor.ShareSource! with { Width = width, Height = height } };
        var args = FfmpegVideoCapture.Arguments("screen", devices, "windows");
        Assert.Contains(args, arg => arg.Contains($"scale_d3d11=width={outputWidth}:height={outputHeight}:format=nv12,hwdownload,format=nv12"));
        Assert.Contains(args, arg => arg.Contains("pad=1920:1080:(ow-iw)/2:(oh-ih)/2"));
    }
    [Fact]
    public async Task PackagedFfmpeg_ProducesCorrectPlanarPixelsFromSyntheticSource()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var capture = new FfmpegVideoCapture("screen", Monitor, "windows", args =>
        {
            var synthetic = args.ToList();
            // Replace the complete capture filter with generated color, so this
            // exercises the packaged process/pipe without opening a real device.
            synthetic[synthetic.IndexOf("-i") + 1] = "color=c=red:size=320x180:rate=60";
            synthetic.InsertRange(synthetic.Count - 1, new[] { "-frames:v", "3" });
            return new SyntheticProcess(NativeCapture.Start("ffmpeg", synthetic));
        });
        await capture.StartAsync(stop.Token);
        var received = false;
        try
        {
            await capture.RunAsync(frame =>
            {
                Assert.InRange(frame[0], (byte)80, (byte)82);
                Assert.InRange(frame[1920 * 1080], (byte)89, (byte)91);
                Assert.InRange(frame[1920 * 1080 * 5 / 4], (byte)239, (byte)241);
                received = true; stop.Cancel();
            }, stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        Assert.True(received); Assert.False(capture.UsedFallback);
    }
}
