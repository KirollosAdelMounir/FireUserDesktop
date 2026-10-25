using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using UserIdentityDesktop.Core;
using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.Data;

namespace UserIdentityDesktop.Service.Media;

public sealed partial class NativeMediaSession
{
    private readonly IRemoteClipboard? clipboardStore;
    private readonly ITransferTracker? transferTracker;
    private readonly Dictionary<string,RemoteClipboardTransfer> clipboardTransfers=new();
    private Task clipboardCleanup = Task.CompletedTask;
    public event Action<string>? ClipboardTransferChanged;
    private void ResetClipboard()
    {
        foreach(var previous in clipboardTransfers.Values)
        {
            previous.Dispose();
            clipboardCleanup=clipboardCleanup.IsCompleted?previous.Completion:Task.WhenAll(clipboardCleanup,previous.Completion);
        }
        clipboardTransfers.Clear();
    }
    private RemoteClipboardTransfer? ClipboardSession(string peer,RemotePacket binding)
    {
        if(clipboardStore==null||lifetime==null||!control.IsGrantPeer(peer,binding))return null;
        var key=peer+":"+binding.Grant;
        if(clipboardTransfers.TryGetValue(key,out var previous))
        {
            if(!previous.Reader.IsCompleted)return previous;
            previous.Dispose();clipboardCleanup=clipboardCleanup.IsCompleted?previous.Completion:Task.WhenAll(clipboardCleanup,previous.Completion);
        }
        var host=control.IsHostedGrantPeer(peer,binding);
        var clipboardTransfer=new RemoteClipboardTransfer(clipboardStore, message=>Task.Run(()=>
        {
            RemotePacket packet;IntPtr currentRoom;
            lock(remoteLock)
            {
                if(!Connected||!control.IsGrantPeer(peer,binding)||(packet=control.ClipboardPacket(message,peer,binding)!)==null)throw new OperationCanceledException();
                currentRoom=room;
            }
            var bytes=JsonSerializer.SerializeToUtf8Bytes(packet,ApiPayload.Json);
            LiveKitNative.Check(LiveKitNative.fu_send_transfer(currentRoom,peer,bytes,bytes.Length),"clipboard channel");
        }),()=>{lock(remoteLock)return Connected&&control.IsGrantPeer(peer,binding);},paste=>ApplyClipboardShortcutAsync(peer,binding,paste),message=>ClipboardTransferChanged?.Invoke(message),lifetime.Token,host,transferTracker);
        clipboardTransfers[key]=clipboardTransfer;
        return clipboardTransfer;
    }
    private async Task ApplyClipboardShortcutAsync(string peer,RemotePacket binding,bool paste)
    {
        ScreenCastPortal? portal;long epoch;
        lock(remoteLock)
        {
            if(!Connected||!canHost||!control.IsHostedGrantPeer(peer,binding))throw new OperationCanceledException();
            portal=waylandInput;epoch=waylandGrantEpoch;
            if(portal==null)
            {
                ReleaseHostedInput();
                var modifier=OperatingSystem.IsMacOS()?227:224;
                LiveKitNative.Check(LiveKitNative.fu_apply_input(2,0,0,modifier,1),"clipboard shortcut");
                try{LiveKitNative.Check(LiveKitNative.fu_apply_input(2,0,0,paste?25:6,1),"clipboard shortcut");}
                finally{LiveKitNative.fu_apply_input(2,0,0,paste?25:6,0);LiveKitNative.fu_apply_input(2,0,0,modifier,0);}
                return;
            }
        }
        await portal.ReleaseInputAsync();
        try
        {
            foreach(var input in new[]{new RemoteInput("",2,Code:224,Down:true),new RemoteInput("",2,Code:paste?25:6,Down:true),new RemoteInput("",2,Code:paste?25:6),new RemoteInput("",2,Code:224)})
            {
                lock(remoteLock)if(!Connected||!control.IsHostedGrantPeer(peer,binding)||!ReferenceEquals(portal,waylandInput))throw new OperationCanceledException();
                if(!await portal.ApplyInputAsync(input,epoch))throw new OperationCanceledException();
            }
        }
        finally{await portal.ReleaseInputAsync();}
    }
    private void ClipboardGesture(bool paste)
    {
        var peer=control.Controlling;
        if(peer==null)return;
        var binding=control.ControllerClipboardPacket(new("gesture",Guid.NewGuid().ToString("N")));
        if(binding==null){ClipboardTransferChanged?.Invoke("Update the other desktop client to use remote clipboard transfer.");return;}
        var transfer=ClipboardSession(peer,binding);
        if(transfer==null)return;
        if(paste)transfer.Paste();else transfer.Copy();
    }
}
