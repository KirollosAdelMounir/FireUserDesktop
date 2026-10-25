using System.Collections.Generic;

namespace UserIdentityDesktop.Core.Models;

public sealed record NativePeer(string Identity, bool CanHost, bool Sharing, string? Scope)
{
    public bool FastInput { get; init; }
    public bool ClipboardSupport { get; init; }
    public bool ExtendedInput { get; init; }
    public string DisplayName { get; init; } = "Participant";
    public string ScreenLabel => DisplayName + "'s screen";
}
public sealed record RemoteInput(string Target, int Kind, double X = 0, double Y = 0, int Code = 0, bool Down = false, bool PositionIncluded = false, double DeltaX = 0, double DeltaY = 0,[property:System.Text.Json.Serialization.JsonIgnore] long ControlEpoch=0);
public sealed record RemoteControlStatus(IReadOnlyList<NativePeer> Peers, string? RequestedBy, string? Controller, string? Controlling)
{
    public System.DateTimeOffset? RequestExpiresAt { get; init; }
    public string? HostUnavailableReason { get; init; }
    public double? InputRoundTripMs { get; init; }
    public long InputEpoch {get;init;}
}
public sealed record RemotePacket(string Type, int Version = 1, bool CanHost = false, bool Sharing = false,
    string? Scope = null, string? Grant = null, long Sequence = 0, RemoteInput? Input = null,
    bool FastInput = false, bool ClipboardSupport = false, long Probe = 0, RemoteClipboardMessage? Clipboard = null,long PointerSequence = 0,bool ExtendedInput=false);
public sealed record RemoteClipboardMessage(string Operation, string Id, long Offset = 0, long Length = 0, string? Kind = null, string? Hash = null, byte[]? Bytes = null, bool Paste = false,int Items = 1);
