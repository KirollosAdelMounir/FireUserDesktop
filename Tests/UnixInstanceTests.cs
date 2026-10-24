using UserIdentityDesktop.Service;
using Xunit;

namespace UserIdentityDesktop.Tests;

public sealed class UnixInstanceTests
{
    [Fact]
    public void SocketPathUsesUtf8LengthAndSeparatesLongProfiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "fu-short");
        // Linux/Windows temporary paths vary; use a short synthetic base to
        // exercise the platform-independent length decision deterministically.
        Assert.EndsWith("fireuserconnect.sock", UnixAppInstanceService.SocketPath("/u/a", "/t"));
        var longProfile = root + new string('é', 70);
        var path = UnixAppInstanceService.SocketPath(longProfile, "/t");
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(path) < 104);
        Assert.Equal(path, UnixAppInstanceService.SocketPath(longProfile, "/t"));
        Assert.NotEqual(path, UnixAppInstanceService.SocketPath(longProfile + "other", "/t"));
        Assert.Throws<IOException>(() => UnixAppInstanceService.SocketPath(longProfile, "/" + new string('x', 110)));
    }

    [Fact]
    public async Task LongProfileActivationRetainsPrivateEndpointAndOwnerLock()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("Unix domain socket lifecycle"); return; }
        var root = Path.Combine(Path.GetTempPath(), "fu-" + Guid.NewGuid().ToString("N"), new string('x', 90));
        Directory.CreateDirectory(root);
        try
        {
            using var owner = new UnixAppInstanceService(root);
            Assert.True(owner.TryAcquire());
            var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            await owner.StartServerAsync(TestContext.Current.CancellationToken, text => { received.TrySetResult(text); return Task.CompletedTask; });
            using var contender = new UnixAppInstanceService(root);
            Assert.False(contender.TryAcquire());
            Assert.True(await contender.SendToExistingInstanceAsync("fireuserconnect://activate"));
            Assert.Equal("fireuserconnect://activate", await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            var socket = UnixAppInstanceService.SocketPath(Path.Combine(root, "UserIdentityDesktop"), Path.GetTempPath());
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(socket));
            owner.Dispose();
            using var successor = new UnixAppInstanceService(root);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!successor.TryAcquire()) await Task.Delay(10, timeout.Token);
        }
        finally { Directory.Delete(Directory.GetParent(root)!.FullName, recursive: true); }
    }

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
