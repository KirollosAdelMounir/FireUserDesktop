using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.Data;

namespace UserIdentityDesktop.Service.Media;

internal sealed class MacScreenCapture : IScreenFrameSource
{
    private IntPtr handle;
    public static IReadOnlyList<ScreenShareSource> Sources()
    {
        var json = Native.fu_screen_sources();
        if (json == IntPtr.Zero) throw new InvalidOperationException("Screen recording access is unavailable. Allow Fire User in macOS Privacy & Security → Screen Recording, then try sharing again.");
        try { return JsonSerializer.Deserialize<List<ScreenShareSource>>(Marshal.PtrToStringUTF8(json)!, ApiPayload.Json) ?? new(); }
        finally { Native.fu_screen_free(json); }
    }
    public MacScreenCapture(ScreenShareSource source)
    {
        if (source.Backend != ScreenCaptureBackend.MacOS || !ulong.TryParse(source.Id, out var id)) throw new InvalidOperationException("Invalid macOS screen source.");
        handle = Native.fu_screen_open(source.Kind == ScreenShareKind.Screen ? 0 : 1, id);
        if (handle == IntPtr.Zero) throw new IOException("The selected macOS screen or window could not start. Check Screen Recording permission and that the source is still open.");
    }
    public void Read(byte[] buffer, CancellationToken ct)
    {
        if (buffer.Length != PipeWireCapture.FrameBytes) throw new ArgumentException("Incorrect screen frame size.");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var read = Native.fu_screen_read(handle, buffer, buffer.Length);
            if (read > 0) return;
            if (read < 0) throw new IOException("The selected screen or window stopped sharing.");
        }
    }
    public void Stop() { if (handle != IntPtr.Zero) Native.fu_screen_stop(handle); }
    public void Dispose() { if (handle != IntPtr.Zero) { Native.fu_screen_close(handle); handle = IntPtr.Zero; } }
    private static class Native
    {
        private const string Library = "fireuser_screencapture";
        [DllImport(Library)] internal static extern IntPtr fu_screen_sources();
        [DllImport(Library)] internal static extern void fu_screen_free(IntPtr value);
        [DllImport(Library)] internal static extern IntPtr fu_screen_open(int kind, ulong id);
        [DllImport(Library)] internal static extern int fu_screen_read(IntPtr handle, [Out] byte[] buffer, int size);
        [DllImport(Library)] internal static extern void fu_screen_stop(IntPtr handle);
        [DllImport(Library)] internal static extern void fu_screen_close(IntPtr handle);
    }
}
