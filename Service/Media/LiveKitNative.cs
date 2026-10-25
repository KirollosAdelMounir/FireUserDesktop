using System;
using System.Runtime.InteropServices;

namespace UserIdentityDesktop.Service.Media;

internal static class LiveKitNative
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void PcmCallback(IntPtr data, int length);
    [DllImport("fireuser_livekit", CallingConvention = CallingConvention.Cdecl)] internal static extern int fu_audio_device_name(int capture, int index, byte[] name, int capacity);
    [DllImport("fireuser_livekit", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr fu_audio_open([MarshalAs(UnmanagedType.LPUTF8Str)] string name, int capture, int rate, int channels, PcmCallback? callback);
    [DllImport("fireuser_livekit", CallingConvention = CallingConvention.Cdecl)] internal static extern int fu_audio_write(IntPtr handle, byte[] data, int length);
    [DllImport("fireuser_livekit", CallingConvention = CallingConvention.Cdecl)] internal static extern void fu_audio_close(IntPtr handle);
    [DllImport("fireuser_livekit", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr fu_tone_open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int loop);
    [DllImport("fireuser_livekit", CallingConvention = CallingConvention.Cdecl)] internal static extern void fu_tone_close(IntPtr handle);
    private const string Library = "fireuser_livekit";
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void VideoCallback(IntPtr identity, IntPtr data, int width, int height, int length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void AudioCallback(IntPtr identity, IntPtr data, int length, int rate, int channels);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void EventCallback(int kind, IntPtr identity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void DataCallback(IntPtr sender, IntPtr data, int length);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int fu_abi_version();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr fu_create(VideoCallback video, AudioCallback audio, EventCallback events, DataCallback data);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int fu_connect(IntPtr session, [MarshalAs(UnmanagedType.LPUTF8Str)] string url, [MarshalAs(UnmanagedType.LPUTF8Str)] string token);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int fu_publish(IntPtr session, int kind, int width, int height);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int fu_push(IntPtr session, int kind, byte[] data, int length);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int fu_unpublish(IntPtr session, int kind);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void fu_destroy(IntPtr session);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int fu_send_data(IntPtr session, [MarshalAs(UnmanagedType.LPUTF8Str)] string destination, byte[] data, int length);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int fu_send_input(IntPtr session, [MarshalAs(UnmanagedType.LPUTF8Str)] string destination, byte[] data, int length);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int fu_send_transfer(IntPtr session, [MarshalAs(UnmanagedType.LPUTF8Str)] string destination, byte[] data, int length);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int fu_interactive_screen(IntPtr session,int enabled);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int fu_desktop_bounds(out int width, out int height);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int fu_screen_output_index(int x, int y, int width, int height);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int fu_input_supported();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int fu_apply_input(int kind, double x, double y, int code, int down);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void fu_release_input();
    internal static void Check(int status, string operation)
    {
        if (status != 0) throw new InvalidOperationException("LiveKit C++ " + operation + " failed.");
    }
}
