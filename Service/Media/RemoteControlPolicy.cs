using System;
using System.Collections.Generic;
using System.Linq;
using UserIdentityDesktop.Core.Models;

namespace UserIdentityDesktop.Service.Media;

/// <summary>Authorization is per authenticated LiveKit sender, consent, share and expiring grant.</summary>
public sealed class RemoteControlPolicy(TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    public Dictionary<string, NativePeer> Peers { get; } = new(StringComparer.Ordinal);
    public string? LocalScope { get; private set; }
    public string? RequestedBy { get; private set; }
    public string? Controller { get; private set; }
    public string? Controlling { get; private set; }
    private string? localGrant, remoteGrant, outgoingRequest;
    private long receivedSequence, sentSequence;
    private DateTimeOffset expires;
    private DateTimeOffset rateWindow;
    private int rateCount;
    public RemoteControlStatus Status => new(Peers.Values.ToArray(), RequestedBy, Controller, Controlling);
    public void Sharing(bool enabled) { Revoke(); LocalScope = enabled ? Guid.NewGuid().ToString("N") : null; }
    public bool Hello(string sender, RemotePacket packet)
    {
        if (packet.Version != 1 || packet.Type != "hello" || sender.Length is < 1 or > 256 || (!Peers.ContainsKey(sender) && Peers.Count >= 64)) return false;
        if (packet.Sharing && !Guid.TryParseExact(packet.Scope, "N", out _)) return false;
        var first = !Peers.ContainsKey(sender);
        if (Controlling == sender && (!packet.Sharing || Peers[sender].Scope != packet.Scope)) StopControlling();
        Peers[sender] = new NativePeer(sender, packet.CanHost, packet.Sharing, packet.Scope);
        return first;
    }
    public bool Request(string sender) => LocalScope != null && Controller == null && Peers.ContainsKey(sender) && (RequestedBy == null || RequestedBy == sender) && (RequestedBy = sender) != null;
    public RemotePacket Approve(string sender)
    {
        if (LocalScope == null || RequestedBy != sender || !Peers.ContainsKey(sender)) throw new InvalidOperationException("This remote-control request is no longer available.");
        Controller = sender; RequestedBy = null; localGrant = Guid.NewGuid().ToString("N"); receivedSequence = 0;
        expires = time.GetUtcNow().AddMinutes(5);
        rateWindow = time.GetUtcNow(); rateCount = 0;
        return new RemotePacket("grant", Scope: LocalScope, Grant: localGrant);
    }
    public RemotePacket BeginRequest(string peer)
    {
        if (!Peers.TryGetValue(peer, out var remote) || !remote.CanHost || !remote.Sharing) throw new InvalidOperationException("Remote control requires a native client sharing its desktop. Browser clients cannot offer remote control.");
        outgoingRequest = peer; return new RemotePacket("request", Scope: remote.Scope);
    }
    public bool AcceptGrant(string sender, RemotePacket packet)
    {
        if (outgoingRequest != sender || !Peers.TryGetValue(sender, out var peer) || !peer.Sharing || peer.Scope != packet.Scope || !Guid.TryParseExact(packet.Grant, "N", out _)) return false;
        Controlling = sender; outgoingRequest = null; remoteGrant = packet.Grant; sentSequence = 0; return true;
    }
    public RemotePacket? Outgoing(RemoteInput input)
    {
        if (Controlling == null || remoteGrant == null || !input.Target.StartsWith(Controlling + " · ", StringComparison.Ordinal) || !Peers.TryGetValue(Controlling, out var peer)) return null;
        return new RemotePacket(input.Kind == 3 ? "release" : "input", Scope: peer.Scope, Grant: remoteGrant, Sequence: ++sentSequence, Input: input);
    }
    public bool Authorize(string sender, RemotePacket packet)
    {
        if (LocalScope == null || sender != Controller || packet.Scope != LocalScope || packet.Grant != localGrant || time.GetUtcNow() >= expires || packet.Sequence <= receivedSequence || packet.Sequence - receivedSequence > 10000) return false;
        var input = packet.Input;
        if (input == null || !double.IsFinite(input.X) || !double.IsFinite(input.Y) || input.X < 0 || input.X > 1 || input.Y < 0 || input.Y > 1 || input.Kind is < 0 or > 3) return false;
        if (input.Kind == 1 && input.Code is < 1 or > 3) return false;
        if (input.Kind == 2 && !(input.Code is >= 4 and <= 56 or >= 58 and <= 69 or 76 or >= 79 and <= 82 or >= 224 and <= 231)) return false;
        var now = time.GetUtcNow();
        if (now - rateWindow >= TimeSpan.FromSeconds(1)) { rateWindow = now; rateCount = 0; }
        if (++rateCount > 120) return false;
        receivedSequence = packet.Sequence; return true;
    }
    public void Deny(string sender) { if (RequestedBy == sender) RequestedBy = null; if (outgoingRequest == sender) outgoingRequest = null; }
    public void Remove(string sender) { Peers.Remove(sender); Deny(sender); if (Controller == sender) Revoke(); if (Controlling == sender) StopControlling(); }
    public void Revoke() { Controller = null; RequestedBy = null; localGrant = null; receivedSequence = 0; }
    public void StopControlling() { Controlling = null; remoteGrant = null; outgoingRequest = null; }
    public void Clear() { Revoke(); StopControlling(); LocalScope = null; Peers.Clear(); }
    public static bool TryMapScreenPoint(double x, double y, int width, int height, out double screenX, out double screenY)
    {
        screenX = screenY = 0;
        if (width <= 0 || height <= 0 || !double.IsFinite(x) || !double.IsFinite(y)) return false;
        var scale = Math.Min(960d / width, 540d / height);
        var contentWidth = width * scale; var contentHeight = height * scale;
        screenX = (x * 960 - (960 - contentWidth) / 2) / contentWidth;
        screenY = (y * 540 - (540 - contentHeight) / 2) / contentHeight;
        return screenX is >= 0 and <= 1 && screenY is >= 0 and <= 1;
    }
}
