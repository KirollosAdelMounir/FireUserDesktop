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
    private long receivedMoveSequence, sentMoveSequence;
    private DateTimeOffset remoteExpires;
    private DateTimeOffset expires;
    public DateTimeOffset? RequestExpiresAt { get; private set; }
    private DateTimeOffset rateWindow;
    private int rateCount;
    private int moveRateCount;
    public RemoteControlStatus Status => new(Peers.Values.ToArray(), RequestedBy, Controller, Controlling) { RequestExpiresAt = RequestExpiresAt };
    public void Sharing(bool enabled) { Revoke(); LocalScope = enabled ? Guid.NewGuid().ToString("N") : null; }
    public bool Hello(string sender, RemotePacket packet)
    {
        if (packet.Version != 1 || packet.Type != "hello" || sender.Length is < 1 or > 256 || (!Peers.ContainsKey(sender) && Peers.Count >= 64)) return false;
        if (packet.Sharing && !Guid.TryParseExact(packet.Scope, "N", out _)) return false;
        var first = !Peers.ContainsKey(sender);
        if (Controlling == sender && (!packet.Sharing || Peers[sender].Scope != packet.Scope)) StopControlling();
        Peers[sender] = new NativePeer(sender, packet.CanHost, packet.Sharing, packet.Scope) { FastInput = packet.FastInput, ClipboardSupport = packet.ClipboardSupport };
        return first;
    }
    public bool Request(string sender)
    {
        if (LocalScope == null || Controller != null || !Peers.ContainsKey(sender) || RequestedBy != null) return false;
        RequestedBy = sender;
        RequestExpiresAt = time.GetUtcNow().AddSeconds(30);
        return true;
    }
    public string? ExpireRequest()
    {
        if (RequestedBy is not {} sender || time.GetUtcNow() < RequestExpiresAt) return null;
        Deny(sender); return sender;
    }
    public RemotePacket Approve(string sender)
    {
        ExpireRequest();
        if (LocalScope == null || RequestedBy != sender || !Peers.ContainsKey(sender)) throw new InvalidOperationException("This remote-control request is no longer available.");
        Controller = sender; RequestedBy = null; RequestExpiresAt = null; localGrant = Guid.NewGuid().ToString("N"); receivedSequence = receivedMoveSequence = 0;
        expires = time.GetUtcNow().AddMinutes(5);
        rateWindow = time.GetUtcNow(); rateCount = moveRateCount = 0;
        return new RemotePacket("grant", Scope: LocalScope, Grant: localGrant);
    }
    public RemotePacket BeginRequest(string peer)
    {
        if (!Peers.TryGetValue(peer, out var remote) || !remote.CanHost || !remote.Sharing) throw new InvalidOperationException("Remote control requires a participant sharing a supported native desktop with control enabled.");
        outgoingRequest = peer; return new RemotePacket("request", Scope: remote.Scope);
    }
    public bool AcceptGrant(string sender, RemotePacket packet)
    {
        if (outgoingRequest != sender || !Peers.TryGetValue(sender, out var peer) || !peer.Sharing || peer.Scope != packet.Scope || !Guid.TryParseExact(packet.Grant, "N", out _)) return false;
        Controlling = sender; outgoingRequest = null; remoteGrant = packet.Grant; sentSequence = sentMoveSequence = 0; remoteExpires = time.GetUtcNow().AddMinutes(5); return true;
    }
    public RemotePacket? Outgoing(RemoteInput input)
    {
        if (Controlling == null || remoteGrant == null || !input.Target.StartsWith(Controlling + " · ", StringComparison.Ordinal) || !Peers.TryGetValue(Controlling, out var peer)) return null;
        var move = input.Kind == 0 && peer.FastInput;
        return new RemotePacket(move ? "move" : input.Kind == 3 ? "release" : "input", Scope: peer.Scope, Grant: remoteGrant, Sequence: move ? ++sentMoveSequence : ++sentSequence, Input: move?input with{Target=""}:input,PointerSequence:move?0:sentMoveSequence);
    }
    public bool Authorize(string sender, RemotePacket packet)
    {
        var previous = packet.Type == "move" ? receivedMoveSequence : receivedSequence;
        if (LocalScope == null || sender != Controller || packet.Scope != LocalScope || packet.Grant != localGrant || time.GetUtcNow() >= expires || packet.Sequence <= previous || packet.Sequence - previous > 10000) return false;
        var input = packet.Input;
        if (input == null || !double.IsFinite(input.X) || !double.IsFinite(input.Y) || input.X < 0 || input.X > 1 || input.Y < 0 || input.Y > 1 || input.Kind is < 0 or > 3) return false;
        if (packet.Type == "move" && input.Kind != 0) return false;
        if (input.Kind == 1 && input.Code is < 1 or > 3) return false;
        if (input.Kind == 2 && !(input.Code is >= 4 and <= 56 or >= 58 and <= 69 or 76 or >= 79 and <= 82 or >= 224 and <= 231)) return false;
        var now = time.GetUtcNow();
        if (now - rateWindow >= TimeSpan.FromSeconds(1)) { rateWindow = now; rateCount = moveRateCount = 0; }
        if (packet.Type=="move" ? ++moveRateCount>240 : ++rateCount>120) return false;
        if (packet.Type=="move") receivedMoveSequence = packet.Sequence;
        else
        {
            receivedSequence = packet.Sequence;
            if((input.Kind is 1 or 3)&&packet.PointerSequence>receivedMoveSequence&&packet.PointerSequence-receivedMoveSequence<=10000)receivedMoveSequence=packet.PointerSequence;
        }
        return true;
    }
    public RemotePacket? ClipboardPacket(RemoteClipboardMessage message,string? destination=null,RemotePacket? binding=null)
    {
        if (Controller is {} controller && (destination==null||destination==controller) && (binding==null||IsHostedGrantPeer(controller,binding)) && localGrant != null && time.GetUtcNow() < expires && Peers.TryGetValue(controller,out var client) && client.ClipboardSupport)
            return new RemotePacket("clipboard", Scope: LocalScope, Grant: localGrant, Clipboard: message);
        if(binding!=null&&(Controlling==null||!IsControlledGrantPeer(Controlling,binding)))return null;
        if(destination!=null&&destination!=Controlling)return null;
        return ControllerClipboardPacket(message);
    }
    public RemotePacket? ControllerClipboardPacket(RemoteClipboardMessage message)
    {
        if (Controlling is {} host && remoteGrant != null && time.GetUtcNow() < remoteExpires && Peers.TryGetValue(host,out var peer) && peer.ClipboardSupport)
            return new RemotePacket("clipboard", Scope: peer.Scope, Grant: remoteGrant, Clipboard: message);
        return null;
    }
    public bool IsHostedGrantPeer(string sender,RemotePacket packet) => sender == Controller && LocalScope != null && packet.Scope == LocalScope && packet.Grant == localGrant && localGrant != null && time.GetUtcNow() < expires;
    public bool IsControlledGrantPeer(string sender,RemotePacket packet) => sender == Controlling && remoteGrant != null && packet.Grant == remoteGrant && Peers.TryGetValue(sender,out var peer) && peer.Sharing && peer.Scope == packet.Scope && time.GetUtcNow() < remoteExpires;
    public bool IsGrantPeer(string sender, RemotePacket packet) => IsHostedGrantPeer(sender,packet)||IsControlledGrantPeer(sender,packet);
    public void Deny(string sender) { if (RequestedBy == sender) { RequestedBy = null; RequestExpiresAt = null; } if (outgoingRequest == sender) outgoingRequest = null; }
    public void Remove(string sender) { Peers.Remove(sender); Deny(sender); if (Controller == sender) Revoke(); if (Controlling == sender) StopControlling(); }
    public void Revoke() { Controller = null; RequestedBy = null; RequestExpiresAt = null; localGrant = null; receivedSequence = 0; }
    public void StopControlling() { Controlling = null; remoteGrant = null; outgoingRequest = null; }
    public void Clear() { Revoke(); StopControlling(); LocalScope = null; Peers.Clear(); }
    public static bool SupportsDesktopSource(MediaDevices devices, int width, int height)
    {
        if (width <= 0 || height <= 0) return false;
        if (devices.ShareSource is not {} source) return string.IsNullOrWhiteSpace(devices.Screen);
        return source.Kind == ScreenShareKind.Screen && source.Backend is ScreenCaptureBackend.Windows or ScreenCaptureBackend.X11 or ScreenCaptureBackend.MacOS && source.X == 0 && source.Y == 0 && source.Width == width && source.Height == height;
    }
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
