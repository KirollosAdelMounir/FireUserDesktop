using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UserIdentityDesktop.Core;
namespace UserIdentityDesktop.Data;
/// <summary>In-memory account-bound FIFO. Only work not sent yet is replayed; uncertain failures are never retried.</summary>
public sealed class BackendActionQueue : IBackendActionQueue
{
    private readonly object gate=new();
    private readonly Queue<Pending> pending=new();
    private CancellationTokenSource accountLifetime=new();
    private string account="";private bool online=true,draining;
    private int active;
    public bool IsConnected {get{lock(gate)return online;}}
    public int PendingCount {get{lock(gate)return pending.Count(x=>!x.Complete)+active;}}
    public event Action? Changed;
    private void Notify(){var handlers=Changed;if(handlers==null)return;foreach(Action handler in handlers.GetInvocationList())try{handler();}catch{/* Observers cannot fail an operation. */}}
    public void SetAccount(string value)
    {
        lock(gate){if(value==account)return;account=value;accountLifetime.Cancel();accountLifetime.Dispose();accountLifetime=new();while(pending.TryDequeue(out var item))item.Cancel();}
        Notify();
    }
    public void SetConnected(bool connected){lock(gate)online=connected;Notify();if(connected)_=DrainAsync();}
    public Task<T> RunAsync<T>(Func<CancellationToken,Task<T>> operation,CancellationToken cancellationToken=default)
    {
        Task<T>? direct=null;Pending<T>? work=null;
        lock(gate){
            if(account.Length==0)return Task.FromException<T>(new InvalidOperationException("Sign in before submitting an action."));
            if(online&&!draining&&pending.Count==0)direct=Execute(operation,accountLifetime.Token,cancellationToken);
            else{work=new(operation,accountLifetime.Token,cancellationToken,Notify);pending.Enqueue(work);}
        }
        if(direct!=null)return direct;
        Notify();if(IsConnected)_=DrainAsync();return work!.Task;
    }
    private static async Task<T> Execute<T>(Func<CancellationToken,Task<T>> operation,CancellationToken account,CancellationToken caller)
    {using var linked=CancellationTokenSource.CreateLinkedTokenSource(account,caller);linked.Token.ThrowIfCancellationRequested();return await operation(linked.Token);}
    private async Task DrainAsync()
    {
        lock(gate){if(draining||!online)return;draining=true;}
        try{while(true){Pending? item;lock(gate){if(!online||!pending.TryDequeue(out item))break;active++;}Notify();try{await item.Run();}finally{lock(gate)active--;Notify();}}}
        finally{bool resume;lock(gate){draining=false;resume=online&&pending.Count>0;}if(resume)_=DrainAsync();}
    }
    private abstract class Pending{public abstract bool Complete{get;}public abstract void Cancel();public abstract Task Run();}
    private sealed class Pending<T> : Pending
    {
        private readonly TaskCompletionSource<T> result=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Func<CancellationToken,Task<T>> operation;private readonly CancellationTokenSource linked;private readonly CancellationTokenRegistration registration;
        public Task<T> Task=>result.Task;public override bool Complete=>result.Task.IsCompleted;
        public Pending(Func<CancellationToken,Task<T>> operation,CancellationToken account,CancellationToken caller,Action changed){this.operation=operation;linked=CancellationTokenSource.CreateLinkedTokenSource(account,caller);registration=linked.Token.Register(()=>{result.TrySetCanceled(linked.Token);changed();});}
        public override void Cancel(){linked.Cancel();registration.Dispose();linked.Dispose();}
        public override async Task Run(){try{if(!Complete){linked.Token.ThrowIfCancellationRequested();result.TrySetResult(await operation(linked.Token));}}catch(OperationCanceledException){result.TrySetCanceled();}catch(Exception error){result.TrySetException(error);}finally{registration.Dispose();linked.Dispose();}}
    }
}
