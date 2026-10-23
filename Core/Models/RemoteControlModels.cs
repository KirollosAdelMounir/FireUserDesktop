using System.Collections.Generic;

namespace UserIdentityDesktop.Core.Models;

public sealed record NativePeer(string Identity, bool CanHost, bool Sharing, string? Scope);
public sealed record RemoteInput(string Target, int Kind, double X = 0, double Y = 0, int Code = 0, bool Down = false);
public sealed record RemoteControlStatus(IReadOnlyList<NativePeer> Peers, string? RequestedBy, string? Controller, string? Controlling);
public sealed record RemotePacket(string Type, int Version = 1, bool CanHost = false, bool Sharing = false,
    string? Scope = null, string? Grant = null, long Sequence = 0, RemoteInput? Input = null);
