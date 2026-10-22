using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace UserIdentityDesktop;

/// <summary>OS shutdown is never vetoed by tray behavior or remote cleanup.</summary>
internal sealed class ApplicationShutdown(Func<Task> cleanup, Action allowClose, Action exit, TimeSpan? timeout = null)
{
    private Task? cleanupTask;
    private bool exiting, prepared;
    internal Task Completion { get; private set; } = Task.CompletedTask;
    public void Request(bool operatingSystemShutdown, Action cancel)
    {
        if (operatingSystemShutdown)
        {
            prepared = true;
            allowClose();
            Completion = ObserveCleanupAsync();
            return;
        }
        if (prepared) return;
        cancel();
        if (exiting) return;
        exiting = true;
        Completion = ExitAsync();
    }
    private Task Cleanup() => cleanupTask ??= cleanup();
    private async Task ObserveCleanupAsync()
    {
        try { await Cleanup(); }
        catch (Exception ex) { Debug.WriteLine("Shutdown cleanup: " + ex.GetType().Name); }
    }
    private async Task ExitAsync()
    {
        try { await ObserveCleanupAsync().WaitAsync(timeout ?? TimeSpan.FromSeconds(3)); }
        catch (TimeoutException) { Debug.WriteLine("Shutdown cleanup exceeded the exit grace period."); }
        // A system shutdown may have arrived while ordinary cleanup was pending.
        if (prepared) return;
        prepared = true; allowClose(); exit();
    }
}
