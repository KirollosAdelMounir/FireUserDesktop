using System;
using System.Collections.Generic;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using UserIdentityDesktop.Core.Models;

using UserIdentityDesktop.Core;
using UserIdentityDesktop.Data;
namespace UserIdentityDesktop.Service;

public class UserService : IUserService
{
    private readonly IAuthService _authService;
    private readonly IConfigurationService _configService;
    private HubConnection? _userHubConnection;
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public event Action<string, List<SystemUserSearch>>? SearchResultReceived;
    private string _lastSearchQuery = string.Empty;

    public UserService(IAuthService authService, IConfigurationService configService)
    {
        _authService = authService;
        _configService = configService;
    }

    public async Task ConnectAsync()
    {
        if (_userHubConnection != null && _userHubConnection.State == HubConnectionState.Connected)
            return;

        if (!_authService.IsAuthenticated)
            return;

        await DisconnectAsync();

        var hubUrl = $"{_configService.ServerUrl}/userhub";

        _userHubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(_authService.AccessToken);
            })
            .WithAutomaticReconnect()
            .Build();

        _userHubConnection.On<List<SystemUserSearch>>("SearchResult", results =>
        {
            SearchResultReceived?.Invoke(_lastSearchQuery, results);
        });

        try
        {
            await _userHubConnection.StartAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"UserHub connection error: {ex.Message}");
        }
    }

    public async Task DisconnectAsync()
    {
        if (_userHubConnection != null)
        {
            try
            {
                await _userHubConnection.StopAsync();
                await _userHubConnection.DisposeAsync();
            }
            catch
            {
                // ignore
            }
            finally
            {
                _userHubConnection = null;
            }
        }
    }

    public async Task SearchUsersAsync(string query)
    {
        _lastSearchQuery = query;
        if (_userHubConnection == null || _userHubConnection.State != HubConnectionState.Connected)
        {
            await ConnectAsync();
        }

        if (_userHubConnection != null && _userHubConnection.State == HubConnectionState.Connected)
        {
            try
            {
                await _userHubConnection.InvokeAsync("UserSearch", query);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"UserSearch error: {ex.Message}");
            }
        }
    }

    public async Task<ViewUserProfileDto?> GetUserProfileAsync(Guid userId)
    {
        try
        {
            using var client = _authService.CreateAuthenticatedClient();
            var url = $"{_configService.ServerUrl}/api/ProfileSettings/User?userId={userId}";
            using var response = await client.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                return await ApiPayload.ReadAsync<ViewUserProfileDto>(response);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to fetch user profile for {userId}: {ex.Message}");
        }
        return null;
    }
}
