using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UserIdentityDesktop.Core;
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

    [ObservableProperty]
    private ViewModelBase _currentView;

    public bool IsChatView => CurrentView is ChatViewModel;

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
        INotificationService notificationService)
    {
        _configService = configService;
        _authService = authService;
        _chatService = chatService;
        _userService = userService;
        _imageService = imageService;
        _fileService = fileService;
        _notificationService = notificationService;

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

    private void OnLoginSucceeded()
    {
        var chatVm = new ChatViewModel(_authService, _chatService, _userService, _imageService, _fileService, _notificationService);
        chatVm.LogoutRequested += OnLogoutRequested;
        _chatViewModel = chatVm;
        _chatViewModel.PropertyChanged += OnChatPropertyChanged;
        SetCurrentView(chatVm);
        _ = chatVm.InitializeAsync();
    }

    private void OnLogoutRequested()
    {
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
        if (CurrentView is ChatViewModel chatViewModel)
        {
            await chatViewModel.LogoutAsync();
        }
    }

    public async Task OpenConversationAsync(Guid userId)
    {
        if (_chatViewModel == null)
            return;

        var profile = await _userService.GetUserProfileAsync(userId);
        var name = profile?.FullName;
        if (string.IsNullOrWhiteSpace(name))
            name = "Conversation";

        Dispatcher.UIThread.Post(() => _chatViewModel?.StartChatWithUser(userId, name, null));
    }

    [RelayCommand]
    public void ToggleTheme()
    {
        IsDarkMode = !IsDarkMode;
        _configService.IsDarkMode = IsDarkMode;
        ApplyTheme(IsDarkMode);
    }

    private void ApplyTheme(bool isDark)
    {
        if (Avalonia.Application.Current != null)
        {
            Avalonia.Application.Current.RequestedThemeVariant = isDark ? ThemeVariant.Dark : ThemeVariant.Light;
        }
    }
}
