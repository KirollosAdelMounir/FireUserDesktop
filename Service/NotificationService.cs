using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.Versioning;
using Avalonia.Platform;
using Microsoft.Win32;

using UserIdentityDesktop.Core;
using UserIdentityDesktop.Core.Models;
namespace UserIdentityDesktop.Service;

public class SystemNotificationService : INotificationService
{
    private string? _cachedIconPath;
    private readonly Media.CallSounds notificationSound = new();
    private readonly LinuxNotifications linuxNotifications = new();

    public event Action<Guid>? NotificationActivated;
    public event Action<DesktopNotification>? NotificationPosted;
    public event Action<string>? NotificationLinkActivated;
    public void ActivateLink(string target) => NotificationLinkActivated?.Invoke(target);
    public Action<string, string, string>? ShowDesktopToast { get; set; }
    // Called on Avalonia's UI thread. Return true when a foreground toast was shown.
    public Func<string, string, Guid, bool>? TryShowForeground { get; set; }
    private readonly System.Threading.SemaphoreSlim osNotificationGate = new(1, 1);
    public void Activate(Guid senderId) => NotificationActivated?.Invoke(senderId);

    [SupportedOSPlatform("windows")]
    public void InitializeWindowsRegistration()
    {
        try
        {
            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath))
                throw new InvalidOperationException("The current executable path is unavailable.");

