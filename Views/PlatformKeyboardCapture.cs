using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
namespace UserIdentityDesktop.Views;

/// <summary>Owns a native capture worker and keeps callbacks rooted until its join completes.</summary>
internal sealed class PlatformKeyboardCapture : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate void KeyCallback(long epoch,int hid,int down);
    private sealed class CaptureHandle:SafeHandleZeroOrMinusOneIsInvalid
    {
        private CaptureHandle():base(true){}
        internal KeyCallback? Callback;
        protected override bool ReleaseHandle(){PlatformKeyboardCapture.Close(handle);GC.KeepAlive(Callback);Callback=null;return true;}
    }
    [DllImport("fireuser_livekit",EntryPoint="fu_keyboard_capture_open",CallingConvention=CallingConvention.Cdecl)]private static extern CaptureHandle Open(nuint window,KeyCallback callback);
    [DllImport("fireuser_livekit",EntryPoint="fu_keyboard_capture_renew",CallingConvention=CallingConvention.Cdecl)]private static extern int Renew(CaptureHandle handle,int enabled,long epoch);
    [DllImport("fireuser_livekit",EntryPoint="fu_keyboard_capture_close",CallingConvention=CallingConvention.Cdecl)]private static extern void Close(IntPtr handle);
    private readonly CaptureHandle handle;
    private int disposed;
    internal Task Completion {get;private set;}=Task.CompletedTask;
    internal PlatformKeyboardCapture(IntPtr window,Action<long,int,bool> key)
    {
        KeyCallback callback=(epoch,hid,down)=>{try{key(epoch,hid,down!=0);}catch{}};
        handle=Open(unchecked((nuint)window),callback);handle.Callback=callback;
    }
    internal int Update(bool enabled,long epoch)=>Volatile.Read(ref disposed)!=0||handle.IsInvalid?-1:Renew(handle,enabled?1:0,epoch);
    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        if(!handle.IsInvalid)Renew(handle,0,0);
        Completion=Task.Run(handle.Dispose);
    }
}
