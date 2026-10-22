using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using UserIdentityDesktop.ViewModels;

namespace UserIdentityDesktop.Views;

internal static class SubmissionMessage
{
    public static async Task ShowAsync(Window? owner, string message)
    {
        if (OperatingSystem.IsWindows())
        {
            var handle = owner?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            await Task.Run(() => MessageBoxW(handle, message, "Fire User Connect", 0x40)); return;
        }
        if (OperatingSystem.IsMacOS())
        {
            if (await Run("/usr/bin/osascript", new[] { "-e", "on run argv", "-e", "display dialog (item 1 of argv) with title \"Fire User Connect\" buttons {\"OK\"} default button \"OK\" with icon note", "-e", "end run", message })) return;
        }
        if (OperatingSystem.IsLinux())
        {
            var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
            var zenity = paths.Select(path => Path.Combine(path, "zenity")).FirstOrDefault(File.Exists);
            if (zenity != null && await Run(zenity, new[] { "--info", "--no-markup", "--title=Fire User Connect", "--text=" + message })) return;
            var kdialog = paths.Select(path => Path.Combine(path, "kdialog")).FirstOrDefault(File.Exists);
            if (kdialog != null && await Run(kdialog, new[] { "--title", "Fire User Connect", "--msgbox", message })) return;
        }
        var dialog = new MessageDialog(new("Submission completed", "Fire User Connect", message, ""));
        if (owner != null) dialog.RequestedThemeVariant = owner.ActualThemeVariant;
        if (owner?.IsVisible == true) await dialog.ShowDialog(owner); else dialog.Show();
    }
    private static async Task<bool> Run(string executable, string[] args)
    {
        try
        {
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var argument in args) info.ArgumentList.Add(argument);
            using var process = Process.Start(info); if (process == null) return false;
            var errors = process.StandardError.ReadToEndAsync(); var output = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync(); await Task.WhenAll(errors, output); return process.ExitCode == 0;
        }
        catch (Exception) { return false; }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
