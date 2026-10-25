using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.ViewModels;

namespace UserIdentityDesktop.Views;

/// <summary>A dedicated message loop routes system keys without waiting for video rendering.</summary>
internal sealed class RemoteKeyboardCapture : IDisposable
{
    private sealed record Route(IntPtr Window,string Target,WorkspaceViewModel Model);
    private readonly CallWindow window;
    private readonly Hook callback;
    private readonly HashSet<int> held=new();
    private readonly object routeLock=new();
    private Route? route;
    private readonly Thread? worker;
    private readonly Channel<(Route Route,RemoteInput Input,bool Release)> keys=Channel.CreateBounded<(Route,RemoteInput,bool)>(64);
    private Task keySender=Task.CompletedTask;
    internal Task Completion=>keySender;
    private IntPtr hook;
    private uint threadId;
    private int disposed;
    private delegate IntPtr Hook(int code,IntPtr message,IntPtr data);
    [StructLayout(LayoutKind.Sequential)]private struct Keyboard{public uint Key,Scan,Flags,Time;public UIntPtr Extra;}
    [StructLayout(LayoutKind.Sequential)]private struct Message{public IntPtr Window;public uint Id;public UIntPtr WParam;public IntPtr LParam;public uint Time;public int X,Y;public uint Private;}
    [DllImport("user32.dll")]private static extern IntPtr SetWindowsHookExW(int id,Hook callback,IntPtr module,uint thread);
    [DllImport("user32.dll")]private static extern int UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]private static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
    [DllImport("user32.dll")]private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]private static extern int IsWindow(IntPtr window);
    [DllImport("user32.dll")]private static extern int GetMessageW(out Message message,IntPtr window,uint first,uint last);
    [DllImport("user32.dll")]private static extern int PeekMessageW(out Message message,IntPtr window,uint first,uint last,uint remove);
    [DllImport("user32.dll")]private static extern int PostThreadMessageW(uint thread,uint message,UIntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll")]private static extern int TranslateMessage(in Message message);
    [DllImport("user32.dll")]private static extern IntPtr DispatchMessageW(in Message message);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("kernel32.dll")]private static extern uint GetCurrentThreadId();
    internal RemoteKeyboardCapture(CallWindow window)
    {
        this.window=window;callback=Handle;
        window.Activated+=Activated;window.Deactivated+=Deactivated;window.GotFocus+=FocusChanged;window.LostFocus+=FocusChanged;
        if(!OperatingSystem.IsWindows())return;
        var handle=window.TryGetPlatformHandle()?.Handle??IntPtr.Zero;
        // Headless windows never install a hook or observe real keyboard activity.
        if(handle==IntPtr.Zero||IsWindow(handle)==0)return;
        keySender=Task.Run(DeliverKeys);
        worker=new Thread(Run){IsBackground=true,Name="Fire User remote keyboard"};worker.Start();Refresh();
    }
    private void Run()
    {
        try
        {
            threadId=GetCurrentThreadId();PeekMessageW(out _,IntPtr.Zero,0,0,0);
            if(Volatile.Read(ref disposed)!=0)return;
            hook=SetWindowsHookExW(13,callback,GetModuleHandleW(null),0);
            if(hook==IntPtr.Zero)return;
            while(GetMessageW(out var message,IntPtr.Zero,0,0)>0){TranslateMessage(in message);DispatchMessageW(in message);}
        }
        finally{if(hook!=IntPtr.Zero){UnhookWindowsHookEx(hook);hook=IntPtr.Zero;}GC.KeepAlive(callback);}
    }
    private void Activated(object? sender,EventArgs e)=>Refresh();
    private void Deactivated(object? sender,EventArgs e)=>SetRoute(null);
    private void FocusChanged(object? sender,RoutedEventArgs e)=>Refresh();
    internal void Refresh()
    {
        if(worker==null)return;
        var view=window.FocusManager?.GetFocusedElement() as NativeVideoView;
        var next=CanRoute(window.IsActive,true,view?.IsFocused==true,view?.RemoteControlEnabled==true)&&view?.Frame is {} frame&&window.DataContext is WorkspaceViewModel vm
            ?new Route(window.TryGetPlatformHandle()?.Handle??IntPtr.Zero,frame.Identity,vm):null;
        SetRoute(next);
    }
    private void SetRoute(Route? next)
    {
        lock(routeLock)
        {
            if(route==next)return;
            if(route is {} previous&&!Enqueue(previous,new(previous.Target,3),true))next=null;
            held.Clear();route=next;
        }
    }
    private bool Enqueue(Route target,RemoteInput input,bool release=false)
    {
        if(keys.Writer.TryWrite((target,input,release)))return true;
        target.Model.EmergencyRemoteStop();held.Clear();route=null;
        return false;
    }
    private async Task DeliverKeys()
    {
        try
        {
            await foreach(var key in keys.Reader.ReadAllAsync())
            {
                bool current;
                lock(routeLock)current=route==key.Route&&GetForegroundWindow()==key.Route.Window;
                if(current||key.Release)key.Route.Model.ForwardRemoteKeyboard(key.Input);
            }
        }
        catch{lock(routeLock)route?.Model.EmergencyRemoteStop();}
    }
    internal static bool CanRoute(bool active,bool foreground,bool focused,bool granted)=>active&&foreground&&focused&&granted;
    internal static int MapKey(int key)=>key is >=65 and <=90?4+key-65:key is >=49 and <=57?30+key-49:key is >=112 and <=123?58+key-112
        :key switch{48=>39,13=>40,27=>41,8=>42,9=>43,32=>44,189=>45,187=>46,219=>47,221=>48,220=>49,186=>51,222=>52,192=>53,188=>54,190=>55,191=>56,46=>76,39=>79,37=>80,40=>81,38=>82,17 or 162=>224,163=>228,16 or 160=>225,161=>229,164=>226,165=>230,91=>227,92=>231,_=>0};
    private IntPtr Handle(int code,IntPtr message,IntPtr data)
    {
        if(code<0)return CallNextHookEx(hook,code,message,data);
        try
        {
            lock(routeLock)
            {
                var current=route;
                if(current==null||GetForegroundWindow()!=current.Window){held.Clear();return CallNextHookEx(hook,code,message,data);}
                var keyboard=Marshal.PtrToStructure<Keyboard>(data);
                // An injected event must never be forwarded back into another control session.
                if((keyboard.Flags&0x10)!=0)return new IntPtr(1);
                var key=MapKey((int)keyboard.Key);if(key==0)return CallNextHookEx(hook,code,message,data);
                var down=message.ToInt64() is 0x100 or 0x104;
                var ctrl=held.Contains(224)||held.Contains(228);var alt=held.Contains(226)||held.Contains(230);
                if(down)held.Add(key);else held.Remove(key);
                if(ctrl&&alt&&key==41){held.Clear();current.Model.EmergencyRemoteStop();}
                else if(ctrl&&key is 6 or 25){if(down)Enqueue(current,new(current.Target,key==6?4:5));}
                else Enqueue(current,new(current.Target,2,Code:key,Down:down));
                return new IntPtr(1);
            }
        }
        catch{return CallNextHookEx(hook,code,message,data);}
    }
    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        SetRoute(null);window.Activated-=Activated;window.Deactivated-=Deactivated;window.GotFocus-=FocusChanged;window.LostFocus-=FocusChanged;
        keys.Writer.TryComplete();
        if(threadId!=0)PostThreadMessageW(threadId,0x12,UIntPtr.Zero,IntPtr.Zero);
        worker?.Join(200);GC.KeepAlive(callback);
    }
}
