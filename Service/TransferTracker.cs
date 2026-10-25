using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UserIdentityDesktop.Core;
namespace UserIdentityDesktop.Service;
/// <summary>Byte reports stay off the dispatcher. The UI samples this local state on its display clock.</summary>
public sealed class TransferTracker(TimeProvider? clock = null) : ITransferTracker
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<Guid, Operation> operations = new();
    public event Action? Changed;
    public ITransferOperation Start(TransferDirection direction, int files, long? bytes = null, TransferPurpose purpose = TransferPurpose.ChatAttachment)
    {
        if (files < 1) throw new ArgumentOutOfRangeException(nameof(files));
        var op = new Operation(this, direction, files, bytes, purpose);
        lock (gate) operations.Add(op.Id, op);
        Changed?.Invoke(); return op;
    }
    public IReadOnlyList<TransferSnapshot> Snapshot()
    {
        lock (gate) return operations.Values.Select(x => x.Snapshot()).ToArray();
    }
    public void Cancel(Guid id) { Operation? op; lock(gate)operations.TryGetValue(id,out op);op?.Cancel(); }
    public void Clear() { Operation[] previous;lock (gate) { previous=operations.Values.ToArray();operations.Clear(); }foreach(var op in previous)op.Cancel();Changed?.Invoke(); }
    public void RemoveCompleted(Guid id) { lock (gate) { if (operations.TryGetValue(id, out var op) && op.finished) operations.Remove(id); } }
    private sealed class Operation : ITransferOperation
    {
        private readonly TransferTracker owner;
        internal readonly Guid Id = Guid.NewGuid();
        private readonly TransferDirection direction;
        private readonly int files;
        private readonly TransferPurpose purpose;
        private readonly CancellationTokenSource cancellation=new();
        private bool cancelling;
        public CancellationToken CancellationToken { get; }
        private long bytes; private long? total; private int completed;
        internal bool finished; private bool success; private DateTimeOffset? finishedAt;
        private string phase;
        private readonly Queue<(DateTimeOffset At, long Bytes)> samples = new();
        public Operation(TransferTracker owner, TransferDirection direction, int files, long? total,TransferPurpose purpose)
        { this.owner=owner; this.direction=direction; this.files=files; this.total=total;this.purpose=purpose;CancellationToken=cancellation.Token; phase=purpose==TransferPurpose.RemoteFile?"Preparing remote file transfer…":direction==TransferDirection.Upload ? "Preparing encrypted attachments…" : "Connecting…"; samples.Enqueue((owner.time.GetUtcNow(),0)); }
        public void Cancel()
        {
            lock(owner.gate){if(finished||cancelling)return;cancelling=true;phase="Cancelling…";}
            try{cancellation.Cancel();}catch(ObjectDisposedException){}finally{owner.Changed?.Invoke();}
        }
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
            return new(Id,direction,files,completed,bytes,total,speed,phase,finished,success,finishedAt){Purpose=purpose,CancellationRequested=cancelling};
        }
        public void SetTotal(long? value) { lock(owner.gate) { if(!finished) total=value is >=0 ? value : null; } }
        public void SetPhase(string value) { lock(owner.gate) { if(!finished&&!cancelling) phase=value; } }
        public void FileCompleted() { lock(owner.gate) { if(!finished) completed=Math.Min(files,completed+1); } }
        public void Complete(bool value)
        {
            lock(owner.gate) { if(finished)return; finished=true; success=value;if(value)completed=files; finishedAt=owner.time.GetUtcNow(); phase=value ? "Completed" : cancelling?"Cancelled":"Transfer failed. Please try again."; }
            owner.Changed?.Invoke();
        }
        public void Dispose() { Complete(false);cancellation.Dispose(); }
    }
}
