using Avalonia.Controls;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Styling;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System;
using System.Linq;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using UserIdentityDesktop.ViewModels;

namespace UserIdentityDesktop.Views;

public partial class WorkspaceView : UserControl
{
    private WorkspaceViewModel? model;
    private CallWindow? callWindow;
    internal CallWindow? ActiveCallWindow => callWindow;
    private IncomingCallWindow? incomingWindow;
    private readonly SurfaceMotion calendarMotion = new();
    private int calendarGeneration;
    private readonly Dictionary<WorkspaceFormKind, WorkspaceFormWindow> editorWindows = new();
    internal IReadOnlyCollection<WorkspaceFormWindow> EditorWindows => editorWindows.Values;
    internal Func<Window?, string, Task> ShowSubmissionMessageAsync { get; set; } = SubmissionMessage.ShowAsync;
    public WorkspaceView()
    {
        InitializeComponent();
        CalendarTabs.SelectionChanged += (_, e) => { if (e.Source == CalendarTabs) AnimateCalendarTab(); };
        CalendarTabs.SizeChanged += (_, _) => UpdateCalendarFocus();
        NavigationRail.PointerEntered += (_, _) => NavigationRail.Classes.Add("expanded");
        NavigationRail.PointerExited += (_, _) => NavigationRail.Classes.Remove("expanded");
        NavigationRail.GotFocus += (_, e) => { if (e.NavigationMethod is Avalonia.Input.NavigationMethod.Tab or Avalonia.Input.NavigationMethod.Directional) NavigationRail.Classes.Add("expanded"); };
        NavigationRail.LostFocus += (_, _) => { if (!NavigationRail.IsPointerOver) NavigationRail.Classes.Remove("expanded"); };
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
        };
        DataContextChanged += (_, _) =>
        {
            if (model != null) { model.PropertyChanged -= OnModelChanged; model.SubmissionCompleted -= OnSubmissionCompleted; }
            CloseEditorWindows();
            model = DataContext as WorkspaceViewModel;
            if (model != null) { model.PropertyChanged += OnModelChanged; model.SubmissionCompleted += OnSubmissionCompleted; }
            UpdateMotion(); UpdateLayoutMode();
        };
    }
    private void UpdateCalendarFocus()
    {
        if (CalendarTabs.SelectedItem is not TabItem item || item.TranslatePoint(new Point(), CalendarTabs) is not { } point) return;
        if (SurfaceMotion.Reduced(this)) CalendarTabFocus.Transitions = null;
        else CalendarTabFocus.ClearValue(TransitionsProperty);
        CalendarTabFocus.Width = item.Bounds.Width;
        CalendarTabFocus.Margin = new Thickness(point.X, 0, 0, 0);
    }
    private void AnimateCalendarTab()
    {
        calendarMotion.Cancel();
        var generation = ++calendarGeneration;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (generation != calendarGeneration || !this.IsAttachedToVisualTree()) return;
            UpdateCalendarFocus();
            _ = calendarMotion.EnterAsync(model?.IsVacationTab == true ? VacationPage : CalendarBrowser, SurfaceMotion.Reduced(this));
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }
    private void UpdateLayoutMode()
    {
        CalendarLayout.ColumnDefinitions = new ColumnDefinitions("*");
        CalendarLayout.RowDefinitions = new RowDefinitions("Auto,*");
        CalendarBrowser.Margin = new Thickness(0);
        AnnouncementsLayout.ColumnDefinitions = new ColumnDefinitions("*");
        AnnouncementsLayout.RowDefinitions = new RowDefinitions("Auto,*,Auto");
    }
    private void UpdateEditorWindow(WorkspaceFormKind kind, bool open)
    {
        if (!open) { if (editorWindows.TryGetValue(kind, out var closing)) _ = closing.CloseAnimatedAsync(); return; }
        if (editorWindows.TryGetValue(kind, out var existing)) { existing.Activate(); return; }
        if (model == null || TopLevel.GetTopLevel(this) is not Window owner) return;
        var vm = model;
        var editor = kind switch { WorkspaceFormKind.Calendar => CalendarEditor, WorkspaceFormKind.Vacation => VacationEditor, _ => AnnouncementEditor };
        var parent = editor.Parent as Panel;
        parent?.Children.Remove(editor); vm.Feedback = "";
        var window = new WorkspaceFormWindow(editor, vm, kind) { RequestedThemeVariant = owner.ActualThemeVariant };
        if (owner.Screens.ScreenFromWindow(owner) is {} screen) window.MaxHeight = Math.Max(400, screen.WorkingArea.Height / screen.Scaling - 48);
        editorWindows[kind] = window;
        window.Closed += (_, _) =>
        {
            if (editor.Parent is Panel layout) layout.Children.Remove(editor);
            editor.IsVisible = false; parent?.Children.Add(editor); editorWindows.Remove(kind);
            SetEditorFlag(vm, kind, false);
        };
        _ = window.ShowDialog(owner);
    }
    private static void SetEditorFlag(WorkspaceViewModel vm, WorkspaceFormKind kind, bool value)
    {
        switch (kind) { case WorkspaceFormKind.Calendar: vm.ShowCalendarEditor = value; break; case WorkspaceFormKind.Vacation: vm.ShowVacationEditor = value; break; default: vm.ShowAnnouncementEditor = value; break; }
    }
    private void CloseEditorWindows()
    {
        foreach (var window in editorWindows.Values.ToArray()) window.Close();
    }
    private async void OnSubmissionCompleted(WorkspaceFormKind kind, string message)
    {
        try
        {
            if (editorWindows.TryGetValue(kind, out var window)) await window.CloseAnimatedAsync();
            if (this.IsAttachedToVisualTree()) await ShowSubmissionMessageAsync(TopLevel.GetTopLevel(this) as Window, message);
        }
        catch (Exception ex) { if (model != null) model.Feedback = ex.Message; }
    }
    private void NewVacation_Click(object? sender, RoutedEventArgs e) { if (model != null) model.ShowVacationEditor = true; }
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
        if (e.PropertyName == nameof(WorkspaceViewModel.ShowCalendarEditor)) UpdateEditorWindow(WorkspaceFormKind.Calendar, model?.ShowCalendarEditor == true);
        if (e.PropertyName == nameof(WorkspaceViewModel.ShowVacationEditor)) UpdateEditorWindow(WorkspaceFormKind.Vacation, model?.ShowVacationEditor == true);
        if (e.PropertyName == nameof(WorkspaceViewModel.ShowAnnouncementEditor)) UpdateEditorWindow(WorkspaceFormKind.Announcement, model?.ShowAnnouncementEditor == true);
        if (e.PropertyName is nameof(WorkspaceViewModel.InCall) or nameof(WorkspaceViewModel.ConnectingCall)) UpdateCallWindow();
        if (e.PropertyName == nameof(WorkspaceViewModel.Incoming))
        {
            incomingWindow?.Close(); incomingWindow = null;
            if (model?.Incoming != null) { incomingWindow = new IncomingCallWindow(model); incomingWindow.Show(); }
        }
        if (e.PropertyName == nameof(WorkspaceViewModel.ReduceMotion)) { if (model?.ReduceMotion == true) foreach (var tabs in this.GetVisualDescendants().OfType<TabControl>()) TabMotion.Stop(tabs); UpdateMotion(); }
    }
    private void UpdateMotion()
    {
        if (model?.ReduceMotion == true) NavigationRail.Transitions = null;
        else NavigationRail.ClearValue(TransitionsProperty);
    }
    private void UpdateCallWindow()
    {
        if (model?.CallWindowVisible == true)
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
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        foreach (var tabs in this.GetVisualDescendants().OfType<TabControl>()) TabMotion.Stop(tabs);
        calendarGeneration++; calendarMotion.Cancel();
        incomingWindow?.Close(); incomingWindow = null;
        CloseEditorWindows();
        if (model != null) { model.PropertyChanged -= OnModelChanged; model.SubmissionCompleted -= OnSubmissionCompleted; }
        base.OnDetachedFromVisualTree(e);
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (model != null) { model.PropertyChanged -= OnModelChanged; model.PropertyChanged += OnModelChanged; model.SubmissionCompleted -= OnSubmissionCompleted; model.SubmissionCompleted += OnSubmissionCompleted; }
        Avalonia.Threading.Dispatcher.UIThread.Post(UpdateCallWindow);
        Avalonia.Threading.Dispatcher.UIThread.Post(UpdateCalendarFocus, Avalonia.Threading.DispatcherPriority.Loaded);
    }
}
