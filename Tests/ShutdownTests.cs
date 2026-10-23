using Avalonia.Controls;
using UserIdentityDesktop.Service;
using UserIdentityDesktop.Views;
using Xunit;

namespace UserIdentityDesktop.Tests;
public sealed class ShutdownTests
{
    [Fact]
    public async Task SystemShutdown_IsNeverVetoed_EvenWhenCleanupIsPending()
    {
        var cleanup = new TaskCompletionSource(); var calls = 0; var allowed = 0; var cancelled = 0;
        var shutdown = new ApplicationShutdown(() => { calls++; return cleanup.Task; }, () => allowed++, () => throw new InvalidOperationException());
        shutdown.Request(true, () => cancelled++);
        shutdown.Request(true, () => cancelled++);
        Assert.Equal(0, cancelled); Assert.True(allowed > 0); Assert.Equal(1, calls);
        cleanup.SetResult(); await shutdown.Completion;
    }
    [Fact]
    public async Task ExplicitExit_IsBounded_AndStartsCleanupOnlyOnce()
    {
        var cleanup = new TaskCompletionSource(); var calls = 0; var exits = 0;
        var shutdown = new ApplicationShutdown(() => { calls++; return cleanup.Task; }, () => {}, () => exits++, TimeSpan.FromMilliseconds(30));
        shutdown.Request(false, () => {}); shutdown.Request(false, () => {});
        await shutdown.Completion.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(1, calls); Assert.Equal(1, exits); cleanup.SetResult();
    }
    [Fact]
    public async Task SystemShutdownDuringExplicitExit_DoesNotExitTwice_AndCleanupFailuresAreObserved()
    {
        var cleanup = new TaskCompletionSource(); var exits = 0;
        var shutdown = new ApplicationShutdown(() => cleanup.Task, () => {}, () => exits++);
        shutdown.Request(false, () => {}); var explicitExit = shutdown.Completion;
        shutdown.Request(true, () => throw new InvalidOperationException());
        cleanup.SetException(new IOException("cleanup failed"));
        await Task.WhenAll(explicitExit, shutdown.Completion); Assert.Equal(0, exits);
    }
    [Theory]
    [InlineData(WindowCloseReason.OSShutdown)] [InlineData(WindowCloseReason.ApplicationShutdown)] [InlineData(WindowCloseReason.OwnerWindowClosing)]
    public void TrayClosePolicy_DoesNotCancelExternalShutdown(WindowCloseReason reason) => Assert.False(MainWindow.HidesOnClose(false, reason));
    [Fact]
    public async Task WindowsListener_DisposeWhileWaitingOrReading_CancelsWithoutDisposingRunningTask()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows named-pipe lifecycle"); return; }
        foreach (var connected in new[] { false, true })
        {
            var name = "fireuser-test-" + Guid.NewGuid().ToString("N");
            using var instance = new WindowsAppInstanceService(name, name);
            await instance.StartServerAsync(CancellationToken.None, _ => Task.CompletedTask);
            using var peer = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.Out, System.IO.Pipes.PipeOptions.Asynchronous);
            if (connected) await peer.ConnectAsync(3000, TestContext.Current.CancellationToken);
            instance.Dispose(); instance.Dispose();
            await instance.ServerCompletion.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            Assert.False(instance.TryAcquire());
        }
    }
}
