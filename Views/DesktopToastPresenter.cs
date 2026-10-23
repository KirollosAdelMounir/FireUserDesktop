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
using Avalonia.Threading;
using UserIdentityDesktop.Core;

namespace UserIdentityDesktop.Views;

/// <summary>The web toast card, rendered natively within the desktop working area.</summary>
public sealed class DesktopToastPresenter(Window owner, Func<bool> reducedMotion)
{
    private readonly List<DesktopToastWindow> visible = new();
    internal IReadOnlyList<Window> ActiveWindows => visible;
    private TransferToastWindow? transfers;
    internal Window? TransferWindow => transfers;
    public void ObserveTransfers(ITransferTracker tracker)
    {
        var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(350) };
        var stopped=false;
        void Refresh()
        {
            if(stopped)return;
            var states=tracker.Snapshot();
            foreach(var state in states)
                if(state.FinishedAt is {} at && DateTimeOffset.UtcNow-at>TimeSpan.FromSeconds(state.Succeeded?4:10)) tracker.RemoveCompleted(state.Id);
            states=tracker.Snapshot();
            if(states.Count==0) { transfers?.CloseForPresenter(); transfers=null; timer.Stop(); PositionAll(); return; }
            if(transfers==null)
            {
                transfers=new TransferToastWindow(reducedMotion()) { RequestedThemeVariant=owner.ActualThemeVariant };
                transfers.SizeChanged+=(_,_)=>PositionAll();
                transfers.Update(states);transfers.Show();
                var motion=new SurfaceMotion();
                transfers.Closed+=(_,_)=>motion.Cancel();
                _=motion.EnterAsync(transfers.Card,reducedMotion(),SurfaceMotionKind.Toast);
            }
            else transfers.Update(states);
            timer.Start(); PositionAll();
        }
        void Changed()=>Dispatcher.UIThread.Post(Refresh);
        tracker.Changed+=Changed;timer.Tick+=(_,_)=>Refresh();
        owner.Closed+=(_,_)=>{stopped=true;timer.Stop();tracker.Changed-=Changed;transfers?.CloseForPresenter();transfers=null;};
        Refresh();
    }
    public void Show(string title, string message, Action activate, bool chat = false, Bitmap? avatar = null, Action<string>? decide = null)
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
            var exit = motion.ExitAsync(toast.ToastCard, reducedMotion(), SurfaceMotionKind.Toast);
            await Task.WhenAny(exit, Task.Delay(TimeSpan.FromMilliseconds(400)));
            motion.Cancel();
            toast.Close();
            await exit;
        }
        toast.DecisionRequested += action => { if (closing || decide == null) return; decide(action); _ = Dismiss(false); };
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
                var animations = Task.WhenAll(progress, motion.EnterAsync(toast.ToastCard, reducedMotion(), SurfaceMotionKind.Toast));
                try
                {
                    // Rendering may be throttled. The six-second lifetime must
                    // not wait for the progress/entrance animation's clock.
                    await delay;
                    await Dismiss(false);
                }
                finally
                {
                    lifetime.Cancel();
                    try { await animations; }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                }
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
        transfers?.FitWorkingArea(area.Height / scale - 32);
        var windows = (transfers is null ? Array.Empty<Window>() : new Window[]{transfers}).Concat(visible.Cast<Window>()).ToArray();
        var heights = windows.Select(x => (x.Bounds.Height > 0 ? x.Bounds.Height : 136) * scale).ToArray();
        var gap = 12 * scale;
        if (transfers != null && visible.Count > 0 && heights.Sum() + Math.Max(0, heights.Length - 1) * gap > area.Height - 32 * scale)
        { visible[0].Close(); return; }
        var top = area.Y + Math.Max(16 * scale, (area.Height - heights.Sum() - Math.Max(0, heights.Length - 1) * gap) / 2);
        for (var i = 0; i < windows.Length; i++)
        {
            windows[i].Position = new PixelPoint(area.Right - (int)(412 * scale), (int)top);
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
