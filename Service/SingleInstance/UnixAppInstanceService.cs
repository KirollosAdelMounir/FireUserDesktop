using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using UserIdentityDesktop.Core;
namespace UserIdentityDesktop.Service;

public sealed class UnixAppInstanceService : IAppInstanceService
{
    private readonly string _socketPath;
    private readonly string _lockPath;
    private readonly FileStream _lockFile;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _serverLock = new();
    private Task? _serverTask;
    private bool _disposed;
    private bool _isAcquired;

    public UnixAppInstanceService()
    {
        var appDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(appDataRoot))
            appDataRoot = Path.GetTempPath();

        var appDirectory = Path.Combine(appDataRoot, "UserIdentityDesktop");
        Directory.CreateDirectory(appDirectory);

        _socketPath = Path.Combine(appDirectory, "fireuserconnect.sock");
        _lockPath = _socketPath + ".lock";
        _lockFile = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public bool TryAcquire()
    {
        if (_disposed)
            return false;

        try
        {
            if (!OperatingSystem.IsMacOS())
                _lockFile.Lock(0, 0);

            _isAcquired = true;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public Task StartServerAsync(CancellationToken cancellationToken, Func<string, Task> onMessageReceived)
    {
        if (!_isAcquired || _disposed)
            return Task.CompletedTask;

        lock (_serverLock)
        {
            if (_serverTask != null)
                return Task.CompletedTask;

            _serverTask = Task.Run(() => RunServerLoopAsync(cancellationToken, onMessageReceived), cancellationToken);
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
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                socket.Connect(new UnixDomainSocketEndPoint(_socketPath));

                var payload = Encoding.UTF8.GetBytes(message.Trim() + Environment.NewLine);
                var sent = 0;
                while (sent < payload.Length)
                {
                    var chunkLength = await socket.SendAsync(payload.AsMemory(sent), SocketFlags.None);
                    if (chunkLength <= 0)
                        break;

                    sent += chunkLength;
                }

                return sent == payload.Length;
            }
            catch (SocketException)
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
            if (!OperatingSystem.IsMacOS())
                _lockFile.Unlock(0, 0);
        }
        catch (Exception)
        {
            // Ignore if the lock is not active for this process.
        }

        if (File.Exists(_socketPath))
        {
            try
            {
                File.Delete(_socketPath);
            }
            catch (IOException)
            {
                // The socket may still be in use during shutdown.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _disposeCts.Cancel();
        Release();
        _lockFile.Dispose();
    }

    private async Task RunServerLoopAsync(CancellationToken cancellationToken, Func<string, Task> onMessageReceived)
    {
        try
        {
            if (File.Exists(_socketPath))
            {
                try
                {
                    File.Delete(_socketPath);
                }
                catch (IOException)
                {
                    // A stale socket file may be left behind by a previous crash.
                }
            }

            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(_socketPath));
            socket.Listen(5);

            while (!cancellationToken.IsCancellationRequested)
            {
                var accepted = await socket.AcceptAsync();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var acceptedSocket = accepted;
                        using var networkStream = new NetworkStream(acceptedSocket, ownsSocket: true);
                        using var reader = new StreamReader(networkStream, Encoding.UTF8);
                        var message = await reader.ReadToEndAsync();
                        if (!string.IsNullOrWhiteSpace(message))
                            await onMessageReceived(message.Trim());
                    }
                    catch (Exception)
                    {
                        // Ignore client disconnects and invalid payloads.
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown in progress.
        }
        catch (SocketException)
        {
            // Ignore socket startup failures during shutdown.
        }
        catch (Exception)
        {
            // Ignore server startup issues to avoid crashing the app.
        }
        finally
        {
            if (File.Exists(_socketPath))
            {
                try
                {
                    File.Delete(_socketPath);
                }
                catch (IOException)
                {
                    // Cleanup is best effort.
                }
            }
        }
    }
}
