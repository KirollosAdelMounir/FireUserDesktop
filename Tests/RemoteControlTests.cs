using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.Service.Media;
using Xunit;

namespace UserIdentityDesktop.Tests;

public sealed class RemoteControlTests
{
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
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
