using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UserIdentityDesktop.Core.Models;
using Avalonia.Input;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UserIdentityDesktop.ViewModels;

namespace UserIdentityDesktop.Views;

public partial class CallWindow : Window
{
    private bool closingAfterCall, leaving, presentationFullscreen;
    private WindowState previousWindowState;
    private bool previousMembers, previousChat;
    private WorkspaceViewModel? observedModel;
    public CallWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateGalleryLayout();
        AddHandler(KeyDownEvent, PresentationKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) =>
        {
            ExitShareFullscreen();
            if (observedModel != null) { observedModel.PropertyChanged -= OnPresentationChanged; observedModel.ChooseScreenShareSourceAsync = null; }
            observedModel = DataContext as WorkspaceViewModel;
            if (observedModel != null) { observedModel.PropertyChanged += OnPresentationChanged; observedModel.ChooseScreenShareSourceAsync = ChooseScreenShareSource; }
            UpdateGalleryLayout();
        };
        PropertyChanged += (_, e) => { if (e.Property == WindowStateProperty && presentationFullscreen && WindowState != WindowState.FullScreen) ExitShareFullscreen(restoreWindowState: false); };
        Closed += (_, _) => { if (observedModel != null) { observedModel.PropertyChanged -= OnPresentationChanged; observedModel.ChooseScreenShareSourceAsync = null; } };
        Closing += async (_, args) =>
        {
            if (closingAfterCall || DataContext is not WorkspaceViewModel { InCall: true } vm) return;
            args.Cancel = true;
            if (leaving) return;
            leaving = true;
            try { await vm.LeaveCommand.ExecuteAsync(null); }
            catch (Exception ex) { vm.Feedback = ex.Message; }
            finally { CloseAfterCall(); }
        };
    }
    private async Task<ScreenShareSource?> ChooseScreenShareSource(IReadOnlyList<ScreenShareSource> discovered)
    {
        var sources = discovered.ToList();
        if (!OperatingSystem.IsMacOS() && !sources.Any(x => x.Backend == ScreenCaptureBackend.Portal))
        {
            var backend = OperatingSystem.IsWindows() ? ScreenCaptureBackend.Windows : ScreenCaptureBackend.X11;
            var index = 0;
            foreach (var screen in Screens.All)
            {
                var bounds = screen.Bounds;
                sources.Insert(index, new("monitor-" + index, $"Screen {++index} · {bounds.Width} × {bounds.Height}", ScreenShareKind.Screen, backend, bounds.X, bounds.Y, bounds.Width, bounds.Height));
            }
        }
        var picker = new ScreenSharePicker { DataContext = new ScreenSharePickerViewModel(sources), RequestedThemeVariant = ActualThemeVariant };
        return await picker.ShowDialog<ScreenShareSource?>(this);
    }
    private void Fullscreen_Click(object? sender, RoutedEventArgs e) => ToggleShareFullscreen();
    private void ToggleShareFullscreen()
    {
        if (presentationFullscreen) { ExitShareFullscreen(); return; }
        if (DataContext is not WorkspaceViewModel { HasFocusedScreen: true } vm) return;
        previousWindowState = WindowState; previousMembers = vm.ShowCallMembers; previousChat = vm.ShowCallChat;
        vm.ShowCallMembers = vm.ShowCallChat = false;
        presentationFullscreen = true; Classes.Add("share-fullscreen"); WindowState = WindowState.FullScreen;
    }
    private void ExitShareFullscreen(bool restoreWindowState = true)
    {
        if (!presentationFullscreen) return;
        presentationFullscreen = false; Classes.Remove("share-fullscreen");
        if (restoreWindowState) WindowState = previousWindowState;
        if (observedModel != null) { observedModel.ShowCallMembers = previousMembers; observedModel.ShowCallChat = previousChat; }
    }
    private void PresentationKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None) return;
        if (e.Key == Key.Escape && presentationFullscreen) { ExitShareFullscreen(); e.Handled = true; }
        else if (e.Key == Key.F11 && (presentationFullscreen || observedModel?.HasFocusedScreen == true)) { ToggleShareFullscreen(); e.Handled = true; }
    }
    private void OnPresentationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkspaceViewModel.ParticipantSummary) or nameof(WorkspaceViewModel.ShowCallMembers) or nameof(WorkspaceViewModel.ShowCallChat)) UpdateGalleryLayout();
        if (e.PropertyName == nameof(WorkspaceViewModel.HasFocusedScreen) && observedModel?.HasFocusedScreen != true) ExitShareFullscreen();
    }
    private void UpdateGalleryLayout()
    {
        if (observedModel == null) return;
        var available = (Bounds.Width > 0 ? Bounds.Width : Width) - 40 - (observedModel.ShowCallMembers ? 266 : 0) - (observedModel.ShowCallChat ? 318 : 0);
        observedModel.GalleryColumns = Math.Max(1, Math.Min(Math.Max(1, observedModel.Videos.Count), Math.Min(3, (int)(available / 380))));
    }
    private void FocusedTile_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is Control target && observedModel?.FocusedVideo is {} tile) { OpenTileMenu(target,tile,e.TryGetPosition(target,out _)); e.Handled = true; }
    }
    private void Tile_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is Control { DataContext: VideoTileViewModel tile } target) { OpenTileMenu(target,tile,true); e.Handled = true; }
    }
    private void TileMenu_Click(object? sender, RoutedEventArgs e) { if (sender is Control { DataContext: VideoTileViewModel tile } target) OpenTileMenu(target,tile,false); }
    private void OpenTileMenu(Control target, VideoTileViewModel tile, bool atPointer)
    {
        if (observedModel == null) return;
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = tile.IsScreen ? "Focus screen share" : "Focus participant", Command = observedModel.FocusVideoCommand, CommandParameter = tile });
        if (tile.IsScreen) menu.Items.Add(new MenuItem { Header = presentationFullscreen ? "Exit full screen" : "Full screen", Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => { if (presentationFullscreen) ExitShareFullscreen(); else { observedModel.FocusVideoCommand.Execute(tile); ToggleShareFullscreen(); } }) });
        menu.Items.Add(new MenuItem { Header = "Return to gallery", Command = observedModel.ClearVideoFocusCommand });
        MessageMenus.Open(target,menu,atPointer);
    }
    private void CallMessage_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (observedModel != null && sender is Control { DataContext: ChatMessageItemViewModel message } target)
        { MessageMenus.Open(target,MessageMenus.Create(observedModel.Chat,message,target),true); e.Handled = true; }
    }
    private void Tile_Pressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (e.Source is Control origin && (origin is Button || origin.FindAncestorOfType<Button>() != null)) return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && DataContext is WorkspaceViewModel vm && sender is Control { DataContext: VideoTileViewModel tile }) vm.FocusVideoCommand.Execute(tile);
    }
    private void Devices_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel vm) vm.RefreshDevicesCommand.Execute(null);
    }
    public void CloseAfterCall() { closingAfterCall = true; Dispatcher.UIThread.Post(() => { if (IsVisible) Close(); }); }
}
