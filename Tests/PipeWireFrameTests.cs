using System.Runtime.InteropServices;
using UserIdentityDesktop.Service.Media;
using Xunit;

namespace UserIdentityDesktop.Tests;

public sealed class PipeWireFrameTests
{
    [Theory]
    [InlineData(1920, 1080)] [InlineData(320, 180)]
    public void NativeGStreamerRgbaFrames_CopyCorrectPixels_AndRejectWrongDimensions(int width, int height)
    {
        if (!OperatingSystem.IsLinux() || !NativeLibrary.TryLoad("libgstreamer-1.0.so.0", out var library)) { Assert.Skip("Native synthetic frame test requires Linux GStreamer core/app and videotestsrc."); return; }
        NativeLibrary.Free(library);
        PipeWireCapture.Gst.gst_init(IntPtr.Zero, IntPtr.Zero);
        var pipeline = PipeWireCapture.Gst.gst_parse_launch($"videotestsrc num-buffers=1 pattern=red ! video/x-raw,format=RGBA,width={width},height={height} ! appsink name=frames sync=false max-buffers=1 drop=true", out var error);
        Assert.Equal(IntPtr.Zero, error); Assert.NotEqual(IntPtr.Zero, pipeline);
        var sink = PipeWireCapture.Gst.gst_bin_get_by_name(pipeline, "frames");
        try
        {
            Assert.NotEqual(0, PipeWireCapture.Gst.gst_element_set_state(pipeline, 4));
            var sample = PipeWireCapture.Gst.gst_app_sink_try_pull_sample(sink, 5_000_000_000); Assert.NotEqual(IntPtr.Zero, sample);
            var bytes = new byte[PipeWireCapture.FrameBytes];
            if (width != 1920) Assert.Throws<IOException>(() => PipeWireCapture.CopySample(sample, bytes));
            else
            {
                PipeWireCapture.CopySample(sample, bytes);
                foreach (var offset in new[] { 0, 4 * (1920 * 540 + 960), bytes.Length - 4 }) Assert.Equal(new byte[] { 255, 0, 0, 255 }, bytes.AsSpan(offset, 4).ToArray());
            }
        }
        finally { PipeWireCapture.Gst.gst_element_set_state(pipeline, 1); PipeWireCapture.Gst.gst_object_unref(sink); PipeWireCapture.Gst.gst_object_unref(pipeline); }
    }
}
