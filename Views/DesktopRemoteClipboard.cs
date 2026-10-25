using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using UserIdentityDesktop.Core;

namespace UserIdentityDesktop.Views;

internal sealed class DesktopRemoteClipboard : IRemoteClipboard
{
    public async Task<RemoteClipboardContent> ReadAfterCopyAsync(Func<Task> copy,CancellationToken ct)
    {
        if(!OperatingSystem.IsWindows()){await copy();await Task.Delay(100,ct);return await ReadAsync(ct);}
        await Dispatcher.UIThread.InvokeAsync(async()=>
        {
            ct.ThrowIfCancellationRequested();
            using var change=new WindowsClipboardChange();
            await copy();await change.Changed.Task.WaitAsync(TimeSpan.FromSeconds(2),ct);
        });
        return await ReadAsync(ct);
    }
    private static Window MainWindow => (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow
        ?? throw new InvalidOperationException("Clipboard is unavailable in this desktop session.");
    public async Task<RemoteClipboardContent> ReadAsync(CancellationToken ct) => await Dispatcher.UIThread.InvokeAsync(async () =>
    {
        ct.ThrowIfCancellationRequested();
        var clipboard = MainWindow.Clipboard ?? throw new InvalidOperationException("The operating system clipboard is unavailable.");
        var files = await clipboard.TryGetFilesAsync();
        var paths = files?.Select(x => x.TryGetLocalPath()).Where(x => x != null).Cast<string>().ToArray() ?? Array.Empty<string>();
        return new RemoteClipboardContent(paths.Length == 0 ? await clipboard.TryGetTextAsync() : null, paths);
    });
    public async Task WriteAsync(RemoteClipboardContent content, CancellationToken ct) => await Dispatcher.UIThread.InvokeAsync(async () =>
    {
        ct.ThrowIfCancellationRequested();
        var window = MainWindow;
        var clipboard = window.Clipboard ?? throw new InvalidOperationException("The operating system clipboard is unavailable.");
        if (content.Paths.Count == 0) { await clipboard.SetTextAsync(content.Text ?? ""); return; }
        var files = new List<IStorageItem>();
        foreach (var path in content.Paths)
        {
            ct.ThrowIfCancellationRequested();
            IStorageItem? item = System.IO.Directory.Exists(path) ? await window.StorageProvider.TryGetFolderFromPathAsync(new Uri(path)) : await window.StorageProvider.TryGetFileFromPathAsync(new Uri(path));
            if (item == null) throw new InvalidOperationException("A received clipboard item is no longer available.");
            files.Add(item);
        }
        ct.ThrowIfCancellationRequested(); await clipboard.SetFilesAsync(files);
    });
}
