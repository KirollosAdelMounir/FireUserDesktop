using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UserIdentityDesktop.Core.Models;

namespace UserIdentityDesktop.Service.Media;

/// <summary>Native FFmpeg device capture. Arguments never pass through a shell.</summary>
public static class NativeCapture
{
    internal static MediaDevices ResolveScreenOutput(MediaDevices devices)
    {
        if (!OperatingSystem.IsWindows() || devices.ShareSource is not { Backend: ScreenCaptureBackend.Windows, Kind: ScreenShareKind.Screen } source) return devices;
        var output = LiveKitNative.fu_screen_output_index(source.X, source.Y, source.Width, source.Height);
        // Other adapters retain the selected GDI bounds; never capture a different monitor.
        return output < 0 ? devices : devices with { ShareSource = source with { Id = "dxgi:" + output } };
    }
    public static string ResolveExecutable(string executable)
    {
        var fileName = executable + (OperatingSystem.IsWindows() ? ".exe" : "");
        foreach (var directory in new[] { Path.Combine(AppContext.BaseDirectory, "media"), AppContext.BaseDirectory })
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate)) return candidate;
        }
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), fileName);
            if (File.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException("The camera/screen runtime is missing from this installation. Rebuild or repair the application to restore its automatically packaged media files.");
    }
    public static IReadOnlyList<string> Input(string kind, MediaDevices devices, string platform, string? display, bool wayland, int screenWidth = 0, int screenHeight = 0)
    {
        var device = kind == "microphone" ? devices.Microphone : kind == "camera" ? devices.Camera : devices.Screen;
        if (kind == "screen")
        {
            if (platform == "linux" && wayland) throw new InvalidOperationException("Wayland screen sharing requires the system ScreenCast portal; X11 capture is not a fallback.");
            var source = devices.ShareSource ?? throw new InvalidOperationException("Choose a screen or window before sharing.");
            if (source.Backend is ScreenCaptureBackend.Portal or ScreenCaptureBackend.MacOS) throw new InvalidOperationException("This screen source requires its native capture provider.");
            if (platform == "windows" && source.Backend != ScreenCaptureBackend.Windows || platform == "linux" && source.Backend != ScreenCaptureBackend.X11) throw new InvalidOperationException("The selected screen source belongs to another platform.");
            if (platform == "windows" && source.Kind == ScreenShareKind.Screen && source.Id.StartsWith("dxgi:", StringComparison.Ordinal))
            {
                if (!int.TryParse(source.Id.AsSpan(5), out var output) || output < 0) throw new InvalidOperationException("The selected desktop output is invalid.");
                if (source.Width <= 0 || source.Height <= 0) throw new InvalidOperationException("The selected screen has no valid dimensions.");
                var scale = Math.Min(1920d / source.Width, 1080d / source.Height);
                var width = Math.Max(2, (int)(source.Width * scale) / 2 * 2);
                var height = Math.Max(2, (int)(source.Height * scale) / 2 * 2);
                // Resize/convert on the capture GPU before crossing the process pipe.
                // A driver without this path is retried using the same GDI bounds.
                return new[] { "-f", "lavfi", "-i", $"ddagrab=output_idx={output}:framerate=60:draw_mouse=1:dup_frames=1,scale_d3d11=width={width}:height={height}:format=nv12,hwdownload,format=nv12" };
            }
            var selected = new List<string> { "-f", platform == "windows" ? "gdigrab" : "x11grab", "-framerate", "60" };
            if (source.Kind == ScreenShareKind.Window)
            {
                if (!ulong.TryParse(source.Id, out var window) || window == 0) throw new InvalidOperationException("The selected window is invalid.");
                if (platform == "windows") selected.AddRange(new[] { "-i", "hwnd=" + window });
                else { if (string.IsNullOrWhiteSpace(display)) throw new InvalidOperationException("No X11 display is available."); selected.AddRange(new[] { "-window_id", window.ToString(), "-i", display }); }
            }
            else
            {
                if (source.Width <= 0 || source.Height <= 0) throw new InvalidOperationException("The selected screen has no valid dimensions.");
                selected.AddRange(new[] { "-video_size", $"{source.Width}x{source.Height}" });
                if (platform == "windows") selected.AddRange(new[] { "-offset_x", source.X.ToString(), "-offset_y", source.Y.ToString(), "-i", "desktop" });
                else { if (string.IsNullOrWhiteSpace(display)) throw new InvalidOperationException("No X11 display is available."); selected.AddRange(new[] { "-i", $"{display}+{source.X},{source.Y}" }); }
            }
            return selected;
        }
        if (platform == "windows")
        {
            if (kind == "screen") return new[] { "-f", "gdigrab", "-framerate", "60", "-i", "desktop" };
            if (string.IsNullOrWhiteSpace(device)) throw new InvalidOperationException("Enter the native DirectShow device name in Media settings.");
            return new[] { "-f", "dshow", "-i", (kind == "microphone" ? "audio=" : "video=") + device };
        }
        if (platform == "macos")
        {
            if (kind == "screen" && string.IsNullOrWhiteSpace(device)) device = "Capture screen 0";
            if (string.IsNullOrWhiteSpace(device)) throw new InvalidOperationException("Enter the AVFoundation device index in Media settings. Screen capture uses a screen device index.");
            return kind == "microphone" ? new[] { "-f", "avfoundation", "-i", ":" + device }
                : new[] { "-f", "avfoundation", "-framerate", kind == "screen" ? "60" : "30", "-i", device + ":none" };
        }
        if (kind == "microphone") return new[] { "-f", "pulse", "-i", string.IsNullOrWhiteSpace(device) ? "default" : device };
        if (kind == "camera") return new[] { "-f", "v4l2", "-i", string.IsNullOrWhiteSpace(device) ? "/dev/video0" : device };
        if (string.IsNullOrWhiteSpace(device) && string.IsNullOrWhiteSpace(display)) throw new InvalidOperationException("No X11 display is available.");
        var arguments = new List<string> { "-f", "x11grab", "-framerate", "60" };
        if (screenWidth > 0 && screenHeight > 0) arguments.AddRange(new[] { "-video_size", $"{screenWidth}x{screenHeight}" });
        arguments.AddRange(new[] { "-i", string.IsNullOrWhiteSpace(device) ? display! : device });
        return arguments;
    }

    internal static Process Start(string executable, IEnumerable<string> arguments, bool input = false)
    {
        var info = new ProcessStartInfo(ResolveExecutable(executable)) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = !input, RedirectStandardInput = input, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        try
        {
            var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start the native media process.");
            // Drain diagnostics without retaining device data or unbounded output.
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            return process;
        }
        catch (Win32Exception) { throw new InvalidOperationException($"{executable} is unavailable. Install FFmpeg with FFplay and add it to PATH."); }
    }

    internal static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    internal static async Task ReadFrame(Stream stream, byte[] buffer, CancellationToken ct)
    {
        try { await stream.ReadExactlyAsync(buffer, ct); }
        catch (EndOfStreamException) { throw new IOException("Native capture stopped. Check the selected device and operating-system permissions."); }
    }
}
