using System;
using System.Collections.Generic;
using System.Net.Http;
namespace UserIdentityDesktop.Core;
public enum TransferDirection { Upload, Download }
public sealed record TransferSnapshot(Guid Id, TransferDirection Direction, int TotalFiles, int CompletedFiles,
    long Bytes, long? TotalBytes, double BytesPerSecond, string Phase, bool Finished, bool Succeeded, DateTimeOffset? FinishedAt)
{
    public double Percentage => Succeeded ? 100 : TotalBytes is > 0 ? Math.Min(99, 100d * Bytes / TotalBytes.Value) : 0;
}
public interface ITransferOperation : IProgress<long>, IDisposable
{
    void SetTotal(long? bytes);
    void SetPhase(string phase);
    void FileCompleted();
    void Complete(bool success);
}
public interface ITransferTracker
{
    event Action? Changed;
    ITransferOperation Start(TransferDirection direction, int files, long? bytes = null);
    IReadOnlyList<TransferSnapshot> Snapshot();
    void Clear();
    void RemoveCompleted(Guid id);
}

/// <summary>Transfer duration is governed by cancellation and transport failure, never a whole-file deadline.</summary>
public static class AttachmentTransferRequests
{
    public static readonly HttpRequestOptionsKey<bool> UnlimitedDuration = new("FireUser.AttachmentTransfer");
}
