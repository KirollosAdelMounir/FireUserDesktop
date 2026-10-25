using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UserIdentityDesktop.Core.Models;

namespace UserIdentityDesktop.Service.Media;

internal interface IVideoCaptureProcess : IDisposable
{
    Stream Frames { get; }
    void Stop();
}

/// <summary>Owns capture startup and one selected-monitor GDI recovery, before or after publication.</summary>
internal sealed class FfmpegVideoCapture : IDisposable
{
    internal const int Width = 1920, Height = 1080;
    internal int FrameBytes => Width * Height * (kind == "screen" ? 3 : 8) / 2;
    private readonly string kind, platform;
    private MediaDevices devices;
    private readonly Func<IReadOnlyList<string>, IVideoCaptureProcess> start;
    private readonly object processLock = new();
    private IVideoCaptureProcess? process;
    private bool stopped;
    internal bool UsedFallback { get; private set; }
    private byte[] firstFrame = Array.Empty<byte>();

    private sealed class CaptureProcess(Process process) : IVideoCaptureProcess
    {
        public Stream Frames => process.StandardOutput.BaseStream;
        public void Stop() => NativeCapture.Stop(process);
        public void Dispose() { Stop(); process.Dispose(); }
    }

    internal FfmpegVideoCapture(string kind, MediaDevices devices, string platform,
        Func<IReadOnlyList<string>, IVideoCaptureProcess>? start = null)
    {
        this.kind = kind; this.devices = devices; this.platform = platform;
        this.start = start ?? (args => new CaptureProcess(NativeCapture.Start("ffmpeg", args)));
    }

    internal static IReadOnlyList<string> Arguments(string kind, MediaDevices devices, string platform)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-filter_threads", "2" };
        if (kind == "screen") args.AddRange(new[] { "-thread_queue_size", "1", "-probesize", "32", "-analyzeduration", "0", "-fflags", "nobuffer" });
        args.AddRange(NativeCapture.Input(kind, devices, platform, Environment.GetEnvironmentVariable("DISPLAY"), ScreenSources.IsWayland));
        args.AddRange(new[] { "-an", "-vf", "scale=1920:1080:force_original_aspect_ratio=decrease:flags=fast_bilinear,pad=1920:1080:(ow-iw)/2:(oh-ih)/2",
            kind == "screen" ? "-fps_mode" : "-r", kind == "screen" ? "passthrough" : "30", "-pix_fmt", kind == "screen" ? "yuv420p" : "rgba", "-f", "rawvideo", "-flush_packets", "1", "pipe:1" });
        return args;
    }

    private bool UseGdiFallback()
    {
        if (kind != "screen" || platform != "windows" || UsedFallback || devices.ShareSource is not { Backend: ScreenCaptureBackend.Windows, Kind: ScreenShareKind.Screen } source || !source.Id.StartsWith("dxgi:", StringComparison.Ordinal)) return false;
        UsedFallback = true;
        devices = devices with { ShareSource = source with { Id = "gdi-selected-monitor" } };
        Console.Error.WriteLine("Screen capture: Desktop Duplication unavailable; retrying selected monitor bounds using GDI.");
        return true;
    }

    internal async Task StartAsync(CancellationToken ct)
    {
        try { await StartAttemptAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            CloseProcess();
            if (!UseGdiFallback()) throw CaptureFailure(ex);
            try { await StartAttemptAsync(ct).ConfigureAwait(false); }
            catch (Exception fallback) when (fallback is IOException or OperationCanceledException && !ct.IsCancellationRequested) { CloseProcess(); throw CaptureFailure(fallback); }
            catch { CloseProcess(); throw; }
        }
        catch { CloseProcess(); throw; }
    }

    private async Task StartAttemptAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (processLock)
        {
            if (stopped) throw new OperationCanceledException(ct);
            process = start(Arguments(kind, devices, platform));
        }
        firstFrame = new byte[FrameBytes];
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
        startup.CancelAfter(TimeSpan.FromSeconds(8));
        await NativeCapture.ReadFrame(process.Frames, firstFrame, startup.Token).ConfigureAwait(false);
    }

    internal async Task RunAsync(Action<byte[]> submit, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            submit(firstFrame);
            // The first frame is no longer needed; retain only the pooled latest slot.
            firstFrame = Array.Empty<byte>();
            try { await LatestVideoFrames.RunAsync(process!.Frames, FrameBytes, submit, ct).ConfigureAwait(false); return; }
            catch (IOException ex) when (!ct.IsCancellationRequested)
            {
                CloseProcess();
                if (!UseGdiFallback()) throw CaptureFailure(ex);
                await StartAsync(ct).ConfigureAwait(false);
            }
        }
    }

    private IOException CaptureFailure(Exception cause) => new(platform == "windows" && kind == "screen"
        ? "Windows could not capture the selected screen or window. Reselect it; closed or minimized windows and secure or protected desktops cannot be shared."
        : "The selected camera or screen stopped producing frames. Check that it is still available and its operating-system capture permission is enabled.", cause);

    internal void Stop()
    {
        lock (processLock) { stopped = true; process?.Stop(); }
    }
    private void CloseProcess() { lock (processLock) { process?.Dispose(); process = null; } }
    public void Dispose() { Stop(); CloseProcess(); }
}
