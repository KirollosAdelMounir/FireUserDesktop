using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using UserIdentityDesktop.Core.Models;

namespace UserIdentityDesktop.ViewModels;

public partial class ScreenSharePickerViewModel : ViewModelBase
{
    public IReadOnlyList<ScreenShareSource> Screens { get; }
    public IReadOnlyList<ScreenShareSource> Windows { get; }
    public bool UsesPortal { get; }
    public string Hint => UsesPortal ? "Your desktop will ask you to select and approve the screen or window next." : "Only the selected screen or window will be shared.";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SelectedSource)), NotifyPropertyChangedFor(nameof(CanShare))] private int _selectedTab;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SelectedSource)), NotifyPropertyChangedFor(nameof(CanShare))] private ScreenShareSource? _selectedScreen;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SelectedSource)), NotifyPropertyChangedFor(nameof(CanShare))] private ScreenShareSource? _selectedWindow;
    public ScreenShareSource? SelectedSource => SelectedTab == 0 ? SelectedScreen : SelectedWindow;
    public bool CanShare => SelectedSource != null;
    public ScreenSharePickerViewModel(IReadOnlyList<ScreenShareSource> sources)
    {
        Screens = sources.Where(x => x.Kind == ScreenShareKind.Screen).ToArray(); Windows = sources.Where(x => x.Kind == ScreenShareKind.Window).ToArray();
        UsesPortal = sources.Any(x => x.Backend == ScreenCaptureBackend.Portal);
        SelectedScreen = Screens.FirstOrDefault(); SelectedWindow = Windows.FirstOrDefault();
        SelectedTab = Screens.Count > 0 ? 0 : 1;
    }
}
