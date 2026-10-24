using System;
using System.Threading;
using System.Threading.Tasks;
using UserIdentityDesktop.Core.Models;

namespace UserIdentityDesktop.Service.Media;

public sealed partial class NativeMediaSession
{
    private async Task StartNativeScreenAsync(ScreenShareSource selected, MediaDevices devices, CancellationToken ct)
    {
        IScreenFrameSource? source = null;
        ScreenCastPortal? inputPortal=null;
        try
        {
            if (selected.Backend == ScreenCaptureBackend.Portal)
            {
                if (!OperatingSystem.IsLinux() || !ScreenSources.IsWayland) throw new InvalidOperationException("This portal source does not belong to the active desktop.");
                var portal = await ScreenCastPortal.OpenAsync(selected.Kind, ct, remoteControl:devices.AllowRemoteControl);
                inputPortal=portal;
                try { source = await Task.Run(() => new PipeWireCapture(portal), ct); }
                catch { await portal.DisposeAsync(); throw; }
            }
            else
            {
                if (!OperatingSystem.IsMacOS()) throw new InvalidOperationException("This source requires macOS ScreenCaptureKit.");
                source = await Task.Run(() => new MacScreenCapture(selected), ct);
            }
            ct.ThrowIfCancellationRequested();
        }
        catch { if (source != null) await Task.Run(source.Dispose); throw; }
        var input = source;
        var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime!.Token);
        var capture = new Capture(input, stop);
        try
        {
            var buffer = new byte[PipeWireCapture.FrameBytes];
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token); startup.CancelAfter(TimeSpan.FromSeconds(15));
            await Task.Run(() => input.Read(buffer, startup.Token));
            LiveKitNative.Check(await Task.Run(() => LiveKitNative.fu_publish(room, 2, 1920, 1080)), "publish screen");
            captures.Add("screen", capture);
            SetRemoteSharing(true, devices,inputPortal);
            // Portal input stays scoped to its approved monitor; native providers retain their desktop-bound checks.
            var capturedRoom = room;
            capture.Work = Task.Run(() =>
            {
                var lastPreview = 0L;
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        LiveKitNative.Check(LiveKitNative.fu_push(capturedRoom, 2, buffer, buffer.Length), "screen frame");
                        if (Environment.TickCount64 - lastPreview >= 100)
                        {
                            lastPreview = Environment.TickCount64;
                            VideoReceived?.Invoke(new VideoPicture("You · screen", 1920, 1080, (byte[])buffer.Clone()));
                        }
                        input.Read(buffer, stop.Token);
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch { if (!stop.IsCancellationRequested) { CaptureStopped?.Invoke("screen"); Error?.Invoke("The selected screen or window stopped sharing. Choose a source to share again."); } }
                finally { input.Stop(); SetRemoteSharing(false, devices); LiveKitNative.fu_unpublish(capturedRoom, 2); }
            });
        }
        catch { await capture.StopAsync(); capture.Dispose(); throw; }
    }
}
