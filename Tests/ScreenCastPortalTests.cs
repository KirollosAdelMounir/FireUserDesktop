using System.Diagnostics;
using System.Collections.Concurrent;
using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;
using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.Service.Media;
using Xunit;

namespace UserIdentityDesktop.Tests;

public sealed class ScreenCastPortalTests
{
    private sealed class FakePortal : IPathMethodHandler, IDisposable
    {
        public string Path => "/org/freedesktop/portal/desktop";
        public bool HandlesChildPaths => true;
        public required DBusConnection Connection { get; init; }
        public required string Address { get; init; }
        public required Process Bus { get; init; }
        public ConcurrentQueue<string> Methods { get; } = new();
        public uint Types { get; private set; }
        public bool Multiple { get; private set; }
        public bool Persist { get; private set; }
        public uint Result { get; set; }
        public bool HoldStart { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SafeFileHandle remote = File.OpenHandle("/dev/null", FileMode.Open, FileAccess.Read);
        private const string SessionPath = "/org/freedesktop/portal/desktop/session/test/selected";
        public static async Task<FakePortal> Create()
        {
            if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/dbus-daemon")) { Assert.Skip("Isolated portal protocol tests require dbus-daemon on Linux."); }
            var info = new ProcessStartInfo("/usr/bin/dbus-daemon") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var value in new[] { "--session", "--nofork", "--print-address=1" }) info.ArgumentList.Add(value);
            var bus = Process.Start(info)!;
            var address = (await bus.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!;
            var connection = new DBusConnection(address); await connection.ConnectAsync();
            var result = new FakePortal { Connection = connection, Address = address, Bus = bus };
            connection.AddMethodHandler(result); await connection.RequestNameAsync("org.freedesktop.portal.Desktop");
            return result;
        }
        public ValueTask HandleMethodAsync(MethodContext context)
        {
            var member = context.Request.MemberAsString!; Methods.Enqueue(member);
            var reader = context.Request.GetBodyReader();
            if (member == "Get")
            {
                using var reply = context.CreateReplyWriter("v"); reply.WriteVariant(3u); context.Reply(reply.CreateMessage()); return default;
            }
            if (member == "Close") { using var reply = context.CreateReplyWriter(null); context.Reply(reply.CreateMessage()); return default; }
            if (member == "OpenPipeWireRemote")
            {
                using var reply = context.CreateReplyWriter("h"); reply.WriteHandle(remote); context.Reply(reply.CreateMessage()); return default;
            }
            if (member != "CreateSession") reader.ReadObjectPath();
            if (member == "Start") reader.ReadString();
            var options = reader.ReadDictionaryOfStringToVariantValue(); Persist |= options.ContainsKey("persist_mode") || options.ContainsKey("restore_token");
            if (member == "SelectSources") { Types = options["types"].GetUInt32(); Multiple = options["multiple"].GetBool(); }
            var token = options["handle_token"].GetString(); var sender = context.Request.SenderAsString!;
            var path = "/org/freedesktop/portal/desktop/request/" + sender.TrimStart(':').Replace('.', '_') + "/" + token;
            var results = new Dictionary<string, VariantValue>();
            if (member == "CreateSession") results["session_handle"] = SessionPath;
            if (member == "Start") results["streams"] = new Tmds.DBus.Protocol.Array<Struct<uint, Dict<string, VariantValue>>> { Struct.Create(42u, new Dict<string, VariantValue> { ["pipewire-serial"] = 123456ul }) };
            // Intentionally deliver Response before the method reply to exercise the race.
            if (member != "Start" || !HoldStart)
            {
                using var signal = Connection.GetMessageWriter();
                signal.WriteSignalHeader(destination: sender, path: path, @interface: "org.freedesktop.portal.Request", member: "Response", signature: "ua{sv}");
                signal.WriteUInt32(member == "Start" ? Result : 0); signal.WriteDictionary(results); Connection.TrySendMessage(signal.CreateMessage());
            }
            using (var reply = context.CreateReplyWriter("o")) { reply.WriteObjectPath(path); context.Reply(reply.CreateMessage()); }
            if (member == "Start") Started.TrySetResult();
            return default;
        }
        public void Revoke()
        {
            using var signal = Connection.GetMessageWriter(); signal.WriteSignalHeader(path: SessionPath, @interface: "org.freedesktop.portal.Session", member: "Closed", signature: "a{sv}"); signal.WriteDictionary(new Dictionary<string, VariantValue>()); Connection.TrySendMessage(signal.CreateMessage());
        }
        public void Dispose()
        {
            remote.Dispose(); Connection.Dispose();
            if (!Bus.HasExited) Bus.Kill(); Bus.Dispose();
        }
    }
    [Theory]
    [InlineData(ScreenShareKind.Screen, 1u)] [InlineData(ScreenShareKind.Window, 2u)]
    public async Task ApprovedSource_UsesPortalSessionAndRemoteFd_AndClosesOnDispose(ScreenShareKind kind, uint types)
    {
        using var server = await FakePortal.Create(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal(3u, await ScreenCastPortal.AvailableSourcesAsync(timeout.Token, server.Address));
        var portal = await ScreenCastPortal.OpenAsync(kind, timeout.Token, server.Address);
        Assert.Equal(types, server.Types); Assert.False(server.Multiple); Assert.False(server.Persist);
        Assert.Equal(42u, portal.Node); Assert.Equal(123456ul, portal.Serial); var fd = portal.Remote!; Assert.False(fd.IsInvalid); Assert.False(fd.IsClosed);
        Assert.Equal(new[] { "Get", "CreateSession", "SelectSources", "Start", "OpenPipeWireRemote" }, server.Methods);
        await portal.DisposeAsync(); Assert.True(fd.IsClosed); Assert.Equal(1, server.Methods.Count(x => x == "Close"));
        await portal.DisposeAsync(); Assert.Equal(1, server.Methods.Count(x => x == "Close"));
    }
    [Theory]
    [InlineData(1u)] [InlineData(2u)]
    public async Task CancelOrDenial_ClosesSessionWithoutOpeningPipeWireRemote(uint code)
    {
        using var server = await FakePortal.Create(); server.Result = code; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (code == 1) await Assert.ThrowsAsync<ScreenShareCanceledException>(() => ScreenCastPortal.OpenAsync(ScreenShareKind.Window, timeout.Token, server.Address));
        else await Assert.ThrowsAsync<IOException>(() => ScreenCastPortal.OpenAsync(ScreenShareKind.Window, timeout.Token, server.Address));
        Assert.DoesNotContain("OpenPipeWireRemote", server.Methods); Assert.Contains("Close", server.Methods);
    }
    [Fact]
    public async Task CancelWhileChooserIsOpen_ClosesRequestAndSession()
    {
        using var server = await FakePortal.Create(); server.HoldStart = true; using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var opening = ScreenCastPortal.OpenAsync(ScreenShareKind.Screen, cancel.Token, server.Address);
        await server.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
        Assert.DoesNotContain("OpenPipeWireRemote", server.Methods); Assert.Equal(2, server.Methods.Count(x => x == "Close"));
    }
    [Fact]
    public async Task CompositorRevocation_MarksSessionClosed()
    {
        using var server = await FakePortal.Create(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var portal = await ScreenCastPortal.OpenAsync(ScreenShareKind.Screen, timeout.Token, server.Address);
        server.Revoke();
        for (var i = 0; i < 100 && !portal.Closed; i++) await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.True(portal.Closed);
    }
}
