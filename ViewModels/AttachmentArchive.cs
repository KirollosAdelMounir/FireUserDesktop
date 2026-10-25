using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using UserIdentityDesktop.Core;
using System.Threading.Tasks;

namespace UserIdentityDesktop.ViewModels;

/// <summary>Archive verified plaintext locally; never execute downloaded files or overwrite a partial archive.</summary>
public static class AttachmentArchive
{
    private sealed class BatchProgress(ITransferOperation operation) : IProgress<long> { public void Report(long value) => operation.Report(value); }
    public static async Task SaveAsync(IEnumerable<ChatAttachmentItemViewModel> attachments, string destination)
    {
        var items = attachments.ToArray();
        long? total = 0;
        foreach (var item in items) { if (item.TransferSize is not {} size) { total=null; break; } try { total=checked(total+size); } catch (OverflowException) { total=null; break; } }
        using var transfer = items.FirstOrDefault()?.Transfers?.Start(TransferDirection.Download, items.Length, total);
        var cancellationToken=transfer?.CancellationToken??default;
        var progress = transfer == null ? null : new BatchProgress(transfer);
        var staging = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!, ".fireuser-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var output = new FileStream(staging, options))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var attachment in items)
                {
                    transfer?.SetPhase("Downloading attachments…");
                    var source = await attachment.PreparePreviewAsync(cancellationToken,progress);
                    try
                    {
                        var original = Path.GetFileName(attachment.FileName.Replace('\\', '/'));
                        if (string.IsNullOrWhiteSpace(original) || original is "." or "..") original = "Attachment";
                        var name = original; var suffix = 1;
                        while (!names.Add(name)) name = Path.GetFileNameWithoutExtension(original) + " (" + suffix++ + ")" + Path.GetExtension(original);
                        var entry = archive.CreateEntry(name);
                        await using var input = File.OpenRead(source); await using var zipped = entry.Open();
                        await input.CopyToAsync(zipped,cancellationToken);
                        transfer?.FileCompleted();
                    }
                    finally { File.Delete(source); }
                }
            }
            transfer?.SetPhase("Finalizing ZIP…");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staging, destination, overwrite: true);
            transfer?.Complete(true);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }
}
