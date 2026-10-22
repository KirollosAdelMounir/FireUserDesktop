using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Threading;
using Tmds.DBus.Protocol;

namespace UserIdentityDesktop.Service;

/// <summary>Persistent action listener; OS-history clicks must outlive a notify-send process.</summary>
internal sealed class LinuxNotifications
{
    private const string Service = "org.freedesktop.Notifications", Path = "/org/freedesktop/Notifications";
    private DBusConnection? connection;
    private IDisposable? actions;
    private readonly ConcurrentDictionary<uint, string> routes = new();
    private readonly ConcurrentQueue<uint> order = new();
    public async Task ShowAsync(string title, string body, string target, string? icon, Action<string> activate)
    {
        try
        {
            if (connection == null)
            {
                connection = new DBusConnection(DBusAddress.Session ?? throw new InvalidOperationException("The desktop session bus is unavailable."));
                await connection.ConnectAsync();
                actions = await connection.AddMatchAsync(new MatchRule { Type = MessageType.Signal, Sender = Service, Path = Path, Interface = Service, Member = "ActionInvoked" },
                    (Message message, object? _) => { var reader = message.GetBodyReader(); return (Id: reader.ReadUInt32(), Action: reader.ReadString()); },
                    notification =>
                    {
                        if (notification.HasValue && notification.Value.Action == "default" && routes.TryGetValue(notification.Value.Id, out var route))
                            Dispatcher.UIThread.Post(() => activate(route));
                    }, emitOnCapturedContext: false);
            }
            MessageBuffer request;
            using (var writer = connection.GetMessageWriter())
            {
                writer.WriteMethodCallHeader(destination: Service, path: Path, @interface: Service, member: "Notify", signature: "susssasa{sv}i");
                writer.WriteString("Fire User Connect"); writer.WriteUInt32(0); writer.WriteString(icon ?? "");
                writer.WriteString(title); writer.WriteString(System.Security.SecurityElement.Escape(body) ?? ""); writer.WriteArray(new[] { "default", "Open details" });
                writer.WriteDictionary(new Dictionary<string, VariantValue> { ["desktop-entry"] = "fire-user-connect", ["suppress-sound"] = true, ["transient"] = false, ["urgency"] = (byte)0 });
                writer.WriteInt32(6000); request = writer.CreateMessage();
            }
            var id = await connection.CallMethodAsync(request, (Message message, object? _) => message.GetBodyReader().ReadUInt32()).WaitAsync(TimeSpan.FromSeconds(5));
            routes[id] = target; order.Enqueue(id);
            while (order.Count > 500 && order.TryDequeue(out var expired)) routes.TryRemove(expired, out _);
        }
        catch
        {
            actions?.Dispose(); actions = null; connection?.Dispose(); connection = null;
            throw;
        }
    }
}
