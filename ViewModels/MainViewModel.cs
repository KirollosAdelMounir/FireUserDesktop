using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UserIdentityDesktop.Core;
using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.Data;
using UserIdentityDesktop.Service;

namespace UserIdentityDesktop.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly IConfigurationService _configService;
    private readonly IAuthService _authService;
    private readonly IChatService _chatService;
    private readonly IUserService _userService;
    private readonly IImageService _imageService;
    private readonly IFileService _fileService;
    private readonly INotificationService _notificationService;
    private ChatViewModel? _chatViewModel;
    private WorkspaceViewModel? _workspace;
    private string? _pendingMeetingLocator;
    private readonly ICollaborationApi _collaboration;
    private readonly INativeMediaSession _media;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Chat)), NotifyPropertyChangedFor(nameof(IsChatView))]
    private ViewModelBase _currentView;

    public ChatViewModel? Chat => _chatViewModel ?? (CurrentView as WorkspaceViewModel)?.Chat;

    public bool IsChatView => CurrentView is WorkspaceViewModel;

    public string IdentitySearchText
    {
        get => _chatViewModel?.IdentitySearchText ?? string.Empty;
        set
        {
            if (_chatViewModel != null)
                _chatViewModel.IdentitySearchText = value;
        }
    }

    public bool ShowSearchDropdown => _chatViewModel?.ShowSearchDropdown == true;

    [ObservableProperty]
    private bool _isDarkMode;

    public MainViewModel(
        IConfigurationService configService,
        IAuthService authService,
        IChatService chatService,
        IUserService userService,
        IImageService imageService,
        IFileService fileService,
        INotificationService notificationService,
        ICollaborationApi collaboration,
        INativeMediaSession media)
    {
        _configService = configService;
        _authService = authService;
        _chatService = chatService;
        _userService = userService;
        _imageService = imageService;
        _fileService = fileService;
        _notificationService = notificationService;
        _collaboration = collaboration;
        _media = media;

        _isDarkMode = _configService.IsDarkMode;
        ApplyTheme(_isDarkMode);

        var loginVm = new LoginViewModel(_authService, _configService);
        loginVm.LoginSucceeded += OnLoginSucceeded;
        _currentView = loginVm;

        _ = TryAutoLoginAsync();
    }

    // Parameterless constructor for XAML design-time preview
    public MainViewModel()
    {
        _configService = new ConfigurationService();
        _authService = new AuthService(_configService);
        _imageService = new ImageService(_authService, _configService);
        _chatService = new ChatService(_authService, _configService);
        _userService = new UserService(_authService, _configService);
        _fileService = new FileService();
        _notificationService = new SystemNotificationService();
        _collaboration = new CollaborationApi(_authService, _configService);
        _media = new UserIdentityDesktop.Service.Media.NativeMediaSession();

        _isDarkMode = true;
        _currentView = new LoginViewModel(_authService, _configService);
    }

    private async Task TryAutoLoginAsync()
    {
        if (!string.IsNullOrEmpty(_configService.SavedRefreshToken))
        {
            var refreshed = await _authService.RefreshTokenAsync();
            if (refreshed)
            {
                OnLoginSucceeded();
            }
        }
    }
    private string? pendingNotification;
    public void DeferNotification(string target) => pendingNotification = target;

    private void OnLoginSucceeded()
    {
        var chatVm = new ChatViewModel(_authService, _chatService, _userService, _imageService, _fileService, _notificationService);
        chatVm.LogoutRequested += OnLogoutRequested;
        _chatViewModel = chatVm;
        _chatViewModel.PropertyChanged += OnChatPropertyChanged;
        _workspace = new WorkspaceViewModel(chatVm, _collaboration, _media, _authService, _chatService, _notificationService);
        chatVm.BeforeLogoutAsync = async () => { if (_workspace != null) await _workspace.DisposeAsync(); };
        SetCurrentView(_workspace);
        OpenPendingMeeting();
        if (pendingNotification is {} target) { pendingNotification = null; _ = _workspace.OpenNotificationTargetAsync(target); }
        _ = chatVm.InitializeAsync();
        _ = _workspace.RefreshAsync();
    }

    private async void OnLogoutRequested()
    {
        if (_workspace != null) await _workspace.DisposeAsync();
        _workspace = null;
        var loginVm = new LoginViewModel(_authService, _configService);
        loginVm.LoginSucceeded += OnLoginSucceeded;
        if (_chatViewModel != null)
            _chatViewModel.PropertyChanged -= OnChatPropertyChanged;
        _chatViewModel = null;
        SetCurrentView(loginVm);
    }

    private void SetCurrentView(ViewModelBase viewModel)
    {
        CurrentView = viewModel;
        OnPropertyChanged(nameof(Chat));
        OnPropertyChanged(nameof(IsChatView));
        OnPropertyChanged(nameof(IdentitySearchText));
        OnPropertyChanged(nameof(ShowSearchDropdown));
    }

    private void OnChatPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatViewModel.IdentitySearchText) or nameof(ChatViewModel.ShowSearchDropdown))
        {
            OnPropertyChanged(e.PropertyName);
        }
    }

    [RelayCommand]
    public void ClearIdentitySearch()
    {
        _chatViewModel?.ClearIdentitySearch();
    }

    public async Task LogoutAsync()
    {
        if (_chatViewModel is { } chatViewModel)
        {
            if (_workspace != null) await _workspace.DisposeAsync();
            await chatViewModel.LogoutAsync();
        }
    }

    public async Task ShutdownAsync()
    {
        if (_workspace != null) await _workspace.DisposeAsync();
        await _chatService.DisconnectAsync();
        await _userService.DisconnectAsync();
    }

    public void OpenMeeting(string locator)
    {
        if (!MeetingLink.IsValidLocator(locator)) return;
        // Keep only the latest navigation while sign-in is required. No polling or
        // credentials are needed; ordinary login completion consumes it once.
        _pendingMeetingLocator = locator;
        OpenPendingMeeting();
    }

    private void OpenPendingMeeting()
    {
        if (_workspace == null || _pendingMeetingLocator == null) return;
        var locator = _pendingMeetingLocator; _pendingMeetingLocator = null;
        _workspace.OpenMeeting(locator);
    }

    public async Task OpenConversationAsync(Guid userId)
    {
        if (_chatViewModel == null)
            return;

        var profile = await _userService.GetUserProfileAsync(userId);
        var name = profile?.FullName;
        if (string.IsNullOrWhiteSpace(name))
            name = "Conversation";

        Dispatcher.UIThread.Post(() => { if (_workspace != null) _workspace.SelectedWorkspaceTab = 0; _chatViewModel?.StartChatWithUser(userId, name, null); });
    }

    [RelayCommand]
    public void ToggleTheme()
    {
        if(_workspace is {} workspace){workspace.Appearance.Brightness=workspace.Appearance.Brightness<50?100:0;return;}
        IsDarkMode = !IsDarkMode;
        _configService.IsDarkMode = IsDarkMode;
        ApplyTheme(IsDarkMode);
    }

    private void ApplyTheme(bool isDark)
    {
        if (Avalonia.Application.Current != null)
        {
            NativeAppearance.Apply(isDark?0:100);
        }
    }
}
