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
        if (kind == "screen" && platform == "linux" && wayland)
            throw new NotSupportedException("Screen sharing on Wayland requires a portal capture provider. Use an X11 session for this capture provider.");
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
