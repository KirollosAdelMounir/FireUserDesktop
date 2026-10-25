using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
namespace UserIdentityDesktop.Core;
public enum TransferDirection { Upload, Download }
public enum TransferPurpose { ChatAttachment, RemoteFile }
public sealed record TransferSnapshot(Guid Id, TransferDirection Direction, int TotalFiles, int CompletedFiles,
    long Bytes, long? TotalBytes, double BytesPerSecond, string Phase, bool Finished, bool Succeeded, DateTimeOffset? FinishedAt)
{
    public TransferPurpose Purpose { get; init; }
    public bool CancellationRequested { get; init; }
    public double Percentage => Succeeded ? 100 : TotalBytes is > 0 ? Math.Min(99, 100d * Bytes / TotalBytes.Value) : 0;
}
public interface ITransferOperation : IProgress<long>, IDisposable
{
    CancellationToken CancellationToken { get; }
    void Cancel();
    void SetTotal(long? bytes);
    void SetPhase(string phase);
    void FileCompleted();
    void Complete(bool success);
}
public interface ITransferTracker
{
    event Action? Changed;
    ITransferOperation Start(TransferDirection direction, int files, long? bytes = null, TransferPurpose purpose = TransferPurpose.ChatAttachment);
    void Cancel(Guid id);
    IReadOnlyList<TransferSnapshot> Snapshot();
    void Clear();
    void RemoveCompleted(Guid id);
}

/// <summary>Transfer duration is governed by cancellation and transport failure, never a whole-file deadline.</summary>
public static class AttachmentTransferRequests
{
    public static readonly HttpRequestOptionsKey<bool> UnlimitedDuration = new("FireUser.AttachmentTransfer");
}
