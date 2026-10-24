using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;
using UserIdentityDesktop.Core.Models;

namespace UserIdentityDesktop.Service.Media;

public sealed class ScreenShareCanceledException() : OperationCanceledException("Screen sharing was cancelled.");

/// <summary>One non-persistent approved ScreenCast or combined RemoteDesktop/ScreenCast session.</summary>
internal sealed partial class ScreenCastPortal : IAsyncDisposable
{
    private const string Service = "org.freedesktop.portal.Desktop", Path = "/org/freedesktop/portal/desktop", Interface = "org.freedesktop.portal.ScreenCast";
    private readonly DBusConnection connection;
    private string? session;
    private IDisposable? closedWatch;
    private int closed;
    private int disposed;
    public bool Closed => Volatile.Read(ref closed) != 0;
    public SafeFileHandle? Remote { get; private set; }
    public uint Node { get; private set; }
    public ulong? Serial { get; private set; }
    private ScreenCastPortal(DBusConnection connection) => this.connection = connection;

    internal static async Task<uint> AvailableSourcesAsync(CancellationToken ct, string? busAddress = null)
    {
        using var connection = new DBusConnection(busAddress ?? DBusAddress.Session ?? throw new InvalidOperationException("The desktop session bus is unavailable."));
        await connection.ConnectAsync().AsTask().WaitAsync(ct);
        MessageBuffer request;
        using (var writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(destination: Service, path: Path, @interface: "org.freedesktop.DBus.Properties", member: "Get", signature: "ss");
            writer.WriteString(Interface); writer.WriteString("AvailableSourceTypes"); request = writer.CreateMessage();
        }
        return await connection.CallMethodAsync(request, (Message message, object? _) => message.GetBodyReader().ReadVariantValue().GetUInt32()).WaitAsync(ct);
    }
    public static async Task<ScreenCastPortal> OpenAsync(ScreenShareKind kind, CancellationToken ct, string? busAddress = null, bool remoteControl = false)
    {
        if(remoteControl && kind!=ScreenShareKind.Screen)throw new InvalidOperationException("Remote control requires a full-screen share.");
        var portal = new ScreenCastPortal(new DBusConnection(busAddress ?? DBusAddress.Session ?? throw new InvalidOperationException("The desktop session bus is unavailable.")));
        portal.remoteDesktop=remoteControl;
        try
        {
            await portal.connection.ConnectAsync().AsTask().WaitAsync(ct);
            var created = await portal.RequestAsync("CreateSession", null, new() { ["session_handle_token"] = "fireuser_" + Guid.NewGuid().ToString("N") }, ct, remoteControl ? RemoteInterface : Interface);
            portal.session = created["session_handle"].GetString();
            portal.closedWatch = await portal.connection.AddMatchAsync(new MatchRule { Type = MessageType.Signal, Sender = Service, Path = portal.session, Interface = "org.freedesktop.portal.Session", Member = "Closed" },
                (Message _, object? _) => true,
                notification => { if (notification.HasValue || notification.IsCompletion) Interlocked.Exchange(ref portal.closed, 1); }, emitOnCapturedContext: false, flags: ObserverFlags.EmitOnConnectionClosed | ObserverFlags.EmitOnReaderFailed);
            if(remoteControl)await portal.RequestAsync("SelectDevices",portal.session,new(){["types"]=3u},ct,RemoteInterface);
            await portal.RequestAsync("SelectSources", portal.session, new() { ["types"] = kind == ScreenShareKind.Screen ? 1u : 2u, ["multiple"] = false }, ct);
            var started = await portal.RequestAsync("Start", portal.session, new(), ct,remoteControl ? RemoteInterface : Interface);
            if(started.TryGetValue("devices",out var devices))portal.GrantedDevices=devices.GetUInt32();
            var streams = started["streams"];
            if (streams.Count != 1) throw new IOException("The compositor did not return one selected screen or window.");
            var stream = streams.GetItem(0); portal.Node = stream.GetItem(0).GetUInt32();
            var properties = stream.GetItem(1).GetDictionary<string, VariantValue>();
            if(properties.TryGetValue("size",out var size)){portal.LogicalWidth=size.GetItem(0).GetInt32();portal.LogicalHeight=size.GetItem(1).GetInt32();}
            portal.monitorSource=!properties.TryGetValue("source_type",out var sourceType)||sourceType.GetUInt32()==1;
            if (properties.TryGetValue("pipewire-serial", out var serial)) portal.Serial = serial.GetUInt64();
            MessageBuffer request;
            using (var writer = portal.connection.GetMessageWriter())
            {
                writer.WriteMethodCallHeader(destination: Service, path: Path, @interface: Interface, member: "OpenPipeWireRemote", signature: "oa{sv}");
                writer.WriteObjectPath(portal.session); writer.WriteDictionary(new Dictionary<string, VariantValue>()); request = writer.CreateMessage();
            }
            portal.Remote = await portal.connection.CallMethodAsync(request, (Message message, object? _) => message.GetBodyReader().ReadHandle<SafeFileHandle>()).WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            return portal;
        }
        catch (DBusErrorReplyException error) when(remoteControl && (error.ErrorName=="org.freedesktop.DBus.Error.UnknownInterface"||error.ErrorName=="org.freedesktop.DBus.Error.UnknownMethod"))
        {await portal.DisposeAsync();throw new InvalidOperationException("This desktop portal does not support the required keyboard and pointer permission. Check that a compatible RemoteDesktop portal backend is installed.",error);}
        catch { await portal.DisposeAsync(); throw; }
    }
    private async Task<Dictionary<string, VariantValue>> RequestAsync(string method, string? sessionPath, Dictionary<string, VariantValue> options, CancellationToken ct,string? targetInterface=null)
    {
        var token = "fireuser_" + Guid.NewGuid().ToString("N"); options["handle_token"] = token;
        var requestPath = "/org/freedesktop/portal/desktop/request/" + connection.UniqueName!.TrimStart(':').Replace('.', '_') + "/" + token;
        var response = new TaskCompletionSource<(uint Code, Dictionary<string, VariantValue> Results)>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Subscribe before issuing the method: the compositor may respond before its reply arrives.
        using var watch = await connection.AddMatchAsync(new MatchRule { Type = MessageType.Signal, Sender = Service, Path = requestPath, Interface = "org.freedesktop.portal.Request", Member = "Response" },
            (Message message, object? _) => { var reader = message.GetBodyReader(); return (reader.ReadUInt32(), reader.ReadDictionaryOfStringToVariantValue()); },
            notification => { if (notification.HasValue) response.TrySetResult(notification.Value); else if (notification.IsCompletion) response.TrySetException(notification.Exception ?? new IOException("The screen sharing portal disconnected.")); }, emitOnCapturedContext: false, flags: ObserverFlags.EmitOnConnectionClosed | ObserverFlags.EmitOnReaderFailed);
        try
        {
            MessageBuffer request;
            using (var writer = connection.GetMessageWriter())
            {
                writer.WriteMethodCallHeader(destination: Service, path: Path, @interface: targetInterface??Interface, member: method, signature: sessionPath == null ? "a{sv}" : method == "Start" ? "osa{sv}" : "oa{sv}");
                if (sessionPath != null) writer.WriteObjectPath(sessionPath);
                if (method == "Start") writer.WriteString(""); // Unparented portal window; no fabricated Wayland surface token.
                writer.WriteDictionary(options); request = writer.CreateMessage();
            }
            var returned = await connection.CallMethodAsync(request, (Message message, object? _) => message.GetBodyReader().ReadObjectPathAsString()).WaitAsync(ct);
            if (returned != requestPath) throw new IOException("The screen sharing portal returned an unexpected request handle.");
            var result = await response.Task.WaitAsync(ct);
            if (result.Code == 1) throw new ScreenShareCanceledException();
            if (result.Code != 0) throw new IOException("The desktop portal could not approve the selected screen or window.");
            return result.Results;
        }
        catch
        {
            try { await CloseAsync(requestPath, "org.freedesktop.portal.Request").WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            throw;
        }
    }
    private Task CloseAsync(string path, string @interface)
    {
        using var writer = connection.GetMessageWriter(); writer.WriteMethodCallHeader(destination: Service, path: path, @interface: @interface, member: "Close");
        return connection.CallMethodAsync(writer.CreateMessage());
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        DisableInput();
        await ReleaseInputAsync();
        Interlocked.Exchange(ref closed, 1);
        closedWatch?.Dispose(); closedWatch = null;
        if (session != null) { try { await CloseAsync(session, "org.freedesktop.portal.Session").WaitAsync(TimeSpan.FromSeconds(2)); } catch { } session = null; }
        Remote?.Dispose(); Remote = null; connection.Dispose();
    }
}
