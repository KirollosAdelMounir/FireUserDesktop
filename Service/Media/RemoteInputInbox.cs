using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using UserIdentityDesktop.Core.Models;
namespace UserIdentityDesktop.Service.Media;

/// <summary>Keeps reliable controls ordered while obsolete pointer positions are replaced.</summary>
internal sealed class RemoteInputInbox
{
    private readonly Channel<(string Sender,RemotePacket Packet)> reliable=Channel.CreateBounded<(string,RemotePacket)>(256);
    private readonly Channel<(string Sender,RemotePacket Packet)> moves=Channel.CreateBounded<(string,RemotePacket)>(new BoundedChannelOptions(1){FullMode=BoundedChannelFullMode.DropOldest});
    private readonly Channel<bool> ready=Channel.CreateBounded<bool>(new BoundedChannelOptions(1){FullMode=BoundedChannelFullMode.DropOldest});
    internal bool TryWrite((string Sender,RemotePacket Packet) item)
    {
        var accepted=(item.Packet.Type=="move"?moves:reliable).Writer.TryWrite(item);
        if(accepted)ready.Writer.TryWrite(true);
        return accepted;
    }
    internal async IAsyncEnumerable<(string Sender,RemotePacket Packet)> ReadAllAsync([EnumeratorCancellation]CancellationToken cancellation)
    {
        while(await ready.Reader.WaitToReadAsync(cancellation))
        {
            ready.Reader.TryRead(out _);
            while(reliable.Reader.TryRead(out var control))yield return control;
            if(moves.Reader.TryRead(out var move))yield return move;
        }
    }
}
