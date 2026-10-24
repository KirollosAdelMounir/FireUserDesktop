using System;
using System.Threading;
using System.Threading.Tasks;
namespace UserIdentityDesktop.Core;
public interface IBackendActionQueue
{
    bool IsConnected { get; }
    int PendingCount { get; }
    event Action? Changed;
    void SetAccount(string account);
    void SetConnected(bool connected);
    Task<T> RunAsync<T>(Func<CancellationToken,Task<T>> operation,CancellationToken cancellationToken=default);
}
