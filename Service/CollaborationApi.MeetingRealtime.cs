using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;

namespace UserIdentityDesktop.Service;
public sealed partial class CollaborationApi
{
    private HubConnection? meetingConnection;
    private CancellationTokenSource? meetingLifetime;
    private Task? meetingHeartbeat;
    public event Action<string>? MeetingChanged;
    public async Task WatchMeetingAsync(string locator, CancellationToken ct = default)
    {
        await StopWatchingMeetingAsync();
        var connection = new HubConnectionBuilder().WithUrl(config.ServerUrl.TrimEnd('/') + "/meetinghub", options => options.AccessTokenProvider = () => Task.FromResult<string?>(auth.AccessToken)).WithAutomaticReconnect().Build();
        meetingConnection = connection;
        meetingLifetime = new CancellationTokenSource();
        connection.On<string>("MeetingChanged", changed => { if (changed == locator) MeetingChanged?.Invoke(locator); });
        connection.Reconnected += async _ => { await connection.InvokeAsync("Watch", locator, (string?)null); MeetingChanged?.Invoke(locator); };
        try
        {
            await connection.StartAsync(ct);
            await connection.InvokeAsync("Watch", locator, (string?)null, ct);
            var token = meetingLifetime.Token;
            meetingHeartbeat = Task.Run(async () =>
            {
                using var clock = new PeriodicTimer(TimeSpan.FromSeconds(30));
                try
                {
                    while (await clock.WaitForNextTickAsync(token))
                    {
                        if (connection.State != HubConnectionState.Connected) continue;
                        try { await connection.InvokeAsync("Heartbeat", token); }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                        catch { MeetingChanged?.Invoke(locator); }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            });
        }
        catch { await StopWatchingMeetingAsync(); throw; }
    }
    public async Task StopWatchingMeetingAsync()
    {
        meetingLifetime?.Cancel();
        if (meetingHeartbeat != null) await meetingHeartbeat;
        meetingHeartbeat = null;
        if (meetingConnection != null) await meetingConnection.DisposeAsync();
        meetingConnection = null;
        meetingLifetime?.Dispose(); meetingLifetime = null;
    }
}
