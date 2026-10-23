using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using UserIdentityDesktop.ViewModels;

namespace UserIdentityDesktop.Views;

public enum SurfaceMotionKind { Page, Popup, Overlay, Drawer, Toast }

/// <summary>One animation owns a surface; cancellation restores it before reuse.</summary>
internal sealed class SurfaceMotion
{
    private CancellationTokenSource? active;
    private Control? target;
    private ITransform? previousTransform;
    private double previousOpacity;
    private RelativePoint previousOrigin;
    internal Task Completion { get; private set; } = Task.CompletedTask;
    internal static bool Reduced(Control control) => Environment.GetEnvironmentVariable("FIREUSER_REDUCED_MOTION") == "1" || control.DataContext switch
    {
        WorkspaceViewModel workspace => workspace.ReduceMotion,
        ChatViewModel chat => chat.Workspace?.ReduceMotion == true,
        _ => App.MainViewModelInstance?.CurrentView is WorkspaceViewModel { ReduceMotion: true }
    };
    public Task EnterAsync(Control surface, bool reduced, SurfaceMotionKind kind = SurfaceMotionKind.Page) => Start(surface, reduced, kind, false);
    public Task ExitAsync(Control surface, bool reduced, SurfaceMotionKind kind = SurfaceMotionKind.Popup) => Start(surface, reduced, kind, true);
    public void Cancel()
    {
        var stop = active; active = null;
        stop?.Cancel();
        Restore();
        // The running method disposes its token source only after RunAsync returns.
    }
    private void Restore()
    {
        if (target == null) return;
        target.Opacity = previousOpacity; target.RenderTransform = previousTransform; target.RenderTransformOrigin = previousOrigin; target = null;
    }
    private Task Start(Control surface, bool reduced, SurfaceMotionKind kind, bool exit)
    {
        Cancel();
        if (reduced) return Completion = Task.CompletedTask;
        var stop = new CancellationTokenSource(); active = stop; target = surface;
        previousTransform = surface.RenderTransform; previousOpacity = surface.Opacity; previousOrigin = surface.RenderTransformOrigin;
        surface.RenderTransform = new TransformGroup { Children = { new ScaleTransform(), new TranslateTransform() } };
        surface.RenderTransformOrigin = new RelativePoint(.5, .5, RelativeUnit.Relative);
        var offsetX = kind == SurfaceMotionKind.Drawer ? 18d : kind == SurfaceMotionKind.Toast ? 40d : 0;
        var offsetY = kind is SurfaceMotionKind.Page or SurfaceMotionKind.Popup ? 10d : 0;
        var scale = kind == SurfaceMotionKind.Popup ? .96d : kind == SurfaceMotionKind.Toast ? .95d : 1d;
        var effect = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(exit ? (kind == SurfaceMotionKind.Toast ? 320 : 180) : (kind is SurfaceMotionKind.Popup or SurfaceMotionKind.Overlay ? 180 : 240)), Easing = new AngularNavigationEasing(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, exit ? 1d : 0d), new Setter(TranslateTransform.XProperty, exit ? 0d : offsetX), new Setter(TranslateTransform.YProperty, exit ? 0d : offsetY), new Setter(ScaleTransform.ScaleXProperty, exit ? 1d : scale), new Setter(ScaleTransform.ScaleYProperty, exit ? 1d : scale) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, exit ? 0d : 1d), new Setter(TranslateTransform.XProperty, exit ? offsetX : 0d), new Setter(TranslateTransform.YProperty, exit ? offsetY : 0d), new Setter(ScaleTransform.ScaleXProperty, exit ? scale : 1d), new Setter(ScaleTransform.ScaleYProperty, exit ? scale : 1d) } }
            }
        };
        return Completion = RunAsync(effect, surface, stop);
    }
    private async Task RunAsync(Animation effect, Control surface, CancellationTokenSource stop)
    {
        try { await effect.RunAsync(surface, stop.Token); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            if (active == stop) { active = null; Restore(); }
            stop.Dispose();
        }
    }
}
