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
    private static readonly SocketsHttpHandler authenticatedTransport = new() { PooledConnectionLifetime = TimeSpan.FromMinutes(5), ConnectTimeout = TimeSpan.FromSeconds(10), UseCookies = false };
    private readonly IConfigurationService _configService;
    private readonly HttpClient _httpClient;
    public IBackendActionQueue Actions { get; }
    private Timer? _refreshTimer;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public UserData? CurrentUser { get; private set; }
    public string? AccessToken { get; private set; }
    public string? RefreshToken { get; private set; }
    public bool IsAuthenticated => CurrentUser != null && !string.IsNullOrEmpty(AccessToken);

    public event Action<UserData?>? CurrentUserChanged;
    public event Action<bool>? AuthenticationStateChanged;

    public AuthService(IConfigurationService configService, HttpClient? httpClient = null, IBackendActionQueue? actions=null)
    {
        Actions=actions??new BackendActionQueue();
        _configService = configService;
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<(bool Success, string? ErrorMessage)> LoginAsync(string email, string password)
    {
        try
        {
            var url = $"{_configService.ServerUrl}/api/User/Login";
            var request = new LoginRequest { Email = email, Password = password };

            using var response = await _httpClient.PostAsJsonAsync(url, request);

            if (response.IsSuccessStatusCode)
            {
                var tokens = await ApiPayload.ReadAsync<TokenResponse>(response);
                if (tokens != null && !string.IsNullOrEmpty(tokens.AccessToken))
                {
                    SetTokens(tokens.AccessToken, tokens.RefreshToken);
                    _configService.SavedEmail = email;
                    _configService.SavedRefreshToken = tokens.RefreshToken;
                    return (true, null);
                }
                return (false, "Failed to parse login response.");
            }

            await ApiPayload.ReadAsync<TokenResponse>(response);
            return (false, "Sign in failed.");
        }
        catch (Exception ex)
        {
            return (false, $"Connection error: {ex.Message}");
        }
    }

    public async Task<bool> RefreshTokenAsync()
    {
        await _refreshGate.WaitAsync();
        try { return await RefreshCoreAsync(); }
        finally { _refreshGate.Release(); }
    }

    private async Task<bool> RefreshCoreAsync()
    {
        var refresh = RefreshToken ?? _configService.SavedRefreshToken;
        if (string.IsNullOrEmpty(refresh))
            return false;

        try
        {
            var url = $"{_configService.ServerUrl}/api/User/RefreshToken";
            using var content = new StringContent(JsonSerializer.Serialize(refresh), Encoding.UTF8, "application/json");

            using var response = await _httpClient.PostAsync(url, content);
            if (response.IsSuccessStatusCode)
            {
                var tokens = await ApiPayload.ReadAsync<TokenResponse>(response);
                if (!string.IsNullOrEmpty(tokens.AccessToken) && !string.IsNullOrEmpty(tokens.RefreshToken))
                {
                    _configService.SavedRefreshToken = tokens.RefreshToken;
                    SetTokens(tokens.AccessToken, tokens.RefreshToken);
                    return true;
                }
            }

            if(response.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)await LogoutAsync();
            return false;
        }
        catch
        {
            return false;
        }
    }

    public async Task LogoutAsync()
    {
        Actions.SetAccount("");
        StopRefreshTimer();

        var refresh = RefreshToken;
        if (!string.IsNullOrEmpty(refresh))
        {
            try
            {
                var url = $"{_configService.ServerUrl}/api/ProfileSettings/LogOut";
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(JsonSerializer.Serialize(refresh), Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
                using var response = await _httpClient.SendAsync(request);
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
        var owner=CurrentUser?.Id;var server=_configService.ServerUrl;
        var client = new HttpClient(new QueuedAuthenticatedHandler(Actions,FreshCredential,authenticatedTransport,()=>CurrentUser?.Id==owner&&IsAuthenticated&&_configService.ServerUrl==server)) { Timeout=Timeout.InfiniteTimeSpan };
        if (!string.IsNullOrEmpty(AccessToken))
        {
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", AccessToken);
        }
        return client;
    }

    public Task<string?> GetCurrentCredentialAsync()=>FreshCredential();
    private async Task<string?> FreshCredential()
    {
        if(string.IsNullOrEmpty(AccessToken))return null;
        var token=new JwtSecurityTokenHandler().ReadJwtToken(AccessToken);
        if(token.ValidTo<=DateTime.UtcNow.AddSeconds(30) && !await RefreshTokenAsync())return null;
        return AccessToken;
    }
    private void SetTokens(string accessToken, string refreshToken)
    {
        AccessToken = accessToken;
        RefreshToken = refreshToken;

        CurrentUser = ParseUserDataFromToken(accessToken);
        Actions.SetAccount(CurrentUser==null?"":_configService.ServerUrl+"|"+CurrentUser.Id);
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
