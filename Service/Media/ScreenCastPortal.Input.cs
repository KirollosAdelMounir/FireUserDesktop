using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;
using UserIdentityDesktop.Core.Models;
namespace UserIdentityDesktop.Service.Media;

internal sealed partial class ScreenCastPortal
{
    private const string RemoteInterface="org.freedesktop.portal.RemoteDesktop";
    private bool remoteDesktop,monitorSource;
    private readonly SemaphoreSlim inputGate=new(1,1);
    private readonly HashSet<int> heldKeys=[],heldButtons=[];
    private long nextGrant,activeGrant;
    public bool RemoteControlRequested=>remoteDesktop;
    public uint GrantedDevices {get;private set;}
    public int LogicalWidth {get;private set;}
    public int LogicalHeight {get;private set;}
    public bool CanControl=>remoteDesktop&&!Closed&&monitorSource&&(GrantedDevices&3)==3&&LogicalWidth>0&&LogicalHeight>0;
    public long EnableInput(){if(!CanControl)throw new InvalidOperationException("The desktop did not approve keyboard and pointer input for this screen.");var epoch=Interlocked.Increment(ref nextGrant);Volatile.Write(ref activeGrant,epoch);return epoch;}
    public void DisableInput(){Volatile.Write(ref activeGrant,0);_ = ReleaseInputAsync();}
    public async Task<bool> ApplyInputAsync(RemoteInput input,long epoch)
    {
        await inputGate.WaitAsync();
        try
        {
            if(!CanControl||epoch==0||epoch!=Volatile.Read(ref activeGrant))return false;
            if(!double.IsFinite(input.X)||!double.IsFinite(input.Y)||input.X<0||input.X>1||input.Y<0||input.Y>1)return false;
            if(input.Kind==3){await ReleaseHeldCoreAsync();return true;}
            if(input.Kind==0){await PointerAsync(input.X*(LogicalWidth-1),input.Y*(LogicalHeight-1));return true;}
            var code=input.Kind==1?input.Code switch{1=>0x110,2=>0x111,3=>0x112,_=>-1}:input.Kind==2?EvdevKey(input.Code):-1;
            if(code<0)return false;
            var held=input.Kind==1?heldButtons:heldKeys;
            if(!input.Down&&!held.Contains(code))return true;
            if(input.Down)held.Add(code);
            await NotifyAsync(input.Kind==1?"NotifyPointerButton":"NotifyKeyboardKeycode",code,input.Down?1u:0u);
            if(input.Down)held.Add(code);else held.Remove(code);
            return true;
        }
        catch {Volatile.Write(ref activeGrant,0);Interlocked.Exchange(ref closed,1);throw;}
        finally{inputGate.Release();}
    }
    public async Task ReleaseInputAsync()
    {
        await inputGate.WaitAsync();
        try{await ReleaseHeldCoreAsync();}
        catch{Volatile.Write(ref activeGrant,0);Interlocked.Exchange(ref closed,1);heldKeys.Clear();heldButtons.Clear();}
        finally{inputGate.Release();}
    }
    private async Task ReleaseHeldCoreAsync()
    {
        foreach(var code in heldKeys)await NotifyAsync("NotifyKeyboardKeycode",code,0);
        foreach(var code in heldButtons)await NotifyAsync("NotifyPointerButton",code,0);
        heldKeys.Clear();heldButtons.Clear();
    }
    private Task NotifyAsync(string method,int code,uint state)
    {
        if(session==null)throw new IOException("The remote desktop session ended.");
        using var writer=connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination:Service,path:Path,@interface:RemoteInterface,member:method,signature:"oa{sv}iu");
        writer.WriteObjectPath(session);writer.WriteDictionary(new Dictionary<string,VariantValue>());writer.WriteInt32(code);writer.WriteUInt32(state);
        return connection.CallMethodAsync(writer.CreateMessage()).WaitAsync(TimeSpan.FromSeconds(2));
    }
    private Task PointerAsync(double x,double y)
    {
        if(session==null)throw new IOException("The remote desktop session ended.");
        using var writer=connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination:Service,path:Path,@interface:RemoteInterface,member:"NotifyPointerMotionAbsolute",signature:"oa{sv}udd");
        writer.WriteObjectPath(session);writer.WriteDictionary(new Dictionary<string,VariantValue>());writer.WriteUInt32(Node);writer.WriteDouble(x);writer.WriteDouble(y);
        return connection.CallMethodAsync(writer.CreateMessage()).WaitAsync(TimeSpan.FromSeconds(2));
    }
    internal static int EvdevKey(int hid)
    {
        ReadOnlySpan<int> letters=[30,48,46,32,18,33,34,35,23,36,37,38,50,49,24,25,16,19,31,20,22,47,17,45,21,44];
        if(hid is >=4 and <=29)return letters[hid-4];
        if(hid is >=30 and <=39)return hid-28;
        if(hid is >=58 and <=67)return hid+1;
        return hid switch{40=>28,41=>1,42=>14,43=>15,44=>57,45=>12,46=>13,47=>26,48=>27,49=>43,51=>39,52=>40,53=>41,54=>51,55=>52,56=>53,68=>87,69=>88,76=>111,79=>106,80=>105,81=>108,82=>103,224=>29,225=>42,226=>56,227=>125,228=>97,229=>54,230=>100,231=>126,_=>-1};
    }
}
