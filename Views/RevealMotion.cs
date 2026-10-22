using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace UserIdentityDesktop.Views;

/// <summary>Keep popup content mounted through its exit, cancelling stale visibility changes.</summary>
public sealed class RevealMotion : AvaloniaObject
{
    public static readonly AttachedProperty<bool> IsOpenProperty = AvaloniaProperty.RegisterAttached<RevealMotion, Control, bool>("IsOpen", true);
    public static readonly AttachedProperty<SurfaceMotionKind> KindProperty = AvaloniaProperty.RegisterAttached<RevealMotion, Control, SurfaceMotionKind>("Kind", SurfaceMotionKind.Popup);
    private static readonly AttachedProperty<SurfaceMotion?> MotionProperty = AvaloniaProperty.RegisterAttached<RevealMotion, Control, SurfaceMotion?>("Motion");
    public static bool GetIsOpen(Control control) => control.GetValue(IsOpenProperty);
    public static void SetIsOpen(Control control, bool value) => control.SetValue(IsOpenProperty, value);
    public static SurfaceMotionKind GetKind(Control control) => control.GetValue(KindProperty);
    public static void SetKind(Control control, SurfaceMotionKind value) => control.SetValue(KindProperty, value);
    static RevealMotion() => IsOpenProperty.Changed.AddClassHandler<Control>((control, args) => { _ = Update(control); });
    private static readonly AttachedProperty<int> GenerationProperty = AvaloniaProperty.RegisterAttached<RevealMotion, Control, int>("Generation");
    private static async Task Update(Control control)
    {
        var motion = control.GetValue(MotionProperty);
        if (motion == null)
        {
            motion = new SurfaceMotion(); control.SetValue(MotionProperty, motion);
            control.DetachedFromVisualTree += (_, _) => { motion.Cancel(); control.IsVisible = GetIsOpen(control); };
        }
        var generation = control.GetValue(GenerationProperty) + 1;
        control.SetValue(GenerationProperty, generation);
        motion.Cancel();
        var open = GetIsOpen(control);
        if (!control.IsAttachedToVisualTree() || SurfaceMotion.Reduced(control)) { control.IsVisible = open; return; }
        if (open)
        {
            control.IsVisible = true;
            await motion.EnterAsync(control, false, GetKind(control));
        }
        else
        {
            await motion.ExitAsync(control, false, GetKind(control));
            if (control.GetValue(GenerationProperty) == generation && !GetIsOpen(control)) control.IsVisible = false;
        }
    }
}
