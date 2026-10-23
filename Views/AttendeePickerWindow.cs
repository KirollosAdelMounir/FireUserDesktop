using System;
using System.Linq;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Animation;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using UserIdentityDesktop.ViewModels;

namespace UserIdentityDesktop.Views;

internal sealed class AttendeePickerWindow : Window
{
    private readonly bool reducedMotion;
    private readonly Canvas transfers = new() { IsHitTestVisible = false };
    private readonly Dictionary<Guid, Button> rows = new();
    private readonly List<CancellationTokenSource> motions = new();
    public AttendeePickerWindow(AttendeePickerViewModel model, bool reducedMotion = false)
    {
        this.reducedMotion = reducedMotion;
        Title = "Meeting attendees"; Width = 960; Height = 700; MinWidth = 560; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; DataContext = model;
        this.Bind(BackgroundProperty, this.GetResourceObservable("ForgeBg")); this.Bind(ForegroundProperty, this.GetResourceObservable("ForgeTextPrimary"));
        var content = new StackPanel { Spacing = 14, Margin = new Thickness(24) };
        content.Children.Add(new TextBlock { Text = "Meeting attendees", FontSize = 24 });
        var search = new TextBox { PlaceholderText = "Search employees by name" }; search.Bind(TextBox.TextProperty, new Binding(nameof(model.Search)) { Mode = BindingMode.TwoWay }); content.Children.Add(search);
        var panes = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        panes.Children.Add(Pane("Search results", model.Results, model.SelectAllCommand, "Select all results"));
        var selected = Pane("Selected people", model.SelectedPeople, model.ClearCommand, "Clear selection"); Grid.SetColumn(selected, 1); panes.Children.Add(selected); content.Children.Add(panes);
        if (model.AllowsMeetingGuests)
        {
            var units = new ComboBox { ItemsSource = model.Units, PlaceholderText = "Select a business unit and its children", HorizontalAlignment = HorizontalAlignment.Stretch };
            units.Bind(ComboBox.SelectedItemProperty, new Binding(nameof(model.Unit)) { Mode = BindingMode.TwoWay }); content.Children.Add(units);
            content.Children.Add(new Button { Content = "Add business unit and children", Command = model.AddUnitCommand });
            content.Children.Add(new TextBlock { Text = "Only units within your reporting responsibility are available.", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            content.Children.Add(new ItemsControl { ItemsSource = model.SelectedUnits, ItemTemplate = new FuncDataTemplate<UserIdentityDesktop.Core.Models.MeetingUnitOption>((unit, _) => new Button { Content = unit!.Name + " ×", Command = model.RemoveUnitCommand, CommandParameter = unit }) });
            var email = new TextBox { PlaceholderText = "Guest email address" }; email.Bind(TextBox.TextProperty, new Binding(nameof(model.Email)) { Mode = BindingMode.TwoWay }); content.Children.Add(email);
            content.Children.Add(new Button { Content = "Add email invitation", Command = model.AddEmailCommand });
            content.Children.Add(new ItemsControl { ItemsSource = model.Emails, ItemTemplate = new FuncDataTemplate<string>((value, _) => new Button { Content = value + " ×", Command = model.RemoveEmailCommand, CommandParameter = value }) });
        }
        var error = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap }; error.Bind(TextBlock.TextProperty, new Binding(nameof(model.Error))); content.Children.Add(error);
        var done = new Button { Content = "Done", HorizontalAlignment = HorizontalAlignment.Right }; done.Classes.Add("fire-btn"); content.Children.Add(done);
        var surface = new Grid(); surface.Children.Add(new ScrollViewer { Content = content }); surface.Children.Add(transfers); Content = surface;
        Closed += (_, _) => { foreach (var stop in motions.ToArray()) stop.Cancel(); transfers.Children.Clear(); }; var motion = new WindowMotion(this); done.Click += async (_, _) => await motion.CloseAsync<object?>(null);
        Opened += async (_, _) =>
        {
            if (Screens.ScreenFromWindow(this) is {} screen) { MaxWidth = Math.Max(560, screen.WorkingArea.Width / screen.Scaling - 48); MaxHeight = Math.Max(460, screen.WorkingArea.Height / screen.Scaling - 48); }
            await model.LoadAsync();
        };
        Control Pane(string title, System.Collections.ObjectModel.ObservableCollection<PersonSelection> people, System.Windows.Input.ICommand all, string allText)
        {
            var stack = new StackPanel { Spacing = 8, Margin = new Thickness(8) };
            stack.Children.Add(new TextBlock { Text = title, FontSize = 18 }); var bulk = new Button { Content = allText }; bulk.Click += (_, _) => TransferSelection(() => all.Execute(null)); stack.Children.Add(bulk);
            var entries = new ItemsControl { ItemsSource = people, ItemTemplate = new FuncDataTemplate<PersonSelection>((person, scope) =>
            {
                var button = new Button { Content = person!.Name + (person.Selected ? "  −" : "  +"), HorizontalAlignment = HorizontalAlignment.Stretch };
                button.AttachedToVisualTree += (_, _) => rows[person.Id] = button;
                button.Click += (_, _) => TransferSelection(() => model.ToggleCommand.Execute(person));
                return button;
            }) };
            stack.Children.Add(new ScrollViewer { Content = entries, Height = 220 }); return stack;
        }
    }
    private void TransferSelection(Action change)
    {
        var before = rows.Where(x => x.Value.IsAttachedToVisualTree()).Select(x => (x.Key, Button: x.Value, Point: x.Value.TranslatePoint(new Point(), this), x.Value.Bounds)).ToArray();
        change();
        foreach (var stop in motions.ToArray()) stop.Cancel();
        if (reducedMotion || SurfaceMotion.Reduced(this)) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible) return;
            foreach (var old in before.Take(80))
            {
                if (old.Point is not {} from || !rows.TryGetValue(old.Key,out var target) || ReferenceEquals(target,old.Button)) continue;
                var to = target.TranslatePoint(new Point(),this);
                if (to == null || from.Y < 0 || from.Y > Bounds.Height || to.Value.Y < 0 || to.Value.Y > Bounds.Height) continue;
                var name = (DataContext as AttendeePickerViewModel)?.People.FirstOrDefault(x=>x.Id==old.Key)?.Name ?? "Attendee";
                _ = TransferAsync(target,name,from,to.Value,old.Bounds.Width,old.Bounds.Height);
            }
        },DispatcherPriority.Loaded);
    }
    private async Task TransferAsync(Button target, string name, Point from, Point to, double width, double height)
    {
        using var stop = new CancellationTokenSource(); motions.Add(stop);
        var label = new Border { Width = width, Height = height, Padding = new Thickness(12,6), CornerRadius = new CornerRadius(8), Background = Brushes.DarkOrange, Child = new TextBlock { Text = name, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center }, RenderTransform = new TranslateTransform() };
        Canvas.SetLeft(label, from.X); Canvas.SetTop(label, from.Y); transfers.Children.Add(label); target.Opacity = 0;
        var animation = new Animation { Duration = TimeSpan.FromMilliseconds(240), Easing = new AngularNavigationEasing(), Children = {
            new KeyFrame { Cue = new Cue(0), Setters = { new Setter(TranslateTransform.XProperty, 0d), new Setter(TranslateTransform.YProperty, 0d) } },
            new KeyFrame { Cue = new Cue(1), Setters = { new Setter(TranslateTransform.XProperty, to.X-from.X), new Setter(TranslateTransform.YProperty, to.Y-from.Y) } }
        } };
        try { await animation.RunAsync(label, stop.Token); }
        catch (OperationCanceledException) { }
        finally { transfers.Children.Remove(label); target.Opacity = 1; motions.Remove(stop); }
    }

}
