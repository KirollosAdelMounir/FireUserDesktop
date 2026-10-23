using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
namespace UserIdentityDesktop.Service.Media;
/// <summary>Bounded native playback of the same audio files used by Angular.</summary>
public sealed class CallSounds : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private long version;
    private IntPtr handle;
    public void Play(string? asset, bool loop = true)
    {
        if (AppContext.TryGetSwitch("FireUser.DisableAudio", out var disabled) && disabled) return;
        var requested = Interlocked.Increment(ref version);
        _ = Task.Run(async () =>
        {
            await gate.WaitAsync();
            try
            {
                if (requested != Volatile.Read(ref version)) return;
                if (handle != IntPtr.Zero) { LiveKitNative.fu_tone_close(handle); handle = IntPtr.Zero; }
                if (asset != null) handle = LiveKitNative.fu_tone_open(Path.Combine(AppContext.BaseDirectory, "sounds", asset + ".mp3"), loop ? 1 : 0);
            }
            catch (Exception) { /* Sound availability must not prevent answering a call. */ }
            finally { gate.Release(); }
            if (!loop && asset != null)
            {
                await Task.Delay(TimeSpan.FromSeconds(8));
                if (requested == Volatile.Read(ref version)) Play(null);
            }
        });
    }
    public void Dispose() => Play(null);
}
