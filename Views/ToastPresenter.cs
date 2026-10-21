using System;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Layout;
using Avalonia.Media;

namespace UserIdentityDesktop.Views;

/// <summary>Bounded, native foreground toasts. Background delivery belongs to the OS shell.</summary>
public sealed class ToastPresenter(Window owner, Func<bool>? reduceMotion = null)
{
    private WindowNotificationManager? manager;
    public void Show(string title, string message, Action activate)
    {
        manager ??= new WindowNotificationManager(owner)
        {
            Position = NotificationPosition.BottomRight,
            MaxItems = 3
        };
        // Apply the host template before Show: otherwise the first layout replaces
        // the manager's temporary item list and drops notifications posted immediately.
        manager.Resources[typeof(NotificationCard)] = Avalonia.Application.Current!.Resources["FireToastCardTheme"];
        manager.ApplyTemplate();
        var content = new Border
        {
            Classes = { "fire-toast" },
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                Children =
                {
                    new TextBlock { Text = "●", Foreground = Brush.Parse("#DD7036"), FontSize = 24, Margin = new Avalonia.Thickness(0,0,14,0) },
                    Body(title, message)
                }
            }
        };
        var action = new Button { Content = content, Classes = { "fire-toast-action" } };
        Avalonia.Automation.AutomationProperties.SetName(action, $"{title}. {message}. Open conversation");
        var opened = false;
        void OpenOnce() { if (opened) return; opened = true; activate(); }
        action.Click += (_, _) => { OpenOnce(); manager.Close(action); };
        manager.Show(action, NotificationType.Information, TimeSpan.FromSeconds(6), onClick: OpenOnce, classes: reduceMotion?.Invoke() == true ? ["reduced-motion"] : []);
    }
    private static StackPanel Body(string title, string message)
    {
        var body = new StackPanel { Spacing = 6, MaxWidth = 300, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(body, 1);
        body.Children.Add(new TextBlock { Text = "FIRE USER", FontSize = 10, FontWeight = FontWeight.SemiBold });
        body.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis });
        return body;
    }
}
