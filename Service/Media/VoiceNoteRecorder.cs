using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;

namespace UserIdentityDesktop.Service.Media;

/// <summary>Explicit microphone capture, encoded as AAC in an M4A container.</summary>
public sealed class VoiceNoteRecorder : IAsyncDisposable
{
    private readonly Action stopInput;
    private readonly FileStream file;
    private readonly Task writer;
    private readonly object stopGate = new();
    private readonly CancellationTokenSource discard = new();
    private Task<string>? stopTask;
    private Task? disposeTask;
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "voice-note-" + Guid.NewGuid().ToString("N") + ".m4a");
    internal string RawPath => Path + ".pcm";
    public VoiceNoteRecorder(string microphone) : this(new NativeAudio.Input(microphone)) { }
    private VoiceNoteRecorder(NativeAudio.Input input) : this(input.Frames.Reader, input.Dispose) { }
    internal VoiceNoteRecorder(ChannelReader<byte[]> frames, Action stopInput)
    {
        this.stopInput = stopInput;
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, BufferSize = 65536, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try { file = new FileStream(RawPath, options); }
        catch { stopInput(); throw; }
        writer = Task.Run(async () => { await foreach (var frame in frames.ReadAllAsync()) await file.WriteAsync(frame); });
    }
    public Task<string> StopAsync() { lock (stopGate) return stopTask ??= StopCoreAsync(); }
    private async Task<string> StopCoreAsync()
    {
        try
        {
            try { stopInput(); await writer; }
            finally { await file.DisposeAsync(); }
            discard.Token.ThrowIfCancellationRequested();
            if (new FileInfo(RawPath).Length == 0) throw new InvalidOperationException("No audio was recorded. Please try again.");
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (new FileStream(Path, options)) { }
            using var process = NativeCapture.Start("ffmpeg", new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-f", "s16le", "-ar", "48000", "-ac", "1", "-i", RawPath, "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", "-f", "ipod", Path });
            try
            {
                await process.WaitForExitAsync(discard.Token);
                if (process.ExitCode != 0 || new FileInfo(Path).Length == 0) throw new InvalidOperationException("Unable to prepare the voice note. Please try again.");
            }
            finally { await AudioNotePlayback.StopProcessAsync(process); }
            return Path;
        }
        catch { if (File.Exists(Path)) File.Delete(Path); throw; }
        finally { if (File.Exists(RawPath)) File.Delete(RawPath); }
    }
    public ValueTask DisposeAsync() { lock (stopGate) return new(disposeTask ??= DisposeCoreAsync()); }
    private async Task DisposeCoreAsync()
    {
        discard.Cancel();
        try { await StopAsync(); }
        catch (OperationCanceledException) { }
        finally
        {
            await AudioNotePlayback.StopAsync(Path);
            if (File.Exists(Path)) File.Delete(Path);
            if (File.Exists(RawPath)) File.Delete(RawPath);
            discard.Dispose();
        }
    }
}
