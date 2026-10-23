using UserIdentityDesktop.Service;
using Xunit;

namespace UserIdentityDesktop.Tests;

public sealed class UnixInstanceTests
{
    [Fact]
    public async Task ContendingInstance_HandsOffActivationWithoutRemovingOwnersSocket()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("Unix domain socket lifecycle"); return; }
        var root = Path.Combine(Path.GetTempPath(), "fu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var owner = new UnixAppInstanceService(root);
            Assert.True(owner.TryAcquire());
            var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            await owner.StartServerAsync(CancellationToken.None, text => { received.TrySetResult(text); return Task.CompletedTask; });
            using (var contender = new UnixAppInstanceService(root))
            {
                Assert.False(contender.TryAcquire());
                Assert.True(await contender.SendToExistingInstanceAsync("fireuserconnect://activate"));
                Assert.Equal("fireuserconnect://activate", await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            }
            received = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var another = new UnixAppInstanceService(root))
            {
                Assert.False(another.TryAcquire());
                Assert.True(await another.SendToExistingInstanceAsync("fireuserconnect://chat/" + Guid.Empty));
                Assert.Equal("fireuserconnect://chat/" + Guid.Empty, await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            }
            owner.Dispose();
            using var successor = new UnixAppInstanceService(root);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!successor.TryAcquire()) await Task.Delay(10, timeout.Token);
            Assert.True(successor.TryAcquire());
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
