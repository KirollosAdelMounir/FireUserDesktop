using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace UserIdentityDesktop.Views;

/// <summary>Enter submits a text form; Shift+Enter remains native multiline input.</summary>
public sealed class EnterSubmit : AvaloniaObject
{
    public static readonly AttachedProperty<ICommand?> CommandProperty = AvaloniaProperty.RegisterAttached<EnterSubmit, Control, ICommand?>("Command");
    public static ICommand? GetCommand(Control control) => control.GetValue(CommandProperty);
    public static void SetCommand(Control control, ICommand? value) => control.SetValue(CommandProperty, value);
    static EnterSubmit() => CommandProperty.Changed.AddClassHandler<Control>((control, _) =>
    {
        control.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
        if (GetCommand(control) != null) control.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    });
    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None || sender is not Control form || e.Source is not Control source) return;
        if (source is not TextBox && source.FindAncestorOfType<TextBox>() == null) return;
        // Keep selection keys in an open combo/dropdown local to that control.
        if (source.FindAncestorOfType<ComboBox>() is { IsDropDownOpen: true }) return;
        if (GetCommand(form) is not {} command) return;
        e.Handled = true;
        if (command.CanExecute(null)) command.Execute(null);
    }
}
