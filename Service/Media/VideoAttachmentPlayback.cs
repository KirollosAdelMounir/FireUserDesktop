using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UserIdentityDesktop.Core.Models;

namespace UserIdentityDesktop.Service.Media;

/// <summary>Decode local authenticated video through packaged FFmpeg, never a browser or shell.</summary>
public static class VideoAttachmentPlayback
{
    public static async Task PlayAsync(string path, double start, Action<VideoPicture> frame, Action<double> position, CancellationToken token, Func<double>? volume = null)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        // Optional audio is decoded independently; a silent video remains playable.
        var audio = PlayAudio(path, start, lifetime.Token, volume);
        try { await DecodeAsync(path, start, frame, position, lifetime.Token); }
        finally { lifetime.Cancel(); await audio; }
    }
    private static async Task PlayAudio(string path, double start, CancellationToken token, Func<double>? volume)
    { try { await AudioNotePlayback.PlayAsync(path, start, _ => { }, token, volume: volume); } catch (Exception) { /* Video without an audio stream is valid. */ } }
    public static async Task DecodeAsync(string path, double start, Action<VideoPicture> frame, Action<double> position, CancellationToken token)
    {
        const int width = 960, height = 540, fps = 30;
        using var decoder = NativeCapture.Start("ffmpeg", new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-protocol_whitelist", "file,pipe", "-ss", start.ToString(CultureInfo.InvariantCulture), "-i", path, "-an", "-vf", "scale=960:540:force_original_aspect_ratio=decrease,pad=960:540:(ow-iw)/2:(oh-ih)/2,fps=30", "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1" });
        using var stop = token.Register(() => NativeCapture.Stop(decoder));
        var clock = Stopwatch.StartNew(); long count = 0;
        try
        {
            while (true)
            {
                var pixels = new byte[width * height * 4]; var offset = 0;
                while (offset < pixels.Length) { var read = await decoder.StandardOutput.BaseStream.ReadAsync(pixels.AsMemory(offset), token); if (read == 0) break; offset += read; }
                if (offset == 0) break;
                if (offset != pixels.Length) throw new IOException("Incomplete video frame.");
                var due = TimeSpan.FromSeconds(count / (double)fps) - clock.Elapsed;
                if (due > TimeSpan.Zero) await Task.Delay(due, token);
                token.ThrowIfCancellationRequested(); frame(new VideoPicture("attachment", width, height, pixels)); position(start + count++ / (double)fps);
            }
            await decoder.WaitForExitAsync(token);
            if (decoder.ExitCode != 0) throw new IOException("Unable to play this video attachment.");
        }
        finally { await AudioNotePlayback.StopProcessAsync(decoder); }
    }
}
