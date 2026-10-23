using System;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using UserIdentityDesktop.Core;
namespace UserIdentityDesktop.Service;

public sealed class UnixAppInstanceService : IAppInstanceService
{
    private readonly string _socketPath;
    private readonly string _lockPath;
    private FileStream? _lockFile;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _serverLock = new();
    private Task? _serverTask;
    private bool _disposed;
    private bool _isAcquired;

    public UnixAppInstanceService() : this(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) { }

    internal UnixAppInstanceService(string appDataRoot)
    {
        if (string.IsNullOrWhiteSpace(appDataRoot))
            appDataRoot = Path.GetTempPath();

        var appDirectory = Path.Combine(appDataRoot, "UserIdentityDesktop");
        Directory.CreateDirectory(appDirectory);

        _socketPath = SocketPath(appDirectory, Path.GetTempPath());
        // Keep ownership in the existing profile directory even when the socket
        // needs a short endpoint (macOS sun_path is only 104 bytes).
        _lockPath = Path.Combine(appDirectory, "fireuserconnect.sock.lock");
    }

    internal static string SocketPath(string appDirectory, string temporaryRoot)
    {
        var normal = Path.Combine(appDirectory, "fireuserconnect.sock");
        if (Encoding.UTF8.GetByteCount(normal) < 104) return normal;
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(appDirectory))))[..20];
        var compact = Path.Combine(temporaryRoot, "fu-" + identity + ".sock");
        if (Encoding.UTF8.GetByteCount(compact) >= 104)
            throw new IOException("The user temporary directory is too long for a Unix activation socket.");
        return compact;
    }

    public bool TryAcquire()
    {
        if (_disposed)
            return false;

        if (_isAcquired) return true;
        try
        {
            // Opening with FileShare.None acquires the OS lock on both Unix platforms.
            // Contention is an expected TryAcquire result, never a constructor failure.
            _lockFile = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

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

        if (!_isAcquired) return;
        _disposeCts.Cancel();
        // The listener removes its own socket before releasing ownership. A second
        // instance must never unlink the active owner's endpoint.
        if (_serverTask != null)
        {
            _ = _serverTask.ContinueWith(_ => ReleaseLock(), TaskScheduler.Default);
        }
        else ReleaseLock();
    }

    private void ReleaseLock()
    {
        _lockFile?.Dispose();
        _lockFile = null;
        _isAcquired = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Release();
        _disposed = true;
    }

    private async Task RunServerLoopAsync(CancellationToken cancellationToken, Func<string, Task> onMessageReceived)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        var stop = linked.Token;
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
            File.SetUnixFileMode(_socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            socket.Listen(5);

            while (!stop.IsCancellationRequested)
            {
                var accepted = await socket.AcceptAsync(stop);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var acceptedSocket = accepted;
                        using var networkStream = new NetworkStream(acceptedSocket, ownsSocket: true);
                        using var reader = new StreamReader(networkStream, Encoding.UTF8);
                        var message = await reader.ReadLineAsync(stop);
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
