using UserIdentityDesktop.Service.Media;
using System.IdentityModel.Tokens.Jwt;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace UserIdentityDesktop.Tests;

public sealed class NativeBridgeTests
{
    [Theory]
    [InlineData(160, 90)]
    [InlineData(1920, 1080)]
    public async Task TwoNativeClients_ExchangeSyntheticMediaAndAuthenticatedData(int width, int height)
    {
        var url = Environment.GetEnvironmentVariable("FIREUSER_LIVEKIT_TEST_URL");
        if (url == null) { Assert.Skip("Set FIREUSER_LIVEKIT_TEST_URL and short-lived participant tokens (or a test key/secret) to run the transport test."); return; }
        Assert.True(Uri.TryCreate(url, UriKind.Absolute, out var endpoint) &&
            (endpoint.Scheme == "wss" || endpoint.Scheme == "ws" && endpoint.IsLoopback));
        var apiKey = Environment.GetEnvironmentVariable("FIREUSER_LIVEKIT_TEST_KEY")!;
        var secret = Environment.GetEnvironmentVariable("FIREUSER_LIVEKIT_TEST_SECRET")!;
        var roomName = "native-test-" + Guid.NewGuid().ToString("N");
        var videoArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var screenArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var audioArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dataArrived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lastVideo = "none";
        var duration = int.TryParse(Environment.GetEnvironmentVariable("FIREUSER_LIVEKIT_TEST_DURATION_SECONDS"), out var seconds) ? Math.Clamp(seconds, 0, 180) : 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long lastCamera = 0, lastScreen = 0, lastAudio = 0, lastData = 0;
        var disconnected = 0;
        LiveKitNative.VideoCallback video = (identity, pixels, w, h, length) =>
        {
            lastVideo = $"{w}x{h}, {length} bytes, {Marshal.PtrToStringUTF8(identity)}";
            // WebRTC may scale under load and codecs round dimensions. Verify decoded content
            // at a usable resolution; this is transport coverage, not a fixed-quality benchmark.
            if (w >= Math.Min(width / 2 - 4, 320) && h >= Math.Min(height / 2 - 4, 180) && length == w * h * 4 && Marshal.ReadByte(pixels) > 180 && Marshal.ReadByte(pixels, 2) < 80)
            {
                if (Marshal.PtrToStringUTF8(identity)!.Contains(" · screen · ")) { Interlocked.Exchange(ref lastScreen, clock.ElapsedMilliseconds); screenArrived.TrySetResult(); }
                else { Interlocked.Exchange(ref lastCamera, clock.ElapsedMilliseconds); videoArrived.TrySetResult(); }
            }
        };
        LiveKitNative.AudioCallback audio = (_, _, length, _, _) => { if (length > 0) { Interlocked.Exchange(ref lastAudio, clock.ElapsedMilliseconds); audioArrived.TrySetResult(); } };
        LiveKitNative.EventCallback events = (kind, _) => { if (kind == 2) Interlocked.Increment(ref disconnected); };
        LiveKitNative.DataCallback data = (sender, _, _) => { Interlocked.Exchange(ref lastData, clock.ElapsedMilliseconds); dataArrived.TrySetResult(Marshal.PtrToStringUTF8(sender)!); };
        string Token(string identity)
        {
            var supplied = Environment.GetEnvironmentVariable(identity == "native-sender"
                ? "FIREUSER_LIVEKIT_TEST_SENDER_TOKEN" : "FIREUSER_LIVEKIT_TEST_RECEIVER_TOKEN");
            if (!string.IsNullOrEmpty(supplied)) return supplied;
            var now = DateTime.UtcNow;
            return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(apiKey,
                claims: new[] { new Claim("sub", identity), new Claim("video", JsonSerializer.Serialize(new { roomJoin = true, room = roomName, canPublish = true, canSubscribe = true, canPublishData = true }), JsonClaimValueTypes.Json) },
                notBefore: now.AddSeconds(-5), expires: now.AddMinutes(5), signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)), SecurityAlgorithms.HmacSha256)));
        }
        var senderToken = Token("native-sender");
        var receiverToken = Token("native-receiver");
        // Subjects are used only to address test packets; LiveKit validates the tokens.
        var senderIdentity = new JwtSecurityTokenHandler().ReadJwtToken(senderToken).Subject;
        var receiverIdentity = new JwtSecurityTokenHandler().ReadJwtToken(receiverToken).Subject;
        var sender = LiveKitNative.fu_create(video, audio, events, data);
        var receiver = LiveKitNative.fu_create(video, audio, events, data);
        Assert.NotEqual(IntPtr.Zero, sender); Assert.NotEqual(IntPtr.Zero, receiver);
        try
        {
            Assert.Equal(0, await Task.Run(() => LiveKitNative.fu_connect(sender, url, senderToken), TestContext.Current.CancellationToken));
            Assert.Equal(0, await Task.Run(() => LiveKitNative.fu_connect(receiver, url, receiverToken), TestContext.Current.CancellationToken));
            Assert.Equal(0, LiveKitNative.fu_publish(sender, 1, width, height));
            Assert.Equal(0, LiveKitNative.fu_publish(sender, 2, width, height));
            Assert.Equal(0, LiveKitNative.fu_publish(sender, 0, width, height));
            var pixels = new byte[width * height * 4];
            for (var offset = 0; offset < pixels.Length; offset += 4)
            { pixels[offset] = 255; pixels[offset + 1] = 80; pixels[offset + 3] = 255; }
            var samples = new byte[960];
            var packet = Encoding.UTF8.GetBytes("{\"type\":\"hello\",\"version\":1}");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(duration + 20));
            var until = Task.WhenAll(videoArrived.Task, screenArrived.Task, audioArrived.Task, dataArrived.Task);
            var started = clock.Elapsed;
            while (!until.IsCompleted || clock.Elapsed - started < TimeSpan.FromSeconds(duration))
            {
                Assert.False(timeout.IsCancellationRequested,
                    $"Receive timeout: video={videoArrived.Task.IsCompleted} ({lastVideo}), screen={screenArrived.Task.IsCompleted}, audio={audioArrived.Task.IsCompleted}, data={dataArrived.Task.IsCompleted}.");
                Assert.Equal(0, LiveKitNative.fu_push(sender, 1, pixels, pixels.Length));
                Assert.Equal(0, LiveKitNative.fu_push(sender, 2, pixels, pixels.Length));
                for (var index = 0; index < samples.Length / 2; index++)
                    System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan(index * 2, 2), (short)(Math.Sin(index * .15) * 5000));
                Assert.Equal(0, LiveKitNative.fu_push(sender, 0, samples, samples.Length));
                Assert.Equal(0, LiveKitNative.fu_send_data(sender, receiverIdentity, packet, packet.Length));
                await Task.Delay(30, TestContext.Current.CancellationToken);
            }
            await until; Assert.Equal(senderIdentity, await dataArrived.Task);
            Assert.Equal(0, Volatile.Read(ref disconnected));
            if (duration > 0)
                foreach (var received in new[] { lastCamera, lastScreen, lastAudio, lastData })
                    Assert.InRange(clock.ElapsedMilliseconds - received, 0, 5000);
        }
        finally
        {
            // Cleanup must finish even if the test timeout was cancelled.
            await Task.Run(() => LiveKitNative.fu_destroy(sender), CancellationToken.None);
            await Task.Run(() => LiveKitNative.fu_destroy(receiver), CancellationToken.None);
            GC.KeepAlive(video); GC.KeepAlive(audio); GC.KeepAlive(events); GC.KeepAlive(data);
        }
    }
    [Fact]
    public void ManagedCallbacks_MatchCppAbi_AndDisconnectedRoomDisposes()
    {
        Assert.Equal(7, LiveKitNative.fu_abi_version());
        LiveKitNative.VideoCallback video = (_, _, _, _, _) => { };
        LiveKitNative.AudioCallback audio = (_, _, _, _, _) => { };
        LiveKitNative.EventCallback events = (_, _) => { };
        LiveKitNative.DataCallback data = (_, _, _) => { };
        var room = LiveKitNative.fu_create(video, audio, events, data);
        Assert.NotEqual(IntPtr.Zero, room);
        try { Assert.Equal(-1, LiveKitNative.fu_publish(room, 0, 960, 540)); }
        finally { LiveKitNative.fu_destroy(room); }
        GC.KeepAlive(video); GC.KeepAlive(audio); GC.KeepAlive(events); GC.KeepAlive(data);
    }
}
