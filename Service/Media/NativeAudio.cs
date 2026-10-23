using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;

namespace UserIdentityDesktop.Service.Media;

internal static class NativeAudio
{
    internal static string[] Devices(bool capture)
    {
        var names = new List<string>(); var buffer = new byte[4096];
        for (var i = 0; i < 128; i++)
        {
            var length = LiveKitNative.fu_audio_device_name(capture ? 1 : 0, i, buffer, buffer.Length);
            if (length < 0) throw new InvalidOperationException("The operating system could not list audio devices. Check your audio service and permissions.");
            if (length == 0) break;
            names.Add(Encoding.UTF8.GetString(buffer, 0, length));
        }
        return names.ToArray();
    }
    internal sealed class Input : IDisposable
    {
        private IntPtr handle;
        private readonly LiveKitNative.PcmCallback callback;
        public Channel<byte[]> Frames { get; } = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(32) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
        public Input(string name)
        {
            callback = (data, length) =>
            {
                try
                {
                    if (length is <= 0 or > 192000) return;
                    var bytes = new byte[length]; Marshal.Copy(data, bytes, 0, length); Frames.Writer.TryWrite(bytes);
                }
                catch { }
            };
            handle = LiveKitNative.fu_audio_open(name, 1, 48000, 1, callback);
            if (handle == IntPtr.Zero) throw new InvalidOperationException("Could not open the selected microphone. Refresh devices and check microphone permissions.");
        }
        public void Dispose()
        {
            if (handle != IntPtr.Zero) { LiveKitNative.fu_audio_close(handle); handle = IntPtr.Zero; }
            Frames.Writer.TryComplete(); GC.KeepAlive(callback);
        }
    }
    internal sealed class Output : IDisposable
    {
        private IntPtr handle;
        public Output(string name, int rate, int channels)
        {
            handle = LiveKitNative.fu_audio_open(name, 0, rate, channels, null);
            if (handle == IntPtr.Zero) throw new InvalidOperationException("Could not open the selected speaker. Refresh devices and check the audio output.");
        }
        public void Write(byte[] bytes) => LiveKitNative.Check(LiveKitNative.fu_audio_write(handle, bytes, bytes.Length), "speaker output");
        public void Dispose() { if (handle != IntPtr.Zero) { LiveKitNative.fu_audio_close(handle); handle = IntPtr.Zero; } }
    }
}
