using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UserIdentityDesktop.Core.Models;

namespace UserIdentityDesktop.Service.Media;

internal static class ScreenSources
{
    internal static bool IsWayland => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) || string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase);
    public static async Task<IReadOnlyList<ScreenShareSource>> DiscoverAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsLinux() && IsWayland)
        {
            await Task.Run(PipeWireCapture.CheckRuntime, ct);
            uint available;
            try { available = await ScreenCastPortal.AvailableSourcesAsync(ct); }
            catch (OperationCanceledException) { throw; }
            catch { throw new InvalidOperationException("The desktop ScreenCast portal is unavailable. Enable xdg-desktop-portal and your compositor's backend (Hyprland, KDE, GNOME or wlroots) in this desktop session."); }
            var sources = new List<ScreenShareSource>();
            if ((available & 1) != 0) sources.Add(new("portal-screen", "Choose a screen in the system sharing dialog", ScreenShareKind.Screen, ScreenCaptureBackend.Portal));
            if ((available & 2) != 0) sources.Add(new("portal-window", "Choose a window in the system sharing dialog", ScreenShareKind.Window, ScreenCaptureBackend.Portal));
            if (sources.Count == 0) throw new InvalidOperationException("This compositor's portal does not expose screen or window sharing.");
            return sources;
        }
        return await Task.Run(() => OperatingSystem.IsMacOS() ? MacScreenCapture.Sources() : OperatingSystem.IsWindows() ? Windows() : X11(), ct);
    }
    private static IReadOnlyList<ScreenShareSource> Windows()
    {
        var result = new List<ScreenShareSource>();
        EnumWindows((window, _) =>
        {
            if (result.Count >= 1024 || IsWindowVisible(window) == 0 || IsIconic(window) != 0) return true;
            var length = GetWindowTextLength(window); if (length is < 1 or > 4096) return true;
            var title = new StringBuilder(length + 1); GetWindowText(window, title, title.Capacity);
            if (title.Length > 0) result.Add(new(((nuint)window).ToString(), title.ToString(), ScreenShareKind.Window, ScreenCaptureBackend.Windows));
            return true;
        }, IntPtr.Zero);
        return result;
    }
    private static IReadOnlyList<ScreenShareSource> X11()
    {
        var result = new List<ScreenShareSource>(); var display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero) throw new InvalidOperationException("No X11 desktop display is available.");
        try
        {
            var root = XDefaultRootWindow(display); var atom = XInternAtom(display, "_NET_CLIENT_LIST", 1);
            if (atom == 0) return result;
            if (XGetWindowProperty(display, root, atom, 0, 1024, 0, 0, out _, out var format, out var count, out _, out var data) != 0) return result;
            try
            {
                if (format != 32 || data == IntPtr.Zero) return result;
                for (var i = 0; i < (int)Math.Min(count, 1024); i++)
                {
                    var id = (nuint)Marshal.ReadIntPtr(data, i * IntPtr.Size);
                    var nameAtom = XInternAtom(display, "_NET_WM_NAME", 1);
                    if (XGetWindowProperty(display, id, nameAtom, 0, 1024, 0, 0, out _, out var nameFormat, out var nameCount, out _, out var name) != 0) continue;
                    try { if (name != IntPtr.Zero && nameFormat == 8 && nameCount > 0) result.Add(new(id.ToString(), Marshal.PtrToStringUTF8(name, (int)Math.Min(nameCount, 4096)) ?? "Window", ScreenShareKind.Window, ScreenCaptureBackend.X11)); }
                    finally { if (name != IntPtr.Zero) XFree(name); }
                }
            }
            finally { if (data != IntPtr.Zero) XFree(data); }
        }
        finally { XCloseDisplay(display); }
        return result;
    }
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);
    [DllImport("user32.dll")] private static extern int IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern int IsIconic(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")] private static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")] private static extern nuint XDefaultRootWindow(IntPtr display);
    [DllImport("libX11.so.6")] private static extern nuint XInternAtom(IntPtr display, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int onlyIfExists);
    [DllImport("libX11.so.6")] private static extern int XGetWindowProperty(IntPtr display, nuint window, nuint property, nint offset, nint length, int delete, nuint type, out nuint actualType, out int format, out nuint items, out nuint remaining, out IntPtr value);
    [DllImport("libX11.so.6")] private static extern int XFree(IntPtr value);
}
