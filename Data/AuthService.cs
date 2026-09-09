using System;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UserIdentityDesktop.Core.Models;

using UserIdentityDesktop.Core;
namespace UserIdentityDesktop.Data;

public class AuthService : IAuthService
{
    private readonly IConfigurationService _configService;
    private readonly HttpClient _httpClient;
    private Timer? _refreshTimer;

    public UserData? CurrentUser { get; private set; }
    public string? AccessToken { get; private set; }
    public string? RefreshToken { get; private set; }
    public bool IsAuthenticated => CurrentUser != null && !string.IsNullOrEmpty(AccessToken);

    public event Action<UserData?>? CurrentUserChanged;
    public event Action<bool>? AuthenticationStateChanged;

    public AuthService(IConfigurationService configService)
    {
        _configService = configService;
        _httpClient = new HttpClient();
    }

    public async Task<(bool Success, string? ErrorMessage)> LoginAsync(string email, string password)
    {
        try
        {
            var url = $"{_configService.ServerUrl}/api/User/Login";
            var request = new LoginRequest { Email = email, Password = password };

            var response = await _httpClient.PostAsJsonAsync(url, request);

            if (response.IsSuccessStatusCode)
            {
                var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>();
                if (tokens != null && !string.IsNullOrEmpty(tokens.AccessToken))
                {
                    SetTokens(tokens.AccessToken, tokens.RefreshToken);
                    _configService.SavedEmail = email;
                    _configService.SavedRefreshToken = tokens.RefreshToken;
                    return (true, null);
                }
                return (false, "Failed to parse login response.");
            }

            var errorBody = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(errorBody))
            {
                errorBody = $"Server returned {(int)response.StatusCode}: {response.ReasonPhrase}";
            }
            return (false, errorBody);
        }
        catch (Exception ex)
        {
            return (false, $"Connection error: {ex.Message}");
        }
    }

    public async Task<bool> RefreshTokenAsync()
    {
        var refresh = RefreshToken ?? _configService.SavedRefreshToken;
        if (string.IsNullOrEmpty(refresh))
            return false;

        try
        {
            var url = $"{_configService.ServerUrl}/api/User/RefreshToken";
            var content = new StringContent(JsonSerializer.Serialize(refresh), Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(url, content);
            if (response.IsSuccessStatusCode)
            {
                var newAccessToken = await response.Content.ReadAsStringAsync();
                newAccessToken = newAccessToken?.Trim('"') ?? string.Empty;

                if (!string.IsNullOrEmpty(newAccessToken))
                {
                    SetTokens(newAccessToken, refresh);
                    return true;
                }
            }

            await LogoutAsync();
            return false;
        }
        catch
        {
            return false;
        }
    }

    public async Task LogoutAsync()
    {
        StopRefreshTimer();

        var refresh = RefreshToken;
        if (!string.IsNullOrEmpty(refresh))
        {
            try
            {
                var url = $"{_configService.ServerUrl}/api/ProfileSettings/LogOut";
                var content = new StringContent(JsonSerializer.Serialize(refresh), Encoding.UTF8, "application/json");
                await _httpClient.PostAsync(url, content);
            }
            catch
            {
                // ignore
            }
        }

        AccessToken = null;
        RefreshToken = null;
        CurrentUser = null;
        _configService.SavedRefreshToken = null;

        CurrentUserChanged?.Invoke(null);
        AuthenticationStateChanged?.Invoke(false);
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = new HttpClient();
        if (!string.IsNullOrEmpty(AccessToken))
        {
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", AccessToken);
        }
        return client;
    }

    private void SetTokens(string accessToken, string refreshToken)
    {
        AccessToken = accessToken;
        RefreshToken = refreshToken;

        CurrentUser = ParseUserDataFromToken(accessToken);
        CurrentUserChanged?.Invoke(CurrentUser);
        AuthenticationStateChanged?.Invoke(IsAuthenticated);

        ScheduleTokenRefresh(accessToken);
    }

    private UserData? ParseUserDataFromToken(string token)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var jwt = handler.ReadJwtToken(token);

            var nameId = jwt.Claims.FirstOrDefault(c => c.Type == "nameid" || c.Type == ClaimTypes.NameIdentifier)?.Value;
            var firstName = jwt.Claims.FirstOrDefault(c => c.Type == "FirstName")?.Value ?? "";
            var lastName = jwt.Claims.FirstOrDefault(c => c.Type == "LastName")?.Value ?? "";
            var email = jwt.Claims.FirstOrDefault(c => c.Type == "Email" || c.Type == ClaimTypes.Email)?.Value ?? "";
            var role = jwt.Claims.FirstOrDefault(c => c.Type == "role" || c.Type == ClaimTypes.Role)?.Value ?? "";
            var dobClaim = jwt.Claims.FirstOrDefault(c => c.Type == "DateOfBirth")?.Value;

            DateTime? dob = null;
            if (!string.IsNullOrEmpty(dobClaim) && DateTime.TryParse(dobClaim, out var parsedDob))
                dob = parsedDob;

            if (Guid.TryParse(nameId, out var userId))
            {
                return new UserData
                {
                    Id = userId,
                    FirstName = firstName,
                    LastName = lastName,
                    Email = email,
                    Role = role,
                    DateOfBirth = dob
                };
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error decoding token: {ex.Message}");
        }

        return null;
    }

    private void ScheduleTokenRefresh(string token)
    {
        StopRefreshTimer();

        try
        {
            var handler = new JwtSecurityTokenHandler();
            var jwt = handler.ReadJwtToken(token);
            var exp = jwt.ValidTo;
            var remaining = exp - DateTime.UtcNow;

            // Refresh 2 minutes before expiry or halfway if short-lived
            var refreshDelay = remaining > TimeSpan.FromMinutes(3)
                ? remaining - TimeSpan.FromMinutes(2)
                : remaining * 0.7;

            if (refreshDelay < TimeSpan.FromSeconds(10))
                refreshDelay = TimeSpan.FromSeconds(10);

            _refreshTimer = new Timer(async _ =>
            {
                await RefreshTokenAsync();
            }, null, refreshDelay, Timeout.InfiniteTimeSpan);
        }
        catch
        {
            // ignore timer schedule error
        }
    }

    private void StopRefreshTimer()
    {
        _refreshTimer?.Dispose();
        _refreshTimer = null;
    }
}
