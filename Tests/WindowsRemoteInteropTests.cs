using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Headless.XUnit;
using UserIdentityDesktop.Views;
using Xunit;

namespace UserIdentityDesktop.Tests;

public sealed class WindowsRemoteInteropTests
{
    [DllImport("user32.dll")]private static extern int IsWindow(IntPtr handle);
    [Fact]
    public void WindowsKeyboardAndClipboardImports_ResolveActualExports()
    {
        if(!OperatingSystem.IsWindows()){Assert.Skip("Windows interop metadata");return;}
        foreach(var type in new[]{typeof(RemoteKeyboardCapture),typeof(WindowsClipboardChange)})
        foreach(var method in type.GetMethods(BindingFlags.NonPublic|BindingFlags.Static))
        {
            var import=method.GetCustomAttribute<DllImportAttribute>();if(import==null)continue;
            var module=NativeLibrary.Load(import.Value);
            try{Assert.True(NativeLibrary.TryGetExport(module,import.EntryPoint??method.Name,out _),method.Name);}
            finally{NativeLibrary.Free(module);}
        }
    }
    [AvaloniaFact]
    public void ClipboardNotificationWindow_IsPrivateAndDestroyedOnDispose()
    {
        if(!OperatingSystem.IsWindows()){Assert.Skip("Windows clipboard notification lifecycle");return;}
        using var change=new WindowsClipboardChange();var handle=change.Handle;
        Assert.NotEqual(IntPtr.Zero,handle);Assert.Equal(1,IsWindow(handle));
        change.Dispose();Assert.Equal(0,IsWindow(handle));Assert.True(change.Changed.Task.IsCompleted);
        change.Dispose();Assert.Equal(0,IsWindow(handle));
    }
}
