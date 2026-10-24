using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Avalonia.Interactivity;
using UserIdentityDesktop.Core.Models;

namespace UserIdentityDesktop.Views;

public partial class ScheduleCalendar : UserControl
{
    public static readonly StyledProperty<DateTimeOffset?> FocusDateProperty =
        AvaloniaProperty.Register<ScheduleCalendar, DateTimeOffset?>(nameof(FocusDate), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<IEnumerable<CalendarItem>?> ItemsProperty =
        AvaloniaProperty.Register<ScheduleCalendar, IEnumerable<CalendarItem>?>(nameof(Items));
    public DateTimeOffset? FocusDate { get => GetValue(FocusDateProperty); set => SetValue(FocusDateProperty, value); }
    public IEnumerable<CalendarItem>? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    private readonly Button[] days = new Button[42];
    private INotifyCollectionChanged? observed;
    private bool attached;
    public event Action<DateTime, TimeSpan>? CreateScheduleRequested;

    public ScheduleCalendar()
    {
        InitializeComponent();
        UpdateHours();
        MinuteChoice.ItemsSource = Enumerable.Range(0, 60).Select(x => x.ToString("00")).ToArray();
        HourChoice.SelectedIndex = 9; MinuteChoice.SelectedIndex = 0;
        var names = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        for (var i = 0; i < names.Length; i++)
        {
            var label = new TextBlock { Text = names[i], Classes = { "calendar-weekday" } };
            Grid.SetColumn(label, i); DayGrid.Children.Add(label);
        }
        for (var i = 0; i < days.Length; i++)
        {
            var button = new Button { Classes = { "calendar-day" } };
            button.Click += (_, _) => FocusDate = new DateTimeOffset((DateTime)button.Tag!);
            button.KeyDown += Day_KeyDown;
            Grid.SetColumn(button, i % 7); Grid.SetRow(button, 1 + i / 7);
            days[i] = button; DayGrid.Children.Add(button);
        }
        RefreshDays();
    }

    public static DateTime MonthStart(DateTime day)
    {
        var first = new DateTime(day.Year, day.Month, 1);
        return first.AddDays(-(int)first.DayOfWeek);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsProperty)
        {
            if (observed != null) observed.CollectionChanged -= ItemsChanged;
            observed = Items as INotifyCollectionChanged;
            if (attached && observed != null) observed.CollectionChanged += ItemsChanged;
        }
        if (change.Property == FocusDateProperty || change.Property == ItemsProperty)
            if (days[0] != null) RefreshDays();
    }

