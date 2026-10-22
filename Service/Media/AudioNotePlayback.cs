using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace UserIdentityDesktop.Service.Media;

/// <summary>Decode locally through the packaged runtime; output stays inside the native application.</summary>
public static class AudioNotePlayback
{
    private static readonly object gate = new();
    private static readonly Dictionary<string, List<(CancellationTokenSource Stop, Task Task)>> sessions = new();
    private static Task<T> Track<T>(string path, Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        lock (gate)
        {
            var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var task = Task.Run(async () =>
            {
                try { return await action(stop.Token); }
                catch (Exception) when (stop.IsCancellationRequested) { throw new OperationCanceledException(stop.Token); }
            }, stop.Token);
            if (!sessions.TryGetValue(path, out var active)) sessions[path] = active = new();
            active.Add((stop, task));
            _ = task.ContinueWith(_ =>
            {
                lock (gate)
                {
                    if (sessions.TryGetValue(path, out var remaining))
                    {
                        remaining.RemoveAll(value => value.Stop == stop);
                        if (remaining.Count == 0) sessions.Remove(path);
                    }
                    stop.Dispose();
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }
    public static Task PlayAsync(string path, double start, Action<double> position, CancellationToken ct, double speed = 1) => Track(path, async token =>
    {
        using var output = new NativeAudio.Output("", 48000, 1);
        await DecodeAsync(path, start, output.Write, position, token, speed); return true;
    }, ct);
    public static async Task StopAsync(string path)
    {
        Task task;
        lock (gate)
        {
            if (!sessions.TryGetValue(path, out var active)) return;
            var snapshot = active.ToArray();
            task = Task.WhenAll(snapshot.Select(value => value.Task));
            foreach (var value in snapshot) value.Stop.Cancel();
        }
        try { await task; } catch { /* Playback reports errors to its owning UI. */ }
    }
    internal static async Task StopProcessAsync(Process process)
    {
        NativeCapture.Stop(process);
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException) { }
    }
    internal static async Task DecodeAsync(string path, double start, Action<byte[]> output, Action<double> position, CancellationToken ct, double speed = 1)
    {
        if (speed is not (.5 or 1 or 1.5 or 2)) throw new ArgumentOutOfRangeException(nameof(speed));
        using var decoder = NativeCapture.Start("ffmpeg", new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-protocol_whitelist", "file,pipe", "-ss", start.ToString(CultureInfo.InvariantCulture), "-i", path, "-vn", "-af", "atempo=" + speed.ToString(CultureInfo.InvariantCulture), "-f", "s16le", "-ar", "48000", "-ac", "1", "pipe:1" });
        using var stop = ct.Register(() => NativeCapture.Stop(decoder));
        var clock = Stopwatch.StartNew(); long bytes = 0; double reported = -1;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var frame = new byte[1920]; var count = 0;
                while (count < frame.Length)
                {
                    var read = await decoder.StandardOutput.BaseStream.ReadAsync(frame.AsMemory(count), ct);
                    if (read == 0) break;
                    count += read;
                }
                if (count == 0) break;
                var due = TimeSpan.FromSeconds(bytes / 96000d) - clock.Elapsed;
                if (due > TimeSpan.Zero) await Task.Delay(due, ct);
                if (count % 2 != 0) throw new IOException("Invalid decoded audio frame.");
                output(count == frame.Length ? frame : frame[..count]); bytes += count;
                if (bytes / 96000d - reported >= .2) { reported = bytes / 96000d; position(start + reported * speed); }
            }
            await decoder.WaitForExitAsync(ct);
            if (decoder.ExitCode != 0) throw new IOException("Unable to play this audio attachment.");
            var remaining = TimeSpan.FromSeconds(bytes / 96000d) - clock.Elapsed;
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ct);
            position(start + bytes / 96000d * speed);
        }
        finally { await StopProcessAsync(decoder); }
    }
    public static Task<double[]> WaveformAsync(string path, double duration, CancellationToken ct) => Track(path, async token =>
    {
        var peaks = new double[48];
        if (duration <= 0 || new FileInfo(path).Length > 32 * 1024 * 1024) return peaks;
        using var decoder = NativeCapture.Start("ffmpeg", new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-protocol_whitelist", "file,pipe", "-i", path, "-vn", "-f", "s16le", "-ar", "8000", "-ac", "1", "pipe:1" });
        using var stop = token.Register(() => NativeCapture.Stop(decoder));
        var buffer = new byte[8192]; long sample = 0; int carry = -1;
        try
        {
            while (true)
            {
                var count = await decoder.StandardOutput.BaseStream.ReadAsync(buffer, token);
                if (count == 0) break;
                for (var i = 0; i < count; i++)
                {
                    if (carry < 0) { carry = buffer[i]; continue; }
                    var value = (short)(carry | buffer[i] << 8); carry = -1;
                    var index = Math.Min(47, (int)(sample++ * 48 / (duration * 8000)));
                    peaks[index] = Math.Max(peaks[index], Math.Abs((double)value) / 32768);
                }
            }
            await decoder.WaitForExitAsync(token);
            if (decoder.ExitCode != 0) throw new IOException("Unable to read the audio waveform.");
            return peaks.Select(value => Math.Round(Math.Clamp(value, 0, 1), 3)).ToArray();
        }
        finally { await StopProcessAsync(decoder); }
    }, ct);
    public static Task<double> DurationAsync(string path, CancellationToken ct) => Track(path, token => ReadDurationAsync(path, token), ct);
    private static async Task<double> ReadDurationAsync(string path, CancellationToken ct)
    {
        var info = new ProcessStartInfo(NativeCapture.ResolveExecutable("ffmpeg")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "-nostdin", "-hide_banner", "-protocol_whitelist", "file,pipe", "-i", path }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Unable to read audio information.");
        using var stop = ct.Register(() => NativeCapture.Stop(process));
        try
        {
            double duration = 0;
            while (await process.StandardError.ReadLineAsync(ct) is { } line)
            {
                var index = line.IndexOf("Duration: ", StringComparison.Ordinal);
                if (index >= 0 && TimeSpan.TryParse(line[(index + 10)..].Split(',')[0].Trim(), CultureInfo.InvariantCulture, out var value)) duration = value.TotalSeconds;
            }
            await process.WaitForExitAsync(ct); return duration;
        }
        finally { await StopProcessAsync(process); }
    }
}