            RegisterWindowsNotificationProtocol(executablePath);
            CreateWindowsToastShortcut(executablePath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Windows notification registration error: {ex}");
        }
    }

    [SupportedOSPlatform("linux")]
    public void InitializeLinuxRegistration()
    {
        try
        {
            var executablePath = LinuxLauncherPath();
            if (string.IsNullOrWhiteSpace(executablePath))
                throw new InvalidOperationException("The current executable path is unavailable.");

            var applicationsDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", "applications");
            Directory.CreateDirectory(applicationsDirectory);

            var desktopFilePath = Path.Combine(applicationsDirectory, "fire-user-connect.desktop");
            var iconPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "UserIdentityDesktop", "app_icon.png");

            var desktopEntries = File.Exists(desktopFilePath)
                ? new List<string>(File.ReadAllLines(desktopFilePath))
                : new List<string> { "[Desktop Entry]" };

            SetDesktopEntry(desktopEntries, "Type", "Application");
            SetDesktopEntry(desktopEntries, "Name", "Fire User Connect");
            SetDesktopEntry(desktopEntries, "Comment", "Real-time chatting for User Identity");
            SetDesktopEntry(desktopEntries, "Exec", $"{EscapeDesktopExecPath(executablePath)} --open-chat %u");
            SetDesktopEntry(desktopEntries, "TryExec", executablePath);
            SetDesktopEntry(desktopEntries, "Icon", File.Exists(iconPath) ? iconPath : executablePath);
            SetDesktopEntry(desktopEntries, "Terminal", "false");
            SetDesktopEntry(desktopEntries, "Categories", "Network;Chat;");
            SetDesktopEntry(desktopEntries, "MimeType", "x-scheme-handler/fireuserconnect;");

            File.WriteAllLines(desktopFilePath, desktopEntries);
            File.SetUnixFileMode(desktopFilePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            RegisterLinuxSchemeHandler(desktopFilePath);
            Console.WriteLine($"Linux application registration ready: {desktopFilePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Linux notification registration error: {ex}");
        }
    }

    [SupportedOSPlatform("linux")]
    public void InitializeLinuxAutostart()
    {
        try
        {
            var executablePath = LinuxLauncherPath();
            if (string.IsNullOrWhiteSpace(executablePath))
                throw new InvalidOperationException("The current executable path is unavailable.");

            var autostartDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "autostart");
            Directory.CreateDirectory(autostartDirectory);

            var desktopFilePath = Path.Combine(autostartDirectory, "fire-user-connect-autostart.desktop");
            var entries = new List<string>
            {
                "[Desktop Entry]",
                "Type=Application",
                "Name=Fire User Connect",
                "Exec=" + EscapeDesktopExecPath(executablePath),
                "Terminal=false",
                "X-GNOME-Autostart-enabled=true"
            };

            File.WriteAllLines(desktopFilePath, entries);
            File.SetUnixFileMode(desktopFilePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            Console.WriteLine($"Linux autostart entry ready: {desktopFilePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Linux autostart registration error: {ex}");
        }
    }

    [SupportedOSPlatform("macos")]
    public void InitializeMacRegistration()
    {
        try { MacNotifications.Initialize(ActivateLink); }
        catch (Exception ex) { Console.WriteLine($"macOS notification registration: {ex.GetType().Name}"); }
    }

    private static void SetDesktopEntry(List<string> entries, string key, string value)
    {
        var prefix = key + "=";
        var index = entries.FindIndex(line => line.StartsWith(prefix, StringComparison.Ordinal));
        var entry = prefix + value;

        if (index >= 0)
            entries[index] = entry;
        else
            entries.Add(entry);
    }

    private static string? LinuxLauncherPath() => ResolveLinuxLauncherPath(Environment.GetEnvironmentVariable("APPIMAGE"), Environment.ProcessPath);

    internal static string? ResolveLinuxLauncherPath(string? appImage, string? processPath) =>
        !string.IsNullOrWhiteSpace(appImage) && Path.IsPathRooted(appImage) && File.Exists(appImage)
            ? appImage : processPath;

    private static string EscapeDesktopExecPath(string path)
    {
        return $"\"{path.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }

    private static void RegisterLinuxSchemeHandler(string desktopFilePath)
    {
        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = "xdg-mime",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            processInfo.ArgumentList.Add("default");
            processInfo.ArgumentList.Add(Path.GetFileName(desktopFilePath));
            processInfo.ArgumentList.Add("x-scheme-handler/fireuserconnect");

            using var process = Process.Start(processInfo);
            process?.WaitForExit(3000);
            if (process?.ExitCode != 0)
                Console.WriteLine($"Linux URI handler registration exited with code {process?.ExitCode}.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Linux URI handler registration warning: {ex.Message}");
        }
    }

    private void EnsureIconExtracted()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var appDir = Path.Combine(appData, "UserIdentityDesktop");
            Directory.CreateDirectory(appDir);
            var iconPath = Path.Combine(appDir, "app_icon.png");

            if (!File.Exists(iconPath))
            {
                var uri = new Uri("avares://Fire%20User%20Connect/Assets/PageLogo.png");
                if (AssetLoader.Exists(uri))
                {
                    using var stream = AssetLoader.Open(uri);
                    using var fileStream = File.Create(iconPath);
                    stream.CopyTo(fileStream);
                }
            }

            if (File.Exists(iconPath))
            {
                _cachedIconPath = iconPath;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not extract notification icon: {ex.Message}");
        }
    }
    public void ShowNotification(string title, string message, Guid senderId) => ShowNotification(title, message, $"fireuserconnect://chat/{senderId}");
    public void ShowNotification(string title, string message, string target)
    {
        notificationSound.Play("NotificationSound", loop: false);
        var cleanTitle = SanitizeText(title, 80);
        var cleanMessage = SanitizeText(message, 200);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            NotificationPosted?.Invoke(new(cleanTitle, cleanMessage, target, DateTime.UtcNow));
            if (ShowDesktopToast != null) ShowDesktopToast(cleanTitle, cleanMessage, target);
            else if (Guid.TryParse(target.Split('/')[^1], out var legacy) && TryShowForeground?.Invoke(cleanTitle, cleanMessage, legacy) == true) return;
            _ = ArchiveAfterToastAsync(cleanTitle, cleanMessage, target);
        });
    }
    private async Task ArchiveAfterToastAsync(string title, string message, string target)
    {
        await Task.Delay(TimeSpan.FromSeconds(6));
        await PostSystemAsync(title, message, target);
    }
    private async Task PostSystemAsync(string cleanTitle, string cleanMessage, string senderId)
    {
        // Avoid spawning unbounded shell/audio processes during message bursts.
        await osNotificationGate.WaitAsync();
        try
        {
            if (OperatingSystem.IsLinux())
            {
                EnsureIconExtracted();
                await linuxNotifications.ShowAsync(cleanTitle, cleanMessage, senderId, _cachedIconPath, ActivateLink);
                return;
            }
            await Task.Run(() =>
            {
                EnsureIconExtracted();

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    ShowWindowsNotification(cleanTitle, cleanMessage, senderId);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    ShowMacNotification(cleanTitle, cleanMessage, senderId);
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to post OS notification: {ex.GetType().Name}");
        }
        finally { osNotificationGate.Release(); }
    }
    [SupportedOSPlatform("windows")]
    private void ShowWindowsNotification(string title, string message, string senderId)
    {
        try
        {
            var launchUri = System.Security.SecurityElement.Escape(senderId);
            var iconXml = string.IsNullOrEmpty(_cachedIconPath) || !File.Exists(_cachedIconPath) ? "" :
                $"<image placement='appLogoOverride' src='{System.Security.SecurityElement.Escape(new Uri(_cachedIconPath).AbsoluteUri)}'/>";
            var xmlText = $"<toast activationType='protocol' launch='{launchUri}'><visual><binding template='ToastGeneric'><text>{System.Security.SecurityElement.Escape(title)}</text><text>{System.Security.SecurityElement.Escape(message)}</text>{iconXml}</binding></visual><audio silent='true'/></toast>";
            // Only Base64 crosses the script boundary; notification text is XML data.
            var xmlPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes(xmlText));
            var script = $@"
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null
$xml = New-Object Windows.Data.Xml.Dom.XmlDocument
$xml.LoadXml([System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{xmlPayload}')))
$toast = [Windows.UI.Notifications.ToastNotification]::new($xml)
$toast.SuppressPopup = $true
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('UserIdentityDesktop.Desktop').Show($toast)
";
            var bytes = Encoding.Unicode.GetBytes(script);
            var base64 = Convert.ToBase64String(bytes);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {base64}",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                if (!proc.WaitForExit(4000)) { proc.Kill(entireProcessTree: true); return; }
                if (proc.ExitCode != 0)
                    Console.WriteLine($"Windows notification exited with code {proc.ExitCode}.");
            }

        }
        catch (Exception ex)
        {
            Console.WriteLine($"Windows notification error: {ex.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RegisterWindowsNotificationProtocol(string executablePath)
    {
        try
        {
            using var protocolKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\fireuserconnect");
            protocolKey?.SetValue(null, "URL:Fire User Connect");
            protocolKey?.SetValue("URL Protocol", string.Empty);
            using var commandKey = protocolKey?.CreateSubKey(@"shell\open\command");
            commandKey?.SetValue(null, $"\"{executablePath}\" --open-chat \"%1\"");

        }
        catch (Exception ex)
        {
            Console.WriteLine($"Windows notification protocol registration error: {ex.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void CreateWindowsToastShortcut(string executablePath)
    {
        var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
        Directory.CreateDirectory(startMenu);
        var shortcutPath = Path.Combine(startMenu, "Fire User Connect.lnk");

        try { WriteWindowsToastShortcut(executablePath, shortcutPath); }
        catch (Exception ex) { Console.WriteLine($"Windows notification shortcut error: {ex}"); }
    }

    [SupportedOSPlatform("windows")]
    internal static void WriteWindowsToastShortcut(string executablePath, string shortcutPath)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(executablePath);
            link.SetWorkingDirectory(Path.GetDirectoryName(executablePath) ?? string.Empty);
            link.SetDescription("Fire User Connect");
            link.SetIconLocation(executablePath, 0);

            // All three interfaces share one RCW. Releasing an interface alias here
            // disconnects the link before IPersistFile.Save can use it.
            var propertyStore = (IPropertyStore)link;
            var propertyKey = new PropertyKey(AppUserModelIdFormatId, 5);
            using var appUserModelId = new PropVariant(AppUserModelId);
            var appUserModelIdValue = appUserModelId.Value;
            propertyStore.SetValue(ref propertyKey, ref appUserModelIdValue);
            propertyStore.Commit();
            ((IPersistFile)link).Save(shortcutPath, true);
        }
        finally { Marshal.ReleaseComObject(link); }
    }

    private const string AppUserModelId = "UserIdentityDesktop.Desktop";
    private static readonly Guid AppUserModelIdFormatId = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath(StringBuilder path, int maxPath, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription(StringBuilder description, int maxDescription);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory(StringBuilder directory, int maxDirectory);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments(StringBuilder arguments, int maxArguments);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation(StringBuilder iconPath, int maxPath, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr windowHandle, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010B-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;

        public PropertyKey(Guid formatId, uint propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant : IDisposable
    {
        [FieldOffset(0)] private ushort _variantType;
        [FieldOffset(8)] private IntPtr _value;

        public PropVariant(string value)
        {
            _variantType = 31;
            _value = Marshal.StringToCoTaskMemUni(value);
        }

        public PropVariant Value => this;

        public void Dispose()
        {
            if (_value != IntPtr.Zero)
                Marshal.FreeCoTaskMem(_value);
        }
    }

    [SupportedOSPlatform("macos")]
    private static void ShowMacNotification(string title, string message, string senderId)
    {
        if (!MacNotifications.Show(title, message, senderId))
            Console.WriteLine("macOS notifications require the packaged Fire User Connect.app bundle.");
    }

    private static string SanitizeText(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var trimmed = text.Trim();
        if (trimmed.Length > maxLength)
            trimmed = trimmed.Substring(0, maxLength) + "...";

        var sb = new StringBuilder(trimmed.Length);
        foreach (var c in trimmed)
        {
            if (!char.IsControl(c) || c == '\n' || c == '\r' || c == '\t')
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
