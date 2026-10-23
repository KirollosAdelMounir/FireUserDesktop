using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

using UserIdentityDesktop.Core;
namespace UserIdentityDesktop.Data;

public class FileService : IFileService
{
    private IStorageProvider? GetStorageProvider()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow?.StorageProvider;
        }
        return null;
    }

    public async Task<IReadOnlyList<string>> PickFilesAsync(string title = "Select files", bool allowMultiple = true)
    {
        var sp = GetStorageProvider();
        if (sp == null)
            return Array.Empty<string>();

        var options = new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = allowMultiple,
        };

        var files = await sp.OpenFilePickerAsync(options);
        return files.Select(f => f.Path.LocalPath).ToList();
    }

    public async Task<string?> PickSaveFileAsync(string defaultFileName, string? title = "Save file")
    {
        var sp = GetStorageProvider();
        if (sp == null)
            return null;

        var ext = Path.GetExtension(defaultFileName);
        var options = new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = defaultFileName,
            DefaultExtension = ext.TrimStart('.'),
        };

        var file = await sp.SaveFilePickerAsync(options);
        return file?.Path.LocalPath;
    }

    public void OpenFileWithDefaultApp(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("xdg-open", filePath);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", filePath);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to open file {filePath}: {ex.Message}");
        }
    }
}
