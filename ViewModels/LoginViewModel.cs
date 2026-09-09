using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UserIdentityDesktop.Core;

namespace UserIdentityDesktop.ViewModels;

public partial class LoginViewModel : ViewModelBase
{
    private readonly IAuthService _authService;
    private readonly IConfigurationService _configService;

    [ObservableProperty]
    private string _email = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _serverUrl = string.Empty;

    [ObservableProperty]
    private bool _showServerConfig;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    public event Action? LoginSucceeded;

    public LoginViewModel(IAuthService authService, IConfigurationService configService)
    {
        _authService = authService;
        _configService = configService;

        _email = _configService.SavedEmail ?? string.Empty;
        _serverUrl = _configService.ServerUrl;
    }

    [RelayCommand]
    private void ToggleServerConfig()
    {
        ShowServerConfig = !ShowServerConfig;
    }

    [RelayCommand]
    public async Task LoginAsync()
    {
        if (string.IsNullOrWhiteSpace(Email))
        {
            ErrorMessage = "Please enter your email.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Please enter your password.";
            return;
        }

        ErrorMessage = null;
        IsLoading = true;

        try
        {
            _configService.ServerUrl = ServerUrl;

            var (success, error) = await _authService.LoginAsync(Email.Trim(), Password);
            if (success)
            {
                LoginSucceeded?.Invoke();
            }
            else
            {
                ErrorMessage = error ?? "Login failed. Please check your credentials.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
