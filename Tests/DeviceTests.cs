using UserIdentityDesktop.Service.Media;
using Xunit;

namespace UserIdentityDesktop.Tests;

public sealed class DeviceTests
{
    [Fact]
    public void WindowsDiscovery_SelectsVideoNamesAndIgnoresAudioAndAlternativeNames()
    {
        var result = NativeMediaSession.ParseCameras("[dshow] \"USB Camera\" (video)\n[dshow] Alternative name \"@device:pnp:test\"\n[dshow] \"Microphone\" (audio)", true).ToArray();
        Assert.Single(result); Assert.Equal("USB Camera", result[0].Id);
    }
    [Fact]
    public void MacDiscovery_UsesVideoIndicesAndExcludesScreenAndMicrophone()
    {
        var result = NativeMediaSession.ParseCameras("[avfoundation] AVFoundation video devices:\n[avfoundation] [0] FaceTime HD Camera\n[avfoundation] [1] Capture screen 0\n[avfoundation] AVFoundation audio devices:\n[avfoundation] [0] MacBook Microphone", false).ToArray();
        Assert.Single(result); Assert.Equal("0", result[0].Id); Assert.Equal("FaceTime HD Camera", result[0].Name);
    }
    [Fact]
    public void PackagedCameraRuntime_IsFoundWithoutSystemPath()
    {
        var path = NativeCapture.ResolveExecutable("ffmpeg");
        Assert.True(File.Exists(path)); Assert.StartsWith(AppContext.BaseDirectory, path);
    }
    [Fact]
    public void InvalidAudioFormat_IsRejectedWithoutOpeningDevices()
    {
        Assert.Equal(IntPtr.Zero, LiveKitNative.fu_audio_open("", 0, -1, 1, null));
        Assert.Equal(IntPtr.Zero, LiveKitNative.fu_audio_open("", 1, 48000, 1, null));
    }
}
