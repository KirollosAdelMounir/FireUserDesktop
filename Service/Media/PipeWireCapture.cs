using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace UserIdentityDesktop.Service.Media;

internal interface IScreenFrameSource : IDisposable
{
    void Read(byte[] buffer, CancellationToken ct);
    void Stop();
}

/// <summary>In-process native GStreamer consumes only the portal-issued PipeWire remote.</summary>
internal sealed class PipeWireCapture : IScreenFrameSource
{
    private readonly ScreenCastPortal portal;
    private IntPtr pipeline, sink, bus;
    private int stopped;
    internal const int FrameBytes = 1920 * 1080 * 4;
    public static void CheckRuntime()
    {
        try
        {
            Gst.gst_init(IntPtr.Zero, IntPtr.Zero);
            foreach (var name in new[] { "pipewiresrc", "videoconvert", "videoscale", "appsink" })
            {
                var factory = Gst.gst_element_factory_find(name);
                if (factory == IntPtr.Zero) throw new InvalidOperationException("Screen sharing needs the system GStreamer PipeWire and base/good plugins. Install your distribution's PipeWire GStreamer package (gstreamer1-plugin-pipewire on Fedora, gstreamer1.0-pipewire on Debian/Ubuntu, or gst-plugin-pipewire on Arch).");
                Gst.gst_object_unref(factory);
            }
        }
        catch (DllNotFoundException) { throw new InvalidOperationException("Screen sharing needs the system GStreamer 1.0 core, app, base/good and PipeWire plugins."); }
    }
    public PipeWireCapture(ScreenCastPortal portal)
    {
        this.portal = portal;
        try
        {
            CheckRuntime();
            var target = portal.Serial is {} serial ? "target-object=" + serial : "path=" + portal.Node;
            pipeline = Gst.gst_parse_launch($"pipewiresrc fd={portal.Remote!.DangerousGetHandle().ToInt32()} {target} do-timestamp=true ! videoconvert ! videoscale add-borders=true ! video/x-raw,format=RGBA,width=1920,height=1080,pixel-aspect-ratio=1/1 ! appsink name=frames sync=false max-buffers=1 drop=true", out var error);
            if (error != IntPtr.Zero) { Gst.g_error_free(error); throw new IOException("Could not create the native PipeWire screen capture pipeline. Check the installed GStreamer plugins."); }
            if (pipeline == IntPtr.Zero) throw new IOException("Could not create the PipeWire capture pipeline.");
            sink = Gst.gst_bin_get_by_name(pipeline, "frames"); bus = Gst.gst_element_get_bus(pipeline);
            if (Gst.gst_element_set_state(pipeline, 4) == 0) throw new IOException("The approved PipeWire screen stream could not start.");
        }
        catch { Dispose(); throw; }
    }
    public void Read(byte[] buffer, CancellationToken ct)
    {
        if (buffer.Length != FrameBytes) throw new ArgumentException("Incorrect screen frame size.");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (portal.Closed || Volatile.Read(ref stopped) != 0) throw new IOException("Screen sharing ended in the desktop portal.");
            var sample = Gst.gst_app_sink_try_pull_sample(sink, 200_000_000);
            if (sample != IntPtr.Zero)
            {
                CopySample(sample, buffer); return;
            }
            var error = Gst.gst_bus_pop_filtered(bus, 2); // GST_MESSAGE_ERROR
            if (error != IntPtr.Zero) { Gst.gst_mini_object_unref(error); throw new IOException("PipeWire capture stopped. Check the compositor portal and PipeWire services."); }
            if (Gst.gst_app_sink_is_eos(sink) != 0) throw new IOException("The shared screen or window is no longer available.");
        }
    }
    internal static void CopySample(IntPtr sample, byte[] buffer)
    {
        try
        {
            if (buffer.Length != FrameBytes) throw new ArgumentException("Incorrect screen frame size.");
            var frame = Gst.gst_sample_get_buffer(sample);
            if (frame == IntPtr.Zero || Gst.gst_buffer_get_size(frame) != (nuint)FrameBytes || Gst.gst_buffer_extract(frame, 0, buffer, (nuint)buffer.Length) != (nuint)buffer.Length) throw new IOException("The compositor returned an invalid screen frame.");
        }
        finally { Gst.gst_mini_object_unref(sample); }
    }
    public void Stop()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0) return;
        if (pipeline != IntPtr.Zero) Gst.gst_element_set_state(pipeline, 1);
        portal.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
    public void Dispose()
    {
        Stop();
        if (sink != IntPtr.Zero) { Gst.gst_object_unref(sink); sink = IntPtr.Zero; }
        if (bus != IntPtr.Zero) { Gst.gst_object_unref(bus); bus = IntPtr.Zero; }
        if (pipeline != IntPtr.Zero) { Gst.gst_object_unref(pipeline); pipeline = IntPtr.Zero; }
    }
    internal static class Gst
    {
        private const string Core = "libgstreamer-1.0.so.0", App = "libgstapp-1.0.so.0";
        [DllImport(Core)] internal static extern void gst_init(IntPtr argc, IntPtr argv);
        [DllImport(Core)] internal static extern IntPtr gst_element_factory_find([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(Core)] internal static extern IntPtr gst_parse_launch([MarshalAs(UnmanagedType.LPUTF8Str)] string text, out IntPtr error);
        [DllImport(Core)] internal static extern IntPtr gst_bin_get_by_name(IntPtr bin, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(Core)] internal static extern IntPtr gst_element_get_bus(IntPtr element);
        [DllImport(Core)] internal static extern int gst_element_set_state(IntPtr element, int state);
        [DllImport(Core)] internal static extern IntPtr gst_bus_pop_filtered(IntPtr bus, uint types);
        [DllImport(Core)] internal static extern void gst_object_unref(IntPtr obj);
        [DllImport(Core)] internal static extern void gst_mini_object_unref(IntPtr obj);
        [DllImport(Core)] internal static extern IntPtr gst_sample_get_buffer(IntPtr sample);
        [DllImport(Core)] internal static extern nuint gst_buffer_get_size(IntPtr buffer);
        [DllImport(Core)] internal static extern nuint gst_buffer_extract(IntPtr buffer, nuint offset, [Out] byte[] destination, nuint size);
        [DllImport(App)] internal static extern IntPtr gst_app_sink_try_pull_sample(IntPtr sink, ulong timeout);
        [DllImport(App)] internal static extern int gst_app_sink_is_eos(IntPtr sink);
        [DllImport("libglib-2.0.so.0")] internal static extern void g_error_free(IntPtr error);
    }
}
