using System.Text;
using UserIdentityDesktop.Service;
using Xunit;

namespace UserIdentityDesktop.Tests;
public sealed class WindowsNotificationTests
{
    [Fact]
    public async Task Shortcut_CanSetApplicationIdentityAndSaveThroughTheSameComWrapper()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows shell COM integration"); return; }
        var path = Path.Combine(Path.GetTempPath(), "fireuser-shortcut-test-" + Guid.NewGuid().ToString("N") + ".lnk");
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                for (var i = 0; i < 2; i++)
                {
                    SystemNotificationService.WriteWindowsToastShortcut(Environment.ProcessPath!, path);
                    var bytes = File.ReadAllBytes(path);
                    Assert.True(bytes.Length > 76);
                    Assert.Equal(76, BitConverter.ToInt32(bytes, 0));
                    Assert.True(bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes("UserIdentityDesktop.Desktop")) >= 0);
                }
                completed.SetResult();
            }
            catch (Exception ex) { completed.SetException(ex); }
            finally { File.Delete(path); }
        });
        thread.IsBackground = true; thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }
}
