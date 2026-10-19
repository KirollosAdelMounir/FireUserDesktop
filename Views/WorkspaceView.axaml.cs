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
    public WorkspaceView()
    {
        InitializeComponent();
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
            UpdateMotion();
        };
    }
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
        if (e.PropertyName == nameof(WorkspaceViewModel.InCall)) UpdateCallWindow();
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
            window.Show(owner);
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
