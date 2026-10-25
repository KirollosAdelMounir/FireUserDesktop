using System.Text.Json;
using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.Service.Media;
using UserIdentityDesktop.Views;
using Xunit;
namespace UserIdentityDesktop.Tests;
public sealed class FocusedInputTests
{
    [Fact]
    public async Task PointerFloodKeepsLatestPositionAndDoesNotDelayReliableKeys()
    {
        var inbox=new RemoteInputInbox();
        for(var i=1;i<=10000;i++)Assert.True(inbox.TryWrite(("peer",new("move",Sequence:i))));
        Assert.True(inbox.TryWrite(("peer",new("input",Sequence:1,Input:new("",2,Code:4,Down:true)))));
        Assert.True(inbox.TryWrite(("peer",new("release",Sequence:2,Input:new("",3)))));
        await using var reader=inbox.ReadAllAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());Assert.Equal("input",reader.Current.Packet.Type);
        Assert.True(await reader.MoveNextAsync());Assert.Equal("release",reader.Current.Packet.Type);
        Assert.True(await reader.MoveNextAsync());Assert.Equal(10000,reader.Current.Packet.Sequence);
    }
    [Theory]
    [InlineData(27,41)][InlineData(122,68)][InlineData(123,69)][InlineData(124,104)][InlineData(135,115)]
    [InlineData(36,74)][InlineData(35,77)][InlineData(33,75)][InlineData(34,78)][InlineData(96,98)][InlineData(105,97)]
    public void SystemKeysRetainTheirRemoteUsage(int vk,int usage)=>Assert.Equal(usage,RemoteKeyboardCapture.MapKey(vk));
    [Fact]
    public void LocalEpochIsNotDisclosedOnWireAndChangesForEveryGrant()
    {
        var policy=new RemoteControlPolicy();var scope=Guid.NewGuid().ToString("N");
        policy.Hello("host",new("hello",CanHost:true,Sharing:true,Scope:scope,ExtendedInput:true));
        Assert.True(policy.Peers["host"].ExtendedInput);
        policy.BeginRequest("host");Assert.True(policy.AcceptGrant("host",new("grant",Scope:scope,Grant:Guid.NewGuid().ToString("N"))));
        var first=policy.InputEpoch;Assert.True(first>0);
        policy.StopControlling();Assert.Equal(0,policy.InputEpoch);
        policy.BeginRequest("host");Assert.True(policy.AcceptGrant("host",new("grant",Scope:scope,Grant:Guid.NewGuid().ToString("N"))));
        Assert.True(policy.InputEpoch>first);
        Assert.DoesNotContain("ControlEpoch",JsonSerializer.Serialize(new RemoteInput("",2,ControlEpoch:first)),StringComparison.OrdinalIgnoreCase);
    }
    [Theory]
    [InlineData(6,0,false,1,-1,true)][InlineData(6,0,false,10,10,true)]
    [InlineData(6,0,false,11,0,false)][InlineData(6,0,false,double.NaN,0,false)]
    [InlineData(6,0,true,0,1,false)][InlineData(6,1,false,0,1,false)]
    [InlineData(2,74,true,0,0,true)][InlineData(2,102,true,0,0,false)]
    [InlineData(1,5,true,0,0,true)][InlineData(1,6,true,0,0,false)][InlineData(0,0,false,1,0,false)]
    public void HostedInputRejectsInvalidScrollAndKeyCodes(int kind,int code,bool down,double dx,double dy,bool accepted)
    {
        var policy=new RemoteControlPolicy();policy.Sharing(true);policy.Hello("peer",new("hello",ExtendedInput:true));policy.Request("peer");var grant=policy.Approve("peer");
        var packet=new RemotePacket("input",Scope:grant.Scope,Grant:grant.Grant,Sequence:1,Input:new("",kind,.5,.5,code,down,DeltaX:dx,DeltaY:dy));
        Assert.Equal(accepted,policy.Authorize("peer",packet));
        if(accepted)Assert.False(policy.Authorize("peer",packet));
    }
    [Fact]
    public void ExtensionRequirementPreservesLegacyPeers()
    {
        Assert.False(RemoteInputCodes.RequiresExtension(new("",2,Code:4)));
        Assert.True(RemoteInputCodes.RequiresExtension(new("",6,DeltaY:1)));
        Assert.True(RemoteInputCodes.RequiresExtension(new("",1,Code:4)));
        Assert.True(RemoteInputCodes.RequiresExtension(new("",2,Code:74)));
    }
}
