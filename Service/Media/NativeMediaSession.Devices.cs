using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UserIdentityDesktop.Core.Models;

namespace UserIdentityDesktop.Service.Media;

public sealed partial class NativeMediaSession
{
    public async Task<MediaDeviceCatalog> GetDevicesAsync(CancellationToken ct = default)
    {
        var microphones = await Task.Run(() => NativeAudio.Devices(true), ct);
        var speakers = await Task.Run(() => NativeAudio.Devices(false), ct);
        var cameras = new List<MediaDeviceOption>(); string? warning = null;
        try
        {
            if (OperatingSystem.IsLinux())
            {
                foreach (var path in Directory.EnumerateFiles("/dev", "video*").Order())
                {
                    var label = Path.Combine("/sys/class/video4linux", Path.GetFileName(path), "name");
                    cameras.Add(new(path, File.Exists(label) ? File.ReadAllText(label).Trim() + " (" + path + ")" : path));
                }
            }
            else
            {
                var info = new ProcessStartInfo(NativeCapture.ResolveExecutable("ffmpeg")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (var arg in OperatingSystem.IsWindows() ? new[] { "-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy" } : new[] { "-hide_banner", "-f", "avfoundation", "-list_devices", "true", "-i", "" }) info.ArgumentList.Add(arg);
                using var process = Process.Start(info)!;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var close = timeout.Token.Register(() => NativeCapture.Stop(process));
                var diagnostics = await process.StandardError.ReadToEndAsync(timeout.Token);
                await process.StandardOutput.ReadToEndAsync(timeout.Token); await process.WaitForExitAsync(timeout.Token);
                cameras.AddRange(ParseCameras(diagnostics, OperatingSystem.IsWindows()));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { warning = ex is OperationCanceledException ? "Camera discovery timed out. Check the camera permissions." : ex.Message; }
        static MediaDeviceOption[] Audio(string[] names) => new[] { new MediaDeviceOption("", "System default") }.Concat(names.Select(x => new MediaDeviceOption(x, x))).ToArray();
        return new(Audio(microphones), Audio(speakers), cameras.ToArray(), warning);
    }
    internal static IEnumerable<MediaDeviceOption> ParseCameras(string diagnostics, bool windows)
    {
        var videoSection = false;
        foreach (var line in diagnostics.Split('\n'))
        {
            if (windows)
            {
                var match = Regex.Match(line, "\"([^\"]+)\" \\(video\\)");
                if (match.Success) yield return new(match.Groups[1].Value, match.Groups[1].Value);
            }
            else
            {
                if (line.Contains("AVFoundation video devices:")) { videoSection = true; continue; }
                if (line.Contains("AVFoundation audio devices:")) videoSection = false;
                var match = Regex.Match(line, @"\[(\d+)\] (.+)");
                if (videoSection && match.Success && !match.Groups[2].Value.Contains("Capture screen", StringComparison.OrdinalIgnoreCase)) yield return new(match.Groups[1].Value, match.Groups[2].Value.Trim());
            }
        }
    }
    public async Task<double> CheckMicrophoneAsync(string device, CancellationToken ct = default)
    {
        using var input = await Task.Run(() => new NativeAudio.Input(device), ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var peak = 0d; var received = 0;
        while (received < 48000 * 2)
        {
            var bytes = await input.Frames.Reader.ReadAsync(timeout.Token); received += bytes.Length;
            for (var i = 0; i + 1 < bytes.Length; i += 2) peak = Math.Max(peak, Math.Abs((int)System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i, 2))) / 32768d);
        }
        return peak * 100;
    }
    public async Task<VideoPicture> CheckCameraAsync(string device, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(device)) throw new InvalidOperationException("Select a camera first.");
        var platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        args.AddRange(NativeCapture.Input("camera", new("", device, ""), platform, null, false));
        args.AddRange(new[] { "-an", "-vf", "scale=640:360:force_original_aspect_ratio=decrease,pad=640:360:(ow-iw)/2:(oh-ih)/2", "-frames:v", "1", "-pix_fmt", "rgba", "-f", "rawvideo", "pipe:1" });
        using var process = NativeCapture.Start("ffmpeg", args);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var close = timeout.Token.Register(() => NativeCapture.Stop(process));
        try { var bytes = new byte[640 * 360 * 4]; await NativeCapture.ReadFrame(process.StandardOutput.BaseStream, bytes, timeout.Token); return new("Camera check", 640, 360, bytes); }
        finally { NativeCapture.Stop(process); }
    }
    public async Task CheckSpeakerAsync(string device, CancellationToken ct = default)
    {
        using var output = await Task.Run(() => new NativeAudio.Output(device, 48000, 1), ct);
        var samples = new byte[960];
        for (var part = 0; part < 30; part++)
        {
            ct.ThrowIfCancellationRequested();
            for (var i = 0; i < 480; i++) System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan(i * 2, 2), (short)(Math.Sin((part * 480 + i) * 2 * Math.PI * 440 / 48000) * 2500));
            output.Write(samples); await Task.Delay(10, ct);
        }
        await Task.Delay(100, ct);
    }
    public async Task SetSpeakerAsync(string device)
    {
        Player[] previous;
        lock (playerLock) { speakerDevice = device; previous = players.Values.ToArray(); players.Clear(); foreach (var player in previous) player.Frames.Writer.TryComplete(); }
        await Task.WhenAll(previous.Select(x => x.Work));
    }
}
