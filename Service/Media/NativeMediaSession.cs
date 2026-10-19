using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using UserIdentityDesktop.Core;
using UserIdentityDesktop.Core.Models;

namespace UserIdentityDesktop.Service.Media;

/// <summary>Official LiveKit C++ room transport with native device pipelines.</summary>
public sealed partial class NativeMediaSession : INativeMediaSession
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object playerLock = new();
    private IntPtr room;
    private CancellationTokenSource? lifetime;
    private readonly Dictionary<string, Capture> captures = new();
    private readonly Dictionary<string, Player> players = new();
    private string speakerDevice = "";
    // Root callbacks until fu_destroy has joined every C++ reader thread.
    private readonly LiveKitNative.VideoCallback videoCallback;
    private readonly LiveKitNative.AudioCallback audioCallback;
    private readonly LiveKitNative.EventCallback eventCallback;
    public bool Connected { get; private set; }
    public event Action<VideoPicture>? VideoReceived;
    public event Action<string>? ParticipantLeft;
    public event Action<string>? Error;
    public event Action<string>? CaptureStopped;

    public NativeMediaSession() { videoCallback = OnVideo; audioCallback = OnAudio; eventCallback = OnEvent; dataCallback = OnData; }
    public async Task ConnectAsync(MediaToken token, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (room != IntPtr.Zero) throw new InvalidOperationException("Leave the current call first.");
            if (!Uri.TryCreate(token.ServerUrl, UriKind.Absolute, out var url) || url.Scheme is not ("wss" or "ws")) throw new InvalidOperationException("The server returned an invalid media endpoint.");
            if (LiveKitNative.fu_abi_version() != 3) throw new InvalidOperationException("The native LiveKit bridge version does not match this client.");
            lifetime = new CancellationTokenSource();
            room = LiveKitNative.fu_create(videoCallback, audioCallback, eventCallback, dataCallback);
            if (room == IntPtr.Zero) throw new InvalidOperationException("Could not create the native LiveKit room.");
            LiveKitNative.Check(await Task.Run(() => LiveKitNative.fu_connect(room, token.ServerUrl, token.Token), ct), "connection");
            ct.ThrowIfCancellationRequested(); Connected = true;
            StartData(lifetime.Token); lock (remoteLock) SendHello();
        }
        catch (Exception ex)
        {
            if (room != IntPtr.Zero) { await Task.Run(() => LiveKitNative.fu_destroy(room)); room = IntPtr.Zero; }
            lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null;
            if (ex is DllNotFoundException or BadImageFormatException) throw new InvalidOperationException("The LiveKit C++ runtime is unavailable. Build and package the native bridge for this operating system and architecture.");
            throw;
        }
        finally { gate.Release(); }
    }
    private void OnVideo(IntPtr identity, IntPtr data, int width, int height, int length)
    {
        try
        {
            if (lifetime?.IsCancellationRequested != false || width <= 0 || height <= 0 || width > 4096 || height > 4096 || length != width * height * 4) return;
            var bytes = new byte[length]; Marshal.Copy(data, bytes, 0, length);
            VideoReceived?.Invoke(new VideoPicture(Marshal.PtrToStringUTF8(identity) ?? "Participant", width, height, bytes));
        }
        catch { /* Never unwind managed exceptions across the C ABI. */ }
    }
    private void OnEvent(int kind, IntPtr identity)
    {
        try
        {
            if (lifetime?.IsCancellationRequested != false) return;
            var id = Marshal.PtrToStringUTF8(identity) ?? "";
            RemotePeerEvent(kind, id);
            if (kind is 1 or 5) { ParticipantLeft?.Invoke(id); ClosePlayers(id); }
            else if (kind == 2)
            {
                Connected = false; lifetime?.Cancel();
                CaptureStopped?.Invoke("microphone"); CaptureStopped?.Invoke("camera"); CaptureStopped?.Invoke("screen");
                Error?.Invoke("Media disconnected. Leave and rejoin the call.");
            }
            else if (kind == 3) Error?.Invoke("A native media stream stopped. Leave and rejoin if it does not recover.");
        }
        catch { }
    }
    private void OnAudio(IntPtr identity, IntPtr data, int length, int rate, int channels)
    {
        try
        {
            if (lifetime?.IsCancellationRequested != false || length <= 0 || length > 192000 || rate is < 8000 or > 96000 || channels is < 1 or > 2) return;
            var key = Marshal.PtrToStringUTF8(identity) ?? "audio";
            var bytes = new byte[length]; Marshal.Copy(data, bytes, 0, length);
            lock (playerLock)
            {
                if (lifetime?.IsCancellationRequested != false) return;
                if (!players.TryGetValue(key, out var player))
                {
                    if (players.Count >= 32) return;
                    player = new Player(lifetime.Token, speakerDevice, rate, channels, message => Error?.Invoke(message)); players.Add(key, player);
                }
                player.Frames.Writer.TryWrite(bytes);
            }
        }
        catch { }
    }
    private void ClosePlayers(string identity)
    {
        lock (playerLock) foreach (var pair in players.Where(x => x.Key == identity || x.Key.StartsWith(identity + " · ", StringComparison.Ordinal))) pair.Value.Frames.Writer.TryComplete();
    }
    public Task SetMicrophoneAsync(bool enabled, MediaDevices devices, CancellationToken ct = default) => SetCapture("microphone", enabled, devices, ct);
    public Task SetCameraAsync(bool enabled, MediaDevices devices, CancellationToken ct = default) => SetCapture("camera", enabled, devices, ct);
    public Task SetScreenAsync(bool enabled, MediaDevices devices, CancellationToken ct = default) => SetCapture("screen", enabled, devices, ct);
    private async Task SetCapture(string kind, bool enabled, MediaDevices devices, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!Connected || lifetime == null) throw new InvalidOperationException("Join a call first.");
            var nativeKind = kind == "microphone" ? 0 : kind == "camera" ? 1 : 2;
            if (nativeKind == 2) SetRemoteSharing(false, devices);
            if (captures.Remove(kind, out var previous))
            {
                await previous.StopAsync(); previous.Dispose();
                LiveKitNative.Check(await Task.Run(() => LiveKitNative.fu_unpublish(room, nativeKind)), "unpublish");
            }
            if (!enabled) return;
            if (nativeKind == 0)
            {
                var micStop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                var input = new NativeAudio.Input(devices.Microphone);
                var mic = new Capture(input, micStop);
                try
                {
                    LiveKitNative.Check(await Task.Run(() => LiveKitNative.fu_publish(room, 0, 1, 1)), "publish microphone");
                    captures.Add(kind, mic);
                    mic.Work = Task.Run(async () =>
                    {
                        try
                        {
                            var frame = new byte[960]; var offset = 0;
                            await foreach (var bytes in input.Frames.Reader.ReadAllAsync(micStop.Token))
                            {
                                var consumed = 0;
                                while (consumed < bytes.Length)
                                {
                                    var count = Math.Min(frame.Length - offset, bytes.Length - consumed);
                                    Buffer.BlockCopy(bytes, consumed, frame, offset, count); offset += count; consumed += count;
                                    if (offset == frame.Length) { LiveKitNative.Check(LiveKitNative.fu_push(room, 0, frame, frame.Length), "microphone frame"); offset = 0; }
                                }
                            }
                        }
                        catch (OperationCanceledException) when (micStop.IsCancellationRequested) { }
                        catch { CaptureStopped?.Invoke(kind); Error?.Invoke("Microphone capture stopped. Check the selected device and permissions."); }
                    });
                }
                catch { await mic.StopAsync(); mic.Dispose(); throw; }
                return;
            }
            if (nativeKind == 1 && string.IsNullOrWhiteSpace(devices.Camera))
            {
                var available = await GetDevicesAsync(ct);
                devices = devices with { Camera = available.Cameras.FirstOrDefault()?.Id ?? "" };
                if (string.IsNullOrEmpty(devices.Camera)) throw new InvalidOperationException(available.Warning ?? "No camera is connected. Open Media settings and refresh devices.");
            }
            var platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
            var width = 0; var height = 0;
            if (kind == "screen") LiveKitNative.fu_desktop_bounds(out width, out height);
            args.AddRange(NativeCapture.Input(kind, devices, platform, Environment.GetEnvironmentVariable("DISPLAY"), !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")), width, height));
            var audio = nativeKind == 0;
            args.AddRange(audio ? new[] { "-vn", "-ac", "1", "-ar", "48000", "-f", "s16le", "pipe:1" }
                : new[] { "-an", "-vf", "scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2", "-r", kind == "screen" ? "60" : "30", "-pix_fmt", "rgba", "-f", "rawvideo", "pipe:1" });
            var process = NativeCapture.Start("ffmpeg", args);
            var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            var capture = new Capture(process, stop);
            try
            {
                var buffer = new byte[audio ? 960 : 1920 * 1080 * 4];
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token); startup.CancelAfter(TimeSpan.FromSeconds(10));
                await NativeCapture.ReadFrame(process.StandardOutput.BaseStream, buffer, startup.Token);
                LiveKitNative.Check(await Task.Run(() => LiveKitNative.fu_publish(room, nativeKind, 1920, 1080)), "publish");
                captures.Add(kind, capture);
                if (nativeKind == 2) SetRemoteSharing(true, devices);
                capture.Work = Task.Run(async () =>
                {
                    var lastPreview = 0L;
                    try
                    {
                        while (!stop.IsCancellationRequested)
                        {
                            LiveKitNative.Check(LiveKitNative.fu_push(room, nativeKind, buffer, buffer.Length), "frame submission");
                            // Keep preview work below the transport's 60 FPS screen ceiling.
                            if (!audio && Environment.TickCount64 - lastPreview >= 100)
                            {
                                lastPreview = Environment.TickCount64;
                                VideoReceived?.Invoke(new VideoPicture("You · " + kind, 1920, 1080, (byte[])buffer.Clone()));
                            }
                            await NativeCapture.ReadFrame(process.StandardOutput.BaseStream, buffer, stop.Token);
                        }
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                    catch (Exception)
                    {
                        if (!stop.IsCancellationRequested) { CaptureStopped?.Invoke(kind); Error?.Invoke($"{kind} capture stopped. Check the device and permissions before enabling it again."); }
                    }
                    finally { NativeCapture.Stop(process); if (nativeKind == 2) SetRemoteSharing(false, devices); }
                });
            }
            catch { await capture.StopAsync(); capture.Dispose(); throw; }
        }
        finally { gate.Release(); }
    }
    public async Task DisconnectAsync()
    {
        await gate.WaitAsync();
        try
        {
            Connected = false; lifetime?.Cancel();
            lock (remoteLock) { RevokeLocal(); control.Clear(); canHost = false; RemoteControlChanged?.Invoke(control.Status); }
            foreach (var capture in captures.Values) { await capture.StopAsync(); capture.Dispose(); }
            captures.Clear();
            await Task.WhenAll(remoteReader, remoteWriter);
            if (room != IntPtr.Zero) { await Task.Run(() => LiveKitNative.fu_destroy(room)); room = IntPtr.Zero; }
            Player[] snapshot; lock (playerLock) { snapshot = players.Values.ToArray(); players.Clear(); }
            await Task.WhenAll(snapshot.Select(x => x.Work));
            lifetime?.Dispose(); lifetime = null;
        }
        finally { gate.Release(); }
    }
    public async ValueTask DisposeAsync() => await DisconnectAsync();
    private sealed class Capture : IDisposable
    {
        private readonly Action stopAction;
        private readonly IDisposable resource;
        private readonly CancellationTokenSource stop;
        public Capture(Process process, CancellationTokenSource stop) { resource = process; this.stop = stop; stopAction = () => NativeCapture.Stop(process); }
        public Capture(NativeAudio.Input input, CancellationTokenSource stop) { resource = input; this.stop = stop; stopAction = input.Dispose; }
        public Task Work { get; set; } = Task.CompletedTask;
        public async Task StopAsync() { stop.Cancel(); stopAction(); await Work; }
        public void Dispose() { resource.Dispose(); stop.Dispose(); }
    }
    private sealed class Player
    {
        public Channel<byte[]> Frames { get; } = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(10) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        public Task Work { get; }
        public Player(CancellationToken ct, string device, int rate, int channels, Action<string> error)
        {
            Work = Task.Run(async () =>
            {
                try
                {
                    using var player = new NativeAudio.Output(device, rate, channels);
                    await foreach (var bytes in Frames.Reader.ReadAllAsync(ct)) player.Write(bytes);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (Exception) { if (!ct.IsCancellationRequested) error("Native audio playback stopped. Check the selected speaker and audio output permissions."); }
            });
        }
    }
}
