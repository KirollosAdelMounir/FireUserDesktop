using Avalonia;
using Avalonia.Controls;

namespace UserIdentityDesktop.Views;

/// <summary>Animate the menu surface without changing native dismissal or focus handling.</summary>
public sealed class MenuMotion : AvaloniaObject
{
    public static readonly AttachedProperty<bool> EnabledProperty = AvaloniaProperty.RegisterAttached<MenuMotion, ContextMenu, bool>("Enabled");
    public static bool GetEnabled(ContextMenu menu) => menu.GetValue(EnabledProperty);
    public static void SetEnabled(ContextMenu menu, bool value) => menu.SetValue(EnabledProperty, value);
    static MenuMotion() => EnabledProperty.Changed.AddClassHandler<ContextMenu>((menu, args) =>
    {
        if (!GetEnabled(menu)) return;
        var motion = new SurfaceMotion();
        menu.Opened += (_, _) => { if (GetEnabled(menu)) _ = motion.EnterAsync(menu, SurfaceMotion.Reduced(menu), SurfaceMotionKind.Popup); };
        menu.Closed += (_, _) => motion.Cancel();
        menu.DetachedFromVisualTree += (_, _) => motion.Cancel();
    });
}
