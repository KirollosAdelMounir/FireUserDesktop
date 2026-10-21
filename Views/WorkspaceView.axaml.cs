using Avalonia.Controls;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Styling;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System;
using System.Linq;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using UserIdentityDesktop.ViewModels;

namespace UserIdentityDesktop.Views;

public partial class WorkspaceView : UserControl
{
    private CancellationTokenSource? animation;
    private WorkspaceViewModel? model;
    private CallWindow? callWindow;
    internal CallWindow? ActiveCallWindow => callWindow;
    private IncomingCallWindow? incomingWindow;
    public WorkspaceView()
    {
        InitializeComponent();
        var railExit = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        NavigationRail.PointerEntered += (_, _) => { railExit.Stop(); NavigationRail.Classes.Add("expanded"); };
        NavigationRail.PointerExited += (_, _) => railExit.Start();
        railExit.Tick += (_, _) => { railExit.Stop(); if (!NavigationRail.IsPointerOver) NavigationRail.Classes.Remove("expanded"); };
        SizeChanged += (_, _) => UpdateLayoutMode();
        MonthCalendar.CreateScheduleRequested += (day, time) =>
        {
            if (model == null) return;
            model.ShowCalendarEditor = true; UpdateLayoutMode();
            model.EventDate = new DateTimeOffset(day); model.EventTime = time;
            EventTitleInput.BringIntoView(); EventTitleInput.Focus();
        };
        WorkspaceTabs.SelectionChanged += (_, e) =>
        {
            if (e.Source != WorkspaceTabs) return;
            foreach (var button in NavigationRail.GetVisualDescendants().OfType<Button>())
                button.Classes.Set("active", int.TryParse(button.Tag?.ToString(), out var index) && index == WorkspaceTabs.SelectedIndex);
            if (WorkspaceTabs.SelectedItem is TabItem { Content: Control content }) Animate(content);
        };
        DataContextChanged += (_, _) =>
        {
            if (model != null) model.PropertyChanged -= OnModelChanged;
            model = DataContext as WorkspaceViewModel;
            if (model != null) model.PropertyChanged += OnModelChanged;
            UpdateMotion(); UpdateLayoutMode();
        };
    }
    private void UpdateLayoutMode()
    {
        var compact = Bounds.Width < 1080;
        CalendarLayout.ColumnDefinitions = new ColumnDefinitions(compact || model?.ShowCalendarEditor != true ? "*" : "*,340");
        CalendarLayout.RowDefinitions = new RowDefinitions(compact ? "*,Auto" : "*");
        Grid.SetColumn(CalendarEditor, compact ? 0 : 1); Grid.SetRow(CalendarEditor, compact ? 1 : 0);
        CalendarEditor.MaxHeight = compact ? 260 : double.PositiveInfinity;
        CalendarEditor.Margin = compact ? new Thickness(0, 16, 0, 0) : new Thickness(0);
        AnnouncementsLayout.ColumnDefinitions = new ColumnDefinitions(compact || model?.ShowAnnouncementEditor != true ? "*" : "*,340");
        AnnouncementsLayout.RowDefinitions = new RowDefinitions(compact ? "Auto,*,Auto,Auto" : "Auto,*,Auto");
        Grid.SetColumn(AnnouncementEditor, compact ? 0 : 1); Grid.SetRow(AnnouncementEditor, compact ? 3 : 0);
        Grid.SetRowSpan(AnnouncementEditor, compact ? 1 : 3);
        AnnouncementEditor.MaxHeight = compact ? 240 : double.PositiveInfinity;
        AnnouncementEditor.Margin = compact ? new Thickness(0, 16, 0, 0) : new Thickness(20, 0, 0, 0);
    }
    private void NewSchedule_Click(object? sender, RoutedEventArgs e) { if (model != null) { model.SelectedWorkspaceTab = 2; model.ShowCalendarEditor = true; EventTitleInput.Focus(); } }
    private void CloseSchedule_Click(object? sender, RoutedEventArgs e) { if (model != null) model.ShowCalendarEditor = false; }
    private void ComposeAnnouncement_Click(object? sender, RoutedEventArgs e) { if (model != null) model.ShowAnnouncementEditor = !model.ShowAnnouncementEditor; }
    private void JoinMeetingCard_Click(object? sender, RoutedEventArgs e)
    {
        if (model != null && sender is Control { DataContext: UserIdentityDesktop.Core.Models.CalendarItem item }) { model.SelectedCalendar = item; model.JoinSelectedMeetingCommand.Execute(null); }
    }
    private void Vacations_Expanded(object? sender, RoutedEventArgs e) => model?.RefreshVacationsCommand.Execute(null);
    private void Navigate_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && int.TryParse(button.Tag?.ToString(), out var index))
        {
            WorkspaceTabs.SelectedIndex = index;
            if (index == 4) model?.RefreshDevicesCommand.Execute(null);
        }
    }
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkspaceViewModel.ShowCalendarEditor) or nameof(WorkspaceViewModel.ShowAnnouncementEditor)) UpdateLayoutMode();
        if (e.PropertyName == nameof(WorkspaceViewModel.InCall)) UpdateCallWindow();
        if (e.PropertyName == nameof(WorkspaceViewModel.Incoming))
        {
            incomingWindow?.Close(); incomingWindow = null;
            if (model?.Incoming != null) { incomingWindow = new IncomingCallWindow(model); incomingWindow.Show(); }
        }
        if (e.PropertyName == nameof(WorkspaceViewModel.ReduceMotion)) { if (model?.ReduceMotion == true) animation?.Cancel(); UpdateMotion(); }
    }
    private void UpdateMotion()
    {
        if (model?.ReduceMotion == true) NavigationRail.Transitions = null;
        else NavigationRail.ClearValue(TransitionsProperty);
    }
    private void UpdateCallWindow()
    {
        if (model?.InCall == true)
        {
            if (callWindow != null) { callWindow.Activate(); return; }
            if (TopLevel.GetTopLevel(this) is not Window owner) return;
            var window = new CallWindow { DataContext = model };
            callWindow = window;
            window.Closed += (_, _) => { if (callWindow == window) callWindow = null; };
            // Independent top-level window: the main app remains independently minimizable.
            EventHandler? ownerClosed = null;
            ownerClosed = (_, _) => { window.CloseAfterCall(); owner.Closed -= ownerClosed; };
            owner.Closed += ownerClosed;
            window.Closed += (_, _) => owner.Closed -= ownerClosed;
            window.Show();
        }
        else callWindow?.CloseAfterCall();
    }
    private async void Animate(Control content)
    {
        animation?.Cancel(); animation?.Dispose(); animation = null;
        if (model?.ReduceMotion != false) return;
        var stop = new CancellationTokenSource(); animation = stop;
        var fade = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(240),
            Easing = new AngularNavigationEasing(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0d), new Setter(Avalonia.Media.TranslateTransform.YProperty, 10d) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1d), new Setter(Avalonia.Media.TranslateTransform.YProperty, 0d) } }
            }
        };
        try { await fade.RunAsync(content, stop.Token); }
        catch (OperationCanceledException) { }
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        animation?.Cancel(); animation?.Dispose(); animation = null;
        incomingWindow?.Close(); incomingWindow = null;
        if (model != null) model.PropertyChanged -= OnModelChanged;
        base.OnDetachedFromVisualTree(e);
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (model != null) { model.PropertyChanged -= OnModelChanged; model.PropertyChanged += OnModelChanged; }
        Avalonia.Threading.Dispatcher.UIThread.Post(UpdateCallWindow);
    }
}
