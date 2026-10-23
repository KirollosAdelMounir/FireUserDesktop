using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Selection;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace UserIdentityDesktop.Views;

/// <summary>Animate only the newly selected live tab, never an outgoing interactive page.</summary>
public sealed class TabMotion : AvaloniaObject
{
    public static readonly AttachedProperty<bool> EnabledProperty = AvaloniaProperty.RegisterAttached<TabMotion, TabControl, bool>("Enabled");
    private static readonly AttachedProperty<SurfaceMotion?> MotionProperty = AvaloniaProperty.RegisterAttached<TabMotion, TabControl, SurfaceMotion?>("Motion");
    public static bool GetEnabled(TabControl control) => control.GetValue(EnabledProperty);
    public static void SetEnabled(TabControl control, bool value) => control.SetValue(EnabledProperty, value);
    static TabMotion() => EnabledProperty.Changed.AddClassHandler<TabControl>((control, _) =>
    {
        control.SelectionChanged -= Changed;
        if (!GetEnabled(control)) { Stop(control); return; }
        control.SetValue(MotionProperty, new SurfaceMotion());
        control.SelectionChanged += Changed;
        control.DetachedFromVisualTree += (_, _) => Stop(control);
    });
    private static readonly AttachedProperty<int> GenerationProperty = AvaloniaProperty.RegisterAttached<TabMotion, TabControl, int>("Generation");
    public static void Stop(TabControl control)
    {
        control.SetValue(GenerationProperty, control.GetValue(GenerationProperty) + 1);
        control.GetValue(MotionProperty)?.Cancel();
    }
    private static void Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not TabControl tabs || e.Source != tabs) return;
        var motion = tabs.GetValue(MotionProperty)!;
        Stop(tabs);
        var generation = tabs.GetValue(GenerationProperty);
        var selected = tabs.SelectedItem;
        Dispatcher.UIThread.Post(() =>
        {
            if (!tabs.IsAttachedToVisualTree() || !GetEnabled(tabs) || tabs.GetValue(GenerationProperty) != generation || tabs.SelectedItem != selected) return;
            var content = tabs.SelectedContent as Control ?? (selected as TabItem)?.Content as Control;
            if (content != null) _ = motion.EnterAsync(content, SurfaceMotion.Reduced(tabs));
        }, DispatcherPriority.Loaded);
    }
}
