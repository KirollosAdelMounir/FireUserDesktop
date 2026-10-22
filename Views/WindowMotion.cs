using System.Threading.Tasks;
using Avalonia.Controls;

namespace UserIdentityDesktop.Views;

internal sealed class WindowMotion
{
    private readonly Window window;
    private readonly SurfaceMotion motion = new();
    private bool closing, closed;
    public WindowMotion(Window window)
    {
        this.window = window;
        window.Opened += (_, _) => { if (window.Content is Control content) _ = motion.EnterAsync(content, SurfaceMotion.Reduced(window), SurfaceMotionKind.Popup); };
        window.Closed += (_, _) => { closed = true; motion.Cancel(); };
        window.Closing += (_, e) =>
        {
            if (closing || e.IsProgrammatic || e.CloseReason is WindowCloseReason.OSShutdown or WindowCloseReason.ApplicationShutdown or WindowCloseReason.OwnerWindowClosing) return;
            e.Cancel = true; _ = CloseAsync<object?>(null);
        };
    }
    public async Task CloseAsync<T>(T result)
    {
        if (closing) return;
        closing = true;
        if (window.Content is Control content) await motion.ExitAsync(content, SurfaceMotion.Reduced(window));
        if (!closed) window.Close(result);
    }
}
