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
namespace UserIdentityDesktop.Service;

public class SystemNotificationService : INotificationService
{
    private string? _cachedIconPath;
    private string? _notificationSoundPath;
    private readonly object _notificationSoundLock = new();

    public event Action<Guid>? NotificationActivated;

    public SystemNotificationService()
    {
        Task.Run(EnsureIconExtracted);
    }

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
            var executablePath = Environment.ProcessPath;
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
            var executablePath = Environment.ProcessPath;
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

    [SupportedOSPlatform("osx")]
    public void InitializeMacRegistration()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
                throw new InvalidOperationException("The current executable path is unavailable.");

            var bundleRoot = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrWhiteSpace(bundleRoot))
            {
                var infoPlistPath = Path.Combine(bundleRoot, "..", "..", "Info.plist");
                var resolved = Path.GetFullPath(infoPlistPath);
                if (File.Exists(resolved))
                {
                    var content = File.ReadAllText(resolved);
                    if (!content.Contains("CFBundleURLSchemes", StringComparison.OrdinalIgnoreCase))
                    {
                        var xml = """
<key>CFBundleURLTypes</key>
<array>
    <dict>
        <key>CFBundleURLName</key>
        <string>Fire User Connect</string>
        <key>CFBundleURLSchemes</key>
        <array>
            <string>fireuserconnect</string>
        </array>
    </dict>
</array>
""";
                        var marker = "</dict>";
                        if (content.Contains(marker, StringComparison.Ordinal))
                        {
                            content = content.Replace(marker, xml + marker, StringComparison.Ordinal);
                            File.WriteAllText(resolved, content);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"macOS registration error: {ex}");
        }
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
                var uri = new Uri("avares://UserIdentityDesktop/Assets/PageLogo.png");
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

    public void ShowNotification(string title, string message, Guid senderId)
    {
        Task.Run(() =>
        {
            try
            {
                PlayNotificationSound();
                var cleanTitle = SanitizeText(title, 80);
                var cleanMessage = SanitizeText(message, 200);

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    ShowLinuxNotification(cleanTitle, cleanMessage, senderId);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    ShowWindowsNotification(cleanTitle, cleanMessage, senderId);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    ShowMacNotification(cleanTitle, cleanMessage);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to post OS notification: {ex.Message}");
            }
        });
    }

    private void PlayNotificationSound()
    {
        try
        {
            var soundPath = EnsureNotificationSoundExtracted();
            if (string.IsNullOrWhiteSpace(soundPath) || !File.Exists(soundPath))
                return;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                PlayWindowsNotificationSound(soundPath);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                StartAudioProcess("afplay", soundPath);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                if (!StartAudioProcess("ffplay", "-nodisp", "-autoexit", "-loglevel", "quiet", soundPath))
                    StartAudioProcess("mpg123", "-q", soundPath);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Notification sound error: {ex.Message}");
        }
    }

    private string? EnsureNotificationSoundExtracted()
    {
        if (!string.IsNullOrWhiteSpace(_notificationSoundPath) && File.Exists(_notificationSoundPath))
            return _notificationSoundPath;

        lock (_notificationSoundLock)
        {
            if (!string.IsNullOrWhiteSpace(_notificationSoundPath) && File.Exists(_notificationSoundPath))
                return _notificationSoundPath;

            var appDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "UserIdentityDesktop");
            Directory.CreateDirectory(appDirectory);

            var soundPath = Path.Combine(appDirectory, "NotificationSound.mp3");
            var assetUri = new Uri("avares://UserIdentityDesktop/Assets/NotificationSound.mp3");
            if (!AssetLoader.Exists(assetUri))
                return null;

            using var source = AssetLoader.Open(assetUri);
            using var destination = File.Create(soundPath);
            source.CopyTo(destination);
            _notificationSoundPath = soundPath;
            return soundPath;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void PlayWindowsNotificationSound(string soundPath)
    {
        var safePath = soundPath.Replace("'", "''", StringComparison.Ordinal);
        var script = $"Add-Type -AssemblyName PresentationCore; $player = New-Object System.Windows.Media.MediaPlayer; $player.Open([Uri]::new('{safePath}')); $player.Play(); Start-Sleep -Seconds 3; $player.Close();";
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        StartAudioProcess("powershell.exe", "-NoProfile", "-NonInteractive", "-EncodedCommand", encodedCommand);
    }

    private static bool StartAudioProcess(string fileName, params string[] arguments)
    {
        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var argument in arguments)
                processInfo.ArgumentList.Add(argument);

            return Process.Start(processInfo) != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void ShowLinuxNotification(string title, string message, Guid senderId)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "notify-send",
                CreateNoWindow = true,
                UseShellExecute = false
            };

            psi.ArgumentList.Add("-a");
            psi.ArgumentList.Add("Fire User Connect");
            psi.ArgumentList.Add("-u");
            psi.ArgumentList.Add("normal");
            psi.ArgumentList.Add("-A");
            psi.ArgumentList.Add("default=Open");
            psi.RedirectStandardOutput = true;

            if (!string.IsNullOrEmpty(_cachedIconPath) && File.Exists(_cachedIconPath))
            {
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(_cachedIconPath);
            }

            psi.ArgumentList.Add(title);
            psi.ArgumentList.Add(message);

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                proc.WaitForExit(3000);
                if (proc.StandardOutput.ReadToEnd().Trim() == "default")
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => NotificationActivated?.Invoke(senderId));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Linux notification error: {ex.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    private void ShowWindowsNotification(string title, string message, Guid senderId)
    {
        try
        {
            var safeTitle = title.Replace("'", "''");
            var safeMessage = message.Replace("'", "''");
            var launchUri = $"fireuserconnect://chat/{senderId}";

            var iconXml = "";
            if (!string.IsNullOrEmpty(_cachedIconPath) && File.Exists(_cachedIconPath))
            {
                var safeIconPath = _cachedIconPath.Replace("'", "''");
                iconXml = $"<image placement='appLogoOverride' src='file:///{safeIconPath}'/>";
            }

            var script = $@"
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null
$title = '{safeTitle}'
$msg = '{safeMessage}'
$xmlTemplate = @""
<toast launch='{launchUri}'>
    <visual>
        <binding template='ToastGeneric'>
            <text>$title</text>
            <text>$msg</text>
            {iconXml}
        </binding>
    </visual>
</toast>
""@
$xml = New-Object Windows.Data.Xml.Dom.XmlDocument
$xml.LoadXml($xmlTemplate)
$toast = [Windows.UI.Notifications.ToastNotification]::new($xml)
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
                proc.WaitForExit(4000);
                var error = proc.StandardError.ReadToEnd().Trim();
                if (!string.IsNullOrEmpty(error))
                    Console.WriteLine($"Windows notification PowerShell error: {error}");
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

        try
        {
            var link = (IShellLinkW)new ShellLink();
            try
            {
                link.SetPath(executablePath);
                link.SetWorkingDirectory(Path.GetDirectoryName(executablePath) ?? string.Empty);
                link.SetDescription("Fire User Connect");
                link.SetIconLocation(executablePath, 0);

                var propertyStore = (IPropertyStore)link;
                try
                {
                    var propertyKey = new PropertyKey(AppUserModelIdFormatId, 5);
                    using var appUserModelId = new PropVariant(AppUserModelId);
                    var appUserModelIdValue = appUserModelId.Value;
                    propertyStore.SetValue(ref propertyKey, ref appUserModelIdValue);
                    propertyStore.Commit();
                }
                finally
                {
                    Marshal.ReleaseComObject(propertyStore);
                }

                ((IPersistFile)link).Save(shortcutPath, true);
            }
            finally
            {
                Marshal.ReleaseComObject(link);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Windows notification shortcut error: {ex}");
        }
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

    private void ShowMacNotification(string title, string message)
    {
        try
        {
            var safeTitle = title.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var safeMessage = message.Replace("\\", "\\\\").Replace("\"", "\\\"");

            var script = $"display notification \"{safeMessage}\" with title \"Fire User Connect\" subtitle \"{safeTitle}\" sound name \"default\"";

            var psi = new ProcessStartInfo
            {
                FileName = "osascript",
                CreateNoWindow = true,
                UseShellExecute = false
            };

            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(script);

            using var proc = Process.Start(psi);
            proc?.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"macOS notification error: {ex.Message}");
        }
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
