using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Threading;

namespace UserIdentityDesktop.Service;

[SupportedOSPlatform("macos")]
internal static class MacNotifications
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Activation(IntPtr sender);
    // Native notification center retains this callback for the application lifetime.
    private static readonly Activation callback = OnActivation;
    private static Action<string>? activate;
    [DllImport("fireuser_notifications", CallingConvention = CallingConvention.Cdecl)]
    private static extern void fu_notifications_initialize(Activation handler);
    [DllImport("fireuser_notifications", CallingConvention = CallingConvention.Cdecl)]
    private static extern int fu_notifications_show(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string title,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string message,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string sender);
    internal static void Initialize(Action<string> activated)
    {
        activate = activated;
        fu_notifications_initialize(callback);
    }
    internal static bool Show(string title, string message, string sender) => fu_notifications_show(title, message, sender) != 0;
    private static void OnActivation(IntPtr sender)
    {
        try
        {
            if (Marshal.PtrToStringUTF8(sender) is { } id)
                Dispatcher.UIThread.Post(() => activate?.Invoke(id));
        }
        catch { /* Managed exceptions must never escape a native callback. */ }
    }
}
