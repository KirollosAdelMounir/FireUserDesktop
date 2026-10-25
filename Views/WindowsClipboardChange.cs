using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Threading;

namespace UserIdentityDesktop.Views;

/// <summary>Waits for the OS notification so remote copy cannot read a stale clipboard.</summary>
internal sealed class WindowsClipboardChange : IDisposable
{
    private readonly IntPtr window;
    private readonly Subclass callback;
    private readonly uint initial;
    private int disposed;
    internal IntPtr Handle=>window;
    internal readonly TaskCompletionSource Changed=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private delegate IntPtr Subclass(IntPtr window,uint message,IntPtr wParam,IntPtr lParam,UIntPtr id,UIntPtr data);
    private static readonly UIntPtr Id=new(0x46554350u);
    [DllImport("comctl32.dll")]private static extern int SetWindowSubclass(IntPtr window,Subclass callback,UIntPtr id,UIntPtr data);
    [DllImport("comctl32.dll")]private static extern int RemoveWindowSubclass(IntPtr window,Subclass callback,UIntPtr id);
    [DllImport("comctl32.dll")]private static extern IntPtr DefSubclassProc(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll")]private static extern int AddClipboardFormatListener(IntPtr window);
    [DllImport("user32.dll")]private static extern int RemoveClipboardFormatListener(IntPtr window);
    [DllImport("user32.dll")]private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr CreateWindowExW(uint style,string type,string name,uint windowStyle,int x,int y,int width,int height,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr parameter);
    [DllImport("user32.dll")]private static extern int DestroyWindow(IntPtr window);
    internal WindowsClipboardChange()
    {
        callback=OnMessage;initial=GetClipboardSequenceNumber();
        window=CreateWindowExW(0,"STATIC","",0,0,0,0,0,new IntPtr(-3),IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
        if(window==IntPtr.Zero)throw new InvalidOperationException("Cannot observe clipboard changes.");
        if(SetWindowSubclass(window,callback,Id,UIntPtr.Zero)==0){DestroyWindow(window);throw new InvalidOperationException("Cannot observe clipboard changes.");}
        if(AddClipboardFormatListener(window)==0){RemoveWindowSubclass(window,callback,Id);DestroyWindow(window);throw new InvalidOperationException("Cannot observe clipboard changes.");}
    }
    private IntPtr OnMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam,UIntPtr id,UIntPtr data)
    {
        try{if(message==0x31d&&GetClipboardSequenceNumber()!=initial)Changed.TrySetResult();}catch{}
        return DefSubclassProc(window,message,wParam,lParam);
    }
    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        RemoveClipboardFormatListener(window);RemoveWindowSubclass(window,callback,Id);DestroyWindow(window);Changed.TrySetCanceled();GC.KeepAlive(callback);
    }
}
