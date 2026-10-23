using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

using UserIdentityDesktop.Core;
namespace UserIdentityDesktop.Service;

public sealed class WindowsAppInstanceService : IAppInstanceService
{
    private const string MutexName = "Global\\FireUserConnectSingleInstance";
    private readonly string pipeName;

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _disposeCts = new();
    private Task? _serverTask;
    private readonly object _serverLock = new();
    private bool _disposed;
    private bool acquired;
    internal Task ServerCompletion => _serverTask ?? Task.CompletedTask;

    public WindowsAppInstanceService() : this(MutexName, "fireuserconnect-ipc") { }
    internal WindowsAppInstanceService(string mutexName, string pipeName)
    {
        this.pipeName = pipeName;
        _mutex = new Mutex(false, mutexName, out _);
    }

    public bool TryAcquire()
    {
        if (_disposed)
            return false;
        if (acquired) return true;

        try
        {
            return acquired = _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            return acquired = true;
        }
    }

    public Task StartServerAsync(CancellationToken cancellationToken, Func<string, Task> onMessageReceived)
    {
        if (_disposed)
            return Task.CompletedTask;

        lock (_serverLock)
        {
            if (_disposed || _serverTask != null)
                return Task.CompletedTask;
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
            _serverTask = Task.Run(async () => { using (linked) await RunServerLoopAsync(linked.Token, onMessageReceived); });
        }

        return Task.CompletedTask;
    }

    public async Task<bool> SendToExistingInstanceAsync(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return true;

        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var pipeClient = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                await pipeClient.ConnectAsync(1500);

                using var writer = new StreamWriter(pipeClient, Encoding.UTF8, leaveOpen: true);
                await writer.WriteAsync(message.Trim());
                await writer.FlushAsync();
                return true;
            }
            catch (TimeoutException)
            {
                if (attempt == 9)
                    return false;
                await Task.Delay(200);
            }
            catch (IOException)
            {
                if (attempt == 9)
                    return false;
                await Task.Delay(200);
            }
            catch (Exception)
            {
                return false;
            }
        }

        return false;
    }

    public void Release()
    {
        if (_disposed)
            return;

        try
        {
            _mutex.ReleaseMutex();
            acquired = false;
        }
        catch (Exception)
        {
            // Ignore if the mutex is not held by this process.
        }
    }

    public void Dispose()
    {
        lock (_serverLock)
        {
            if (_disposed) return;
            Release();
            _disposed = true;
            _disposeCts.Cancel();
            _mutex.Dispose();
            // Tasks do not require disposal. In particular, disposing the listener
            // while it awaits a connection throws during process exit.
            if (_serverTask == null) _disposeCts.Dispose();
            else _ = _serverTask.ContinueWith(_ => _disposeCts.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task RunServerLoopAsync(CancellationToken cancellationToken, Func<string, Task> onMessageReceived)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var pipeServer = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipeServer.WaitForConnectionAsync(cancellationToken);

                using var reader = new StreamReader(pipeServer, Encoding.UTF8);
                var message = await reader.ReadToEndAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(message))
                    await onMessageReceived(message.Trim()).WaitAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown in progress.
        }
        catch (Exception)
        {
            // Ignore pipe failures and keep the service alive if the host is still starting.
        }
    }
}
