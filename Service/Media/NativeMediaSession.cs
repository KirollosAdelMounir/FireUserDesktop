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
    public event Action<MediaParticipant>? ParticipantJoined;
    public event Action<MediaParticipantUpdate>? ParticipantUpdated;
    public event Action<string>? Error;
    public event Action<string>? CaptureStopped;

    public event Action<MediaConnectionState>? ConnectionChanged;

    public NativeMediaSession(IRemoteClipboard? clipboard = null,ITransferTracker? transfers = null) { clipboardStore = clipboard;transferTracker=transfers; videoCallback = OnVideo; audioCallback = OnAudio; eventCallback = OnEvent; dataCallback = OnData; }
    public async Task ConnectAsync(MediaToken token, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (room != IntPtr.Zero) throw new InvalidOperationException("Leave the current call first.");
            if (!Uri.TryCreate(token.ServerUrl, UriKind.Absolute, out var url) || url.Scheme is not ("wss" or "ws")) throw new InvalidOperationException("The server returned an invalid media endpoint.");
            if (await Task.Run(LiveKitNative.fu_abi_version) != 6) throw new InvalidOperationException("The native LiveKit bridge version does not match this client.");
            lifetime = new CancellationTokenSource();
            room = await Task.Run(() => LiveKitNative.fu_create(videoCallback, audioCallback, eventCallback, dataCallback));
            if (room == IntPtr.Zero) throw new InvalidOperationException("Could not create the native LiveKit room.");
            LiveKitNative.Check(await Task.Run(() => LiveKitNative.fu_connect(room, token.ServerUrl, token.Token), ct), "connection");
            ct.ThrowIfCancellationRequested(); Connected = true;
            StartData(lifetime.Token); lock (remoteLock) SendHello();
        }
        catch (Exception ex)
        {
            if (room != IntPtr.Zero) { await Task.Run(() => LiveKitNative.fu_destroy(room)); room = IntPtr.Zero; }
            lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null;
            if (ex is DllNotFoundException or BadImageFormatException)
            {
                // Loader diagnostics contain library names/ABI versions, not the
                // meeting credential. Keep them available to terminal support.
                Console.Error.WriteLine($"Native LiveKit loader failure: {ex}");
                throw new InvalidOperationException("The LiveKit C++ runtime is unavailable. Build and package the native bridge for this operating system and architecture. See terminal output for the missing library or incompatible version.", ex);
            }
            throw;
        }
        finally { gate.Release(); }
    }
    private void OnVideo(IntPtr identity, IntPtr data, int width, int height, int length)
    {
        try
        {
            if (lifetime?.IsCancellationRequested != false || width <= 0 || height <= 0 || width > 4096 || height > 4096 || length != width * height * 4) return;
            // The native callback completely fills this buffer before it is published.
            var bytes = GC.AllocateUninitializedArray<byte>(length); Marshal.Copy(data, bytes, 0, length);
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
                // Keep the SDK reason, never credentials, for diagnosis and retry policy.
                var reason = int.TryParse(id, out var value) ? value : 0;
                var retryable = reason is 0 or 3 or 6 or 7 or 8 or 9 or 14 or 15;
                Console.Error.WriteLine($"LiveKit disconnected: reason={reason}; retryable={retryable}");
                ConnectionChanged?.Invoke(new("disconnected", reason.ToString(), retryable));
            }
            else if (kind == 8)
            {
                lock (remoteLock) { var sharing = control.LocalScope != null; RevokeLocal(); control.Clear(); control.Sharing(sharing); RemoteControlChanged?.Invoke(CurrentControlStatus); }
                ConnectionChanged?.Invoke(new("reconnecting"));
            }
            else if (kind == 9)
            {
                lock (remoteLock) SendHello();
                ConnectionChanged?.Invoke(new("connected"));
            }
            else if (kind == 7) { var parts = id.Split('\n', 3); if (parts.Length == 3) ParticipantUpdated?.Invoke(new MediaParticipantUpdate(parts[0], parts[1], parts[2])); }
            else if (kind == 6) { var parts = id.Split('\n', 2); ParticipantJoined?.Invoke(new MediaParticipant(parts[0], parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : parts[0])); }
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
                var input = await Task.Run(() => new NativeAudio.Input(devices.Microphone), ct);
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
            if (nativeKind == 2)
            {
                var selected = devices.ShareSource ?? throw new InvalidOperationException("Choose a screen or window before sharing.");
                if (OperatingSystem.IsLinux() && ScreenSources.IsWayland && selected.Backend != ScreenCaptureBackend.Portal) throw new InvalidOperationException("Wayland requires the system sharing portal.");
                if (selected.Backend is ScreenCaptureBackend.Portal or ScreenCaptureBackend.MacOS) { await StartNativeScreenAsync(selected, devices, ct); return; }
                devices = await Task.Run(() => NativeCapture.ResolveScreenOutput(devices), ct);
            }
            var platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
            var source = new FfmpegVideoCapture(kind, devices, platform);
            var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            var capture = new Capture(source, stop);
            try
            {
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
                await source.StartAsync(startup.Token);
                LiveKitNative.Check(await Task.Run(() => LiveKitNative.fu_publish(room, nativeKind, FfmpegVideoCapture.Width, FfmpegVideoCapture.Height)), "publish");
                captures.Add(kind, capture);
                if (nativeKind == 2) SetRemoteSharing(true, devices);
                capture.Work = Task.Run(async () =>
                {
                    var lastPreview = 0L;
                    void Submit(byte[] pixels)
                    {
                        LiveKitNative.Check(LiveKitNative.fu_push(room, nativeKind, pixels, source.FrameBytes), "frame submission");
                        // Screen preview is generated by the native I420 pipeline at 10 FPS.
                        if (nativeKind == 2 || Environment.TickCount64 - lastPreview < 100) return;
                        lastPreview = Environment.TickCount64;
                        var preview = new byte[source.FrameBytes]; Buffer.BlockCopy(pixels, 0, preview, 0, preview.Length);
                        VideoReceived?.Invoke(new VideoPicture("You · " + kind, FfmpegVideoCapture.Width, FfmpegVideoCapture.Height, preview));
                    }
                    try { await source.RunAsync(Submit, stop.Token); }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                    catch (Exception ex)
                    {
                        if (!stop.IsCancellationRequested)
                        {
                            CaptureStopped?.Invoke(kind);
                            Error?.Invoke(ex is System.IO.IOException ? ex.Message : $"{kind} capture stopped. Check the device and permissions before enabling it again.");
                        }
                    }
                    finally { source.Stop(); if (nativeKind == 2) SetRemoteSharing(false, devices); }
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
            lock (remoteLock) { RevokeLocal(); control.Clear(); canHost = false; RemoteControlChanged?.Invoke(CurrentControlStatus); }
            foreach (var capture in captures.Values) { await capture.StopAsync(); capture.Dispose(); }
            captures.Clear();
            await Task.WhenAll(remoteReader, remoteWriter,moveWriter,clipboardCleanup);
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
        public Capture(FfmpegVideoCapture source, CancellationTokenSource stop) { resource = source; this.stop = stop; stopAction = source.Stop; }
        public Capture(NativeAudio.Input input, CancellationTokenSource stop) { resource = input; this.stop = stop; stopAction = input.Dispose; }
        public Capture(IScreenFrameSource source, CancellationTokenSource stop) { resource = source; this.stop = stop; stopAction = source.Stop; }
        public Task Work { get; set; } = Task.CompletedTask;
        public async Task StopAsync() { stop.Cancel(); await Task.Run(stopAction); await Work; }
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
