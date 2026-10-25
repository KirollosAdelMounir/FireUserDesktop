using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.Service.Media;
using Xunit;

namespace UserIdentityDesktop.Tests;

public sealed class RemoteControlTests
{
    [Fact]
    public void ClickPositionFence_RejectsLatePointerAndPreservesNextMove()
    {
        var host=new RemoteControlPolicy();host.Sharing(true);host.Hello("controller",new("hello",FastInput:true));host.Request("controller");var grant=host.Approve("controller");
        var controller=new RemoteControlPolicy();controller.Hello("host",new("hello",CanHost:true,Sharing:true,Scope:grant.Scope,FastInput:true));controller.BeginRequest("host");controller.AcceptGrant("host",grant);
        var older=controller.Outgoing(new("host · screen · track",0,.1,.2))!;
        var click=controller.Outgoing(new("host · screen · track",1,.8,.9,1,true,true))!;
        Assert.Equal(older.Sequence,click.PointerSequence);Assert.True(click.Input!.PositionIncluded);
        Assert.True(host.Authorize("controller",click));Assert.False(host.Authorize("controller",older));
        Assert.True(host.Authorize("controller",controller.Outgoing(new("host · screen · track",0,.7,.8))!));
    }
    [Fact]
    public void FastPointerPacket_FitsLossyMtuEvenWithLongParticipantIdentity()
    {
        var peer=new string('م',256);var scope=Guid.NewGuid().ToString("N");var policy=new RemoteControlPolicy();
        policy.Hello(peer,new("hello",CanHost:true,Sharing:true,Scope:scope,FastInput:true));policy.BeginRequest(peer);policy.AcceptGrant(peer,new("grant",Scope:scope,Grant:Guid.NewGuid().ToString("N")));
        var packet=policy.Outgoing(new(peer+" · screen · track",0,.123,.987))!;
        Assert.Equal("",packet.Input!.Target);
        Assert.True(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(packet,UserIdentityDesktop.Data.ApiPayload.Json).Length<=1300);
    }
    [Fact]
    public void ReciprocalControl_CannotUseRemoteGrantForExpiredLocalClipboardAuthority()
    {
        var clock=new Clock();var policy=new RemoteControlPolicy(clock);policy.Sharing(true);var remoteScope=Guid.NewGuid().ToString("N");
        policy.Hello("peer",new("hello",CanHost:true,Sharing:true,Scope:remoteScope,ClipboardSupport:true));policy.Request("peer");var local=policy.Approve("peer");
        clock.Now=clock.Now.AddMinutes(1);policy.BeginRequest("peer");var remote=new RemotePacket("grant",Scope:remoteScope,Grant:Guid.NewGuid().ToString("N"));policy.AcceptGrant("peer",remote);
        clock.Now=clock.Now.AddMinutes(4);
        Assert.False(policy.IsHostedGrantPeer("peer",remote));Assert.True(policy.IsControlledGrantPeer("peer",remote));
        var message=new RemoteClipboardMessage("ack",Guid.NewGuid().ToString("N"));
        Assert.Equal(remote.Grant,policy.ClipboardPacket(message,"peer",remote)!.Grant);Assert.Null(policy.ClipboardPacket(message,"peer",local));
    }
    [Fact]
    public void FastPointerSequence_CannotInvalidateReliableKeyOrReplayAnotherMove()
    {
        var host=new RemoteControlPolicy();host.Sharing(true);host.Hello("controller",new("hello",FastInput:true,ClipboardSupport:true));host.Request("controller");
        var grant=host.Approve("controller");
        var move=new RemotePacket("move",Scope:grant.Scope,Grant:grant.Grant,Sequence:20,Input:new("",0,.5,.5));
        Assert.True(host.Authorize("controller",move));
        Assert.True(host.Authorize("controller",move with{Type="input",Sequence=1,Input=new("",2,Code:227,Down:true)}));
        Assert.False(host.Authorize("controller",move with{Sequence=19}));
        Assert.False(host.Authorize("controller",move with{Sequence=21,Input=new("",2,Code:227,Down:true)}));
        Assert.False(host.Authorize("intruder",move with{Sequence=21}));
    }
    [Fact]
    public void ClipboardMessages_RequireExactPeerScopeGrantAndLiveConsentInBothDirections()
    {
        var clock=new Clock();var host=new RemoteControlPolicy(clock);host.Sharing(true);
        var controller=new RemoteControlPolicy(clock);
        host.Hello("controller",new("hello",ClipboardSupport:true));
        controller.Hello("host",new("hello",CanHost:true,Sharing:true,Scope:host.LocalScope,ClipboardSupport:true,FastInput:true));
        Assert.Null(controller.ClipboardPacket(new("request",Guid.NewGuid().ToString("N"))));
        controller.BeginRequest("host");host.Request("controller");var grant=host.Approve("controller");Assert.True(controller.AcceptGrant("host",grant));
        var outgoing=controller.ClipboardPacket(new("request",Guid.NewGuid().ToString("N")))!;
        Assert.True(host.IsGrantPeer("controller",outgoing));Assert.False(host.IsGrantPeer("other",outgoing));
        Assert.False(host.IsGrantPeer("controller",outgoing with{Grant=Guid.NewGuid().ToString("N")}));
        Assert.False(host.IsGrantPeer("controller",outgoing with{Scope=Guid.NewGuid().ToString("N")}));
        Assert.True(controller.IsGrantPeer("host",host.ClipboardPacket(new("ack",outgoing.Clipboard!.Id))!));
        Assert.Equal("move",controller.Outgoing(new("host · screen · track",0,.5,.5))!.Type);
        clock.Now=clock.Now.AddMinutes(5);Assert.Null(controller.ClipboardPacket(outgoing.Clipboard));Assert.False(host.IsGrantPeer("controller",outgoing));
        host.Sharing(false);Assert.False(host.IsGrantPeer("controller",outgoing));
    }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    [Fact]
    public void ConsentRequest_ExpiresAtThirtySeconds_AndDuplicateCannotExtendDeadline()
    {
        var clock = new Clock(); var policy = new RemoteControlPolicy(clock);
        policy.Sharing(true); policy.Hello("peer", new RemotePacket("hello"));
        Assert.True(policy.Request("peer"));
        var deadline = policy.RequestExpiresAt;
        clock.Now = clock.Now.AddSeconds(29);
        Assert.False(policy.Request("peer")); Assert.Equal(deadline, policy.RequestExpiresAt);
        Assert.Null(policy.ExpireRequest()); Assert.Equal("peer", policy.RequestedBy);
        clock.Now = clock.Now.AddSeconds(1);
        Assert.Equal("peer", policy.ExpireRequest()); Assert.Null(policy.RequestedBy);
        Assert.Null(policy.RequestExpiresAt); Assert.Null(policy.ExpireRequest());
        Assert.Throws<InvalidOperationException>(() => policy.Approve("peer")); Assert.Null(policy.Controller);
    }
    [Fact]
    public void ConsentApproval_RejectsLateAnswerWithoutWaitingForExpiryCallback()
    {
        var clock = new Clock(); var policy = new RemoteControlPolicy(clock);
        policy.Sharing(true); policy.Hello("peer", new RemotePacket("hello")); policy.Request("peer");
        clock.Now = clock.Now.AddSeconds(30);
        Assert.Throws<InvalidOperationException>(() => policy.Approve("peer")); Assert.Null(policy.Controller);
        Assert.True(policy.Request("peer")); clock.Now = clock.Now.AddSeconds(29);
        Assert.Equal("grant", policy.Approve("peer").Type); Assert.Null(policy.ExpireRequest());
        Assert.Null(policy.RequestExpiresAt); Assert.Equal("peer", policy.Controller);
    }
    [Fact]
    public void BrowserWithoutNativeHandshake_CannotRequestOrReceiveControl()
    {
        var policy = new RemoteControlPolicy(); policy.Sharing(true);
        Assert.False(policy.Request("browser"));
        Assert.Throws<InvalidOperationException>(() => policy.Approve("browser"));
        Assert.Throws<InvalidOperationException>(() => policy.BeginRequest("browser"));
    }
    [Fact]
    public void InputRequiresExactSenderGrantShareSequenceAndLifetime()
    {
        var clock = new Clock(); var policy = new RemoteControlPolicy(clock); policy.Sharing(true);
        policy.Hello("native-peer", new RemotePacket("hello"));
        Assert.True(policy.Request("native-peer"));
        Assert.False(policy.Authorize("native-peer", new RemotePacket("input", Input: new RemoteInput("", 0, .5, .5))));
        var grant = policy.Approve("native-peer");
        var packet = new RemotePacket("input", Scope: grant.Scope, Grant: grant.Grant, Sequence: 1, Input: new RemoteInput("", 0, .5, .5));
        Assert.False(policy.Authorize("other-peer", packet));
        Assert.False(policy.Authorize("native-peer", packet with { Grant = Guid.NewGuid().ToString("N") }));
        Assert.True(policy.Authorize("native-peer", packet)); Assert.False(policy.Authorize("native-peer", packet));
        Assert.False(policy.Authorize("native-peer", packet with { Sequence = 2, Input = new RemoteInput("", 0, double.NaN, .5) }));
        clock.Now = clock.Now.AddMinutes(6); Assert.False(policy.Authorize("native-peer", packet with { Sequence = 2 }));
        policy.Sharing(false); Assert.Null(policy.Controller);
    }
    [Fact]
    public void NewShareAndDisconnectInvalidateOldConsent()
    {
        var policy = new RemoteControlPolicy(); policy.Sharing(true); policy.Hello("peer", new RemotePacket("hello")); policy.Request("peer");
        var grant = policy.Approve("peer"); policy.Sharing(true);
        Assert.False(policy.Authorize("peer", new RemotePacket("input", Scope: grant.Scope, Grant: grant.Grant, Sequence: 1, Input: new RemoteInput("", 0, .5, .5))));
        policy.Request("peer"); policy.Approve("peer"); policy.Remove("peer"); Assert.Null(policy.Controller);
    }
    [Fact]
    public void ControlGrantRequiresPendingRequestAndCurrentScreenCapability()
    {
        var policy = new RemoteControlPolicy(); var scope = Guid.NewGuid().ToString("N");
        policy.Hello("peer", new RemotePacket("hello", CanHost: true, Sharing: true, Scope: scope));
        var grant = new RemotePacket("grant", Scope: scope, Grant: Guid.NewGuid().ToString("N"));
        Assert.False(policy.AcceptGrant("peer", grant)); policy.BeginRequest("peer"); Assert.True(policy.AcceptGrant("peer", grant));
        policy.Hello("peer", new RemotePacket("hello", CanHost: true)); Assert.Null(policy.Controlling);
    }
    [Fact]
    public void ScreenMapping_RejectsLetterboxBars()
    {
        Assert.False(RemoteControlPolicy.TryMapScreenPoint(0, .5, 1024, 768, out _, out _));
        Assert.True(RemoteControlPolicy.TryMapScreenPoint(.5, .5, 1024, 768, out var x, out var y));
        Assert.Equal(.5, x); Assert.Equal(.5, y);
    }
    [Fact]
    public void InputRate_IsBoundedPerGrant()
    {
        var clock = new Clock(); var policy = new RemoteControlPolicy(clock);
        policy.Sharing(true); policy.Hello("peer", new RemotePacket("hello")); policy.Request("peer"); var grant = policy.Approve("peer");
        for (var sequence = 1; sequence <= 120; sequence++)
            Assert.True(policy.Authorize("peer", new RemotePacket("input", Scope: grant.Scope, Grant: grant.Grant, Sequence: sequence, Input: new RemoteInput("", 0, .5, .5))));
        var packet = new RemotePacket("input", Scope: grant.Scope, Grant: grant.Grant, Sequence: 121, Input: new RemoteInput("", 0, .5, .5));
        Assert.False(policy.Authorize("peer", packet));
        clock.Now = clock.Now.AddSeconds(1); Assert.True(policy.Authorize("peer", packet));
    }
}