    private bool refreshPending;
    private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (refreshPending) return;
        refreshPending = true;
        Dispatcher.UIThread.Post(() => { refreshPending = false; RefreshDays(); });
    }
    private void RefreshDays()
    {
        var selected = FocusDate?.Date ?? DateTime.Today;
        var start = MonthStart(selected);
        MonthTitle.Text = selected.ToString("MMMM yyyy");
        var entries = (Items ?? Array.Empty<CalendarItem>()).Where(x => !x.Cancelled).OrderBy(x => x.StartUtc).ToArray();
        for (var i = 0; i < days.Length; i++)
        {
            var date = start.AddDays(i);
            var items = entries.Where(x => x.StartUtc.ToLocalTime() < date.AddDays(1) && x.EndUtc.ToLocalTime() > date).ToArray();
            var stack = new StackPanel { Spacing = 3 };
            stack.Children.Add(new TextBlock { Text = date.Day.ToString(), FontWeight = Avalonia.Media.FontWeight.SemiBold });
            foreach (var item in items.Take(3))
            {
                var color = Avalonia.Media.Color.Parse(item.Kind switch { "Meeting" => "#AF7DE4", "ScheduledTask" => "#DB952C", "Vacation" => "#359B84", "PublicHoliday" => "#D66985", _ => "#6D89DC" });
                stack.Children.Add(new Border
                {
                    BorderBrush = new Avalonia.Media.SolidColorBrush(color), BorderThickness = new Thickness(3, 0, 0, 0),
                    Background = new Avalonia.Media.SolidColorBrush(color, 0.14), CornerRadius = new CornerRadius(4), Padding = new Thickness(4, 2),
                    Child = new TextBlock { Text = $"{WorkClock.Time(item.StartUtc.ToLocalTime())} {item.Title}", FontSize = 10, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis }
                });
            }
            if (items.Length > 3) stack.Children.Add(new TextBlock { Text = $"+{items.Length - 3} more", FontSize = 10 });
            days[i].Tag = date; days[i].Content = stack;
            days[i].Classes.Set("outside", date.Month != selected.Month);
            days[i].Classes.Set("selected", date == selected);
            days[i].Classes.Set("today", date == DateTime.Today);
            Avalonia.Automation.AutomationProperties.SetName(days[i], $"{date:D}, {items.Length} scheduled items");
        }
        SelectedDayTitle.Text = selected.ToString("dddd, d MMMM");
        HourlyAgenda.Children.Clear();
        for (var hour = 0; hour < 24; hour++)
        {
            var at = hour;
            var from = selected.AddHours(hour);
            var items = entries.Where(x => x.StartUtc.ToLocalTime() < from.AddHours(1) && x.EndUtc.ToLocalTime() > from).ToArray();
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("72,*"), MinHeight = 48 };
            var label = new Button { Content = WorkClock.Time(from), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
            label.Click += (_, _) => HourChoice.SelectedIndex = at;
            row.Children.Add(label);
            Control content;
            if (items.Length == 0)
            {
                var create = new Button { Content = "+ Create schedule", Classes = { "calendar-slot" }, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
                create.Click += (_, _) => CreateScheduleRequested?.Invoke(selected, TimeSpan.FromHours(at));
                content = create;
            }
            else content = new TextBlock { Text = string.Join(" · ", items.Take(5).Select(x => $"{WorkClock.Time(x.StartUtc.ToLocalTime())}–{WorkClock.Time(x.EndUtc.ToLocalTime())} {x.Title}")) + (items.Length > 5 ? $" · +{items.Length - 5} more" : ""), TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(12, 8), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            Grid.SetColumn(content, 1); row.Children.Add(content); HourlyAgenda.Children.Add(row);
        }
    }

    private void UpdateHours(){var hour=HourChoice.SelectedIndex;HourChoice.ItemsSource=Enumerable.Range(0,24).Select(x=>WorkClock.Time(DateTime.Today.AddHours(x))).ToArray();HourChoice.SelectedIndex=hour;}
    private void ClockChanged(object? sender,System.ComponentModel.PropertyChangedEventArgs e){if(e.PropertyName==nameof(UserIdentityDesktop.ViewModels.ClockDisplaySettings.TwelveHour)){UpdateHours();RefreshDays();}}
    private void Create_Click(object? sender, RoutedEventArgs e) =>
        CreateScheduleRequested?.Invoke(FocusDate?.Date ?? DateTime.Today, new TimeSpan(Math.Max(0, HourChoice.SelectedIndex), Math.Max(0, MinuteChoice.SelectedIndex), 0));

    private void Previous_Click(object? sender, RoutedEventArgs e) => MoveMonth(-1);
    private void Next_Click(object? sender, RoutedEventArgs e) => MoveMonth(1);
    private void Today_Click(object? sender, RoutedEventArgs e) => FocusDate = DateTimeOffset.Now.Date;
    private void MoveMonth(int offset) => FocusDate = new DateTimeOffset(new DateTime((FocusDate ?? DateTimeOffset.Now).Year, (FocusDate ?? DateTimeOffset.Now).Month, 1).AddMonths(offset));
    private void Day_KeyDown(object? sender, KeyEventArgs e)
    {
        var offset = e.Key switch { Key.Left => -1, Key.Right => 1, Key.Up => -7, Key.Down => 7, _ => 0 };
        if (offset == 0 || sender is not Button { Tag: DateTime date }) return;
        e.Handled = true;
        FocusDate = new DateTimeOffset(date.AddDays(offset));
        days.First(x => Equals(x.Tag, FocusDate.Value.Date)).Focus();
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        attached = true;
        UserIdentityDesktop.ViewModels.ClockDisplaySettings.Shared.PropertyChanged+=ClockChanged;
        UpdateHours();
        if (observed != null) { observed.CollectionChanged -= ItemsChanged; observed.CollectionChanged += ItemsChanged; }
        RefreshDays();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        attached = false;
        UserIdentityDesktop.ViewModels.ClockDisplaySettings.Shared.PropertyChanged-=ClockChanged;
        if (observed != null) observed.CollectionChanged -= ItemsChanged;
        base.OnDetachedFromVisualTree(e);
    }
}
