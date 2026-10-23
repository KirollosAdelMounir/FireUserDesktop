using System;
using System.Collections.Generic;
using System.Linq;
using UserIdentityDesktop.Core;
namespace UserIdentityDesktop.Service;
/// <summary>Byte reports stay off the dispatcher. The UI samples this local state on its display clock.</summary>
public sealed class TransferTracker(TimeProvider? clock = null) : ITransferTracker
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<Guid, Operation> operations = new();
    public event Action? Changed;
    public ITransferOperation Start(TransferDirection direction, int files, long? bytes = null)
    {
        if (files < 1) throw new ArgumentOutOfRangeException(nameof(files));
        var op = new Operation(this, direction, files, bytes);
        lock (gate) operations.Add(op.Id, op);
        Changed?.Invoke(); return op;
    }
    public IReadOnlyList<TransferSnapshot> Snapshot()
    {
        lock (gate) return operations.Values.Select(x => x.Snapshot()).ToArray();
    }
    public void Clear() { lock (gate) operations.Clear(); Changed?.Invoke(); }
    public void RemoveCompleted(Guid id) { lock (gate) { if (operations.TryGetValue(id, out var op) && op.finished) operations.Remove(id); } }
    private sealed class Operation : ITransferOperation
    {
        private readonly TransferTracker owner;
        internal readonly Guid Id = Guid.NewGuid();
        private readonly TransferDirection direction;
        private readonly int files;
        private long bytes; private long? total; private int completed;
        internal bool finished; private bool success; private DateTimeOffset? finishedAt;
        private string phase;
        private readonly Queue<(DateTimeOffset At, long Bytes)> samples = new();
        public Operation(TransferTracker owner, TransferDirection direction, int files, long? total)
        { this.owner=owner; this.direction=direction; this.files=files; this.total=total; phase=direction==TransferDirection.Upload ? "Preparing encrypted attachments…" : "Connecting…"; samples.Enqueue((owner.time.GetUtcNow(),0)); }
        public void Report(long value)
        {
            if(value <= 0) return;
            lock(owner.gate) { if(finished) return; bytes=checked(bytes+value); var now=owner.time.GetUtcNow(); samples.Enqueue((now,bytes)); Trim(now); }
        }
        private void Trim(DateTimeOffset now) { while(samples.Count>1 && now-samples.Peek().At>TimeSpan.FromSeconds(2)) samples.Dequeue(); }
        internal TransferSnapshot Snapshot()
        {
            var now=owner.time.GetUtcNow(); Trim(now); var first=samples.Peek();
            var seconds=(now-first.At).TotalSeconds;
            var speed=finished || seconds<=0 || now-samples.Last().At>TimeSpan.FromSeconds(2) ? 0 : (bytes-first.Bytes)/seconds;
            return new(Id,direction,files,completed,bytes,total,speed,phase,finished,success,finishedAt);
        }
        public void SetTotal(long? value) { lock(owner.gate) { if(!finished) total=value is >=0 ? value : null; } }
        public void SetPhase(string value) { lock(owner.gate) { if(!finished) phase=value; } }
        public void FileCompleted() { lock(owner.gate) { if(!finished) completed=Math.Min(files,completed+1); } }
        public void Complete(bool value)
        {
            lock(owner.gate) { if(finished)return; finished=true; success=value; finishedAt=owner.time.GetUtcNow(); phase=value ? "Completed" : "Transfer failed. Please try again."; }
            owner.Changed?.Invoke();
        }
        public void Dispose() { Complete(false); }
    }
}
