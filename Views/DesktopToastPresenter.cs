using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;

namespace UserIdentityDesktop.Views;

/// <summary>The web toast card, rendered natively within the desktop working area.</summary>
public sealed class DesktopToastPresenter(Window owner, Func<bool> reducedMotion)
{
    private readonly List<DesktopToastWindow> visible = new();
    internal IReadOnlyList<Window> ActiveWindows => visible;
    public void Show(string title, string message, Action activate, bool chat = false, Bitmap? avatar = null)
    {
        while (visible.Count >= 3) visible[0].Close();
        var toast = new DesktopToastWindow(title, message, chat, avatar) { RequestedThemeVariant = owner.ActualThemeVariant };
        var motion = new SurfaceMotion();
        var lifetime = new CancellationTokenSource();
        var token = lifetime.Token;
        var closing = false;
        Task display = Task.CompletedTask;
        async Task Dismiss(bool open)
        {
            if (closing) return;
            closing = true; lifetime.Cancel(); motion.Cancel();
            if (open) activate();
            await motion.ExitAsync(toast.ToastCard, reducedMotion(), SurfaceMotionKind.Toast);
            toast.Close();
        }
        toast.OpenRequested += () => _ = Dismiss(true);
        toast.DismissRequested += () => _ = Dismiss(false);
        visible.Add(toast);
        toast.SizeChanged += (_, _) => PositionAll();
        toast.Closed += (_, _) =>
        {
            lifetime.Cancel(); motion.Cancel(); visible.Remove(toast); PositionAll();
            _ = DisposeAfterDisplay(display, lifetime);
        };
        toast.Opened += (_, _) => display = Display();
        async Task Display()
        {
            try
            {
                PositionAll();
                var delay = Task.Delay(TimeSpan.FromSeconds(6), token);
                var progress = reducedMotion() ? Task.CompletedTask : Progress(toast.ToastProgress, token);
                await motion.EnterAsync(toast.ToastCard, reducedMotion(), SurfaceMotionKind.Toast);
                await Task.WhenAll(delay, progress);
                await Dismiss(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }
        toast.Show();
    }
    private static async Task DisposeAfterDisplay(Task display, CancellationTokenSource lifetime)
    {
        try { await display; }
        finally { lifetime.Dispose(); }
    }
    private void PositionAll()
    {
        var screen = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary;
        if (screen == null) return;
        var area = screen.WorkingArea; var scale = screen.Scaling;
        var heights = visible.Select(x => (x.Bounds.Height > 0 ? x.Bounds.Height : 136) * scale).ToArray();
        var gap = 12 * scale;
        var top = area.Y + Math.Max(16 * scale, (area.Height - heights.Sum() - Math.Max(0, heights.Length - 1) * gap) / 2);
        for (var i = 0; i < visible.Count; i++)
        {
            visible[i].Position = new PixelPoint(area.Right - (int)(412 * scale), (int)top);
            top += heights[i] + gap;
        }
    }
    private static Task Progress(Control target, CancellationToken ct) => new Animation
    {
        Duration = TimeSpan.FromSeconds(6), FillMode = FillMode.Forward,
        Children =
        {
            new KeyFrame { Cue = new Cue(0), Setters = { new Setter(ScaleTransform.ScaleXProperty, 1d) } },
            new KeyFrame { Cue = new Cue(1), Setters = { new Setter(ScaleTransform.ScaleXProperty, 0d) } }
        }
    }.RunAsync(target, ct);
}
