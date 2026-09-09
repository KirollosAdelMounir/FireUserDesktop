using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.AspNetCore.SignalR.Client;
using UserIdentityDesktop.Core.Models;

using UserIdentityDesktop.Core;
namespace UserIdentityDesktop.Service;

public class ChatService : IChatService
{
    private readonly IAuthService _authService;
    private readonly IConfigurationService _configService;
    private HubConnection? _hubConnection;
    private Timer? _heartbeatTimer;
    private Timer? _presenceTimer;
    private DateTime _lastActivitySentUtc = DateTime.MinValue;

    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public HubConnectionState State => _hubConnection?.State ?? HubConnectionState.Disconnected;

    public event Action<ChatMessage>? MessageReceived;
    public event Action<List<RecipientDto>>? RecipientsUpdated;
    public event Action<Guid, Guid>? MessageSeen;
    public event Action<bool, string, string>? TypingReceived;
    public event Action<UserPresence>? UserPresenceChanged;
    public event Action<Guid, UserStatus>? UserStatusChanged;
    public event Action<HubConnectionState>? ConnectionStateChanged;

    public ChatService(IAuthService authService, IConfigurationService configService)
    {
        _authService = authService;
        _configService = configService;
    }

    public async Task ConnectAsync()
    {
        if (_hubConnection != null && _hubConnection.State == HubConnectionState.Connected)
            return;

        if (!_authService.IsAuthenticated)
            return;

        await DisconnectAsync();

        var hubUrl = $"{_configService.ServerUrl}/chathub";

        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(_authService.AccessToken);
            })
            .WithAutomaticReconnect()
            .Build();

        _hubConnection.On<ChatMessage>("ReceiveMessageInChat", msg =>
        {
            MessageReceived?.Invoke(msg);
        });

        _hubConnection.On<List<RecipientDto>>("RecipientUpdated", recipients =>
        {
            RecipientsUpdated?.Invoke(recipients);
        });

        _hubConnection.On<JsonElement, JsonElement>("MessageSeen", (senderEl, receiverEl) =>
        {
            var senderStr = senderEl.ValueKind == JsonValueKind.String ? senderEl.GetString() : senderEl.ToString();
            var receiverStr = receiverEl.ValueKind == JsonValueKind.String ? receiverEl.GetString() : receiverEl.ToString();

            Guid.TryParse(senderStr, out var senderId);
            Guid.TryParse(receiverStr, out var receiverId);

            MessageSeen?.Invoke(senderId, receiverId);
        });

        _hubConnection.On<bool, JsonElement, JsonElement>("IsTyping", (isTyping, senderEl, receiverEl) =>
        {
            var senderStr = senderEl.ValueKind == JsonValueKind.String ? senderEl.GetString() ?? "" : senderEl.ToString();
            var receiverStr = receiverEl.ValueKind == JsonValueKind.String ? receiverEl.GetString() ?? "" : receiverEl.ToString();
            TypingReceived?.Invoke(isTyping, senderStr, receiverStr);
        });

        _hubConnection.On<JsonElement>("UserPresenceChanged", data =>
        {
            if (TryParsePresence(data, out var presence))
                UserPresenceChanged?.Invoke(presence);
        });

        _hubConnection.On<JsonElement>("UserStatusChanged", data =>
        {
            if (TryParsePresence(data, out var presence))
            {
                UserStatusChanged?.Invoke(presence.UserId, presence.Status);
            }
        });

        _hubConnection.Reconnecting += _ =>
        {
            ConnectionStateChanged?.Invoke(HubConnectionState.Reconnecting);
            return Task.CompletedTask;
        };

        _hubConnection.Reconnected += _ =>
        {
            ConnectionStateChanged?.Invoke(HubConnectionState.Connected);
            return Task.CompletedTask;
        };

        _hubConnection.Closed += _ =>
        {
            ConnectionStateChanged?.Invoke(HubConnectionState.Disconnected);
            return Task.CompletedTask;
        };

        try
        {
            await _hubConnection.StartAsync();
            StartHeartbeat();
            ConnectionStateChanged?.Invoke(HubConnectionState.Connected);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ChatHub connection error: {ex.Message}");
            ConnectionStateChanged?.Invoke(HubConnectionState.Disconnected);
        }
    }

    public async Task DisconnectAsync()
    {
        if (_hubConnection != null)
        {
            try
            {
                await _hubConnection.StopAsync();
                await _hubConnection.DisposeAsync();
            }
            catch
            {
                // ignore
            }
            finally
            {
                StopHeartbeat();
                _hubConnection = null;
                ConnectionStateChanged?.Invoke(HubConnectionState.Disconnected);
            }
        }
    }

    public async Task<bool> SendMessageAsync(Guid receiverId, string text)
    {
        if (_hubConnection == null || _hubConnection.State != HubConnectionState.Connected)
        {
            await ConnectAsync();
        }

        if (_hubConnection == null || _hubConnection.State != HubConnectionState.Connected)
            return false;

        var currentUser = _authService.CurrentUser;
        if (currentUser == null)
            return false;

        try
        {
            var chatDto = new ChatDto
            {
                Sender = currentUser.Id,
                Receiver = receiverId,
                Text = text,
                Attachments = null
            };

            await _hubConnection.InvokeAsync("SendMessageToUser", chatDto);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to send message: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> SendMessageWithAttachmentsAsync(Guid receiverId, string? text, IEnumerable<string> filePaths)
    {
        var currentUser = _authService.CurrentUser;
        if (currentUser == null)
            return false;

        try
        {
            using var client = _authService.CreateAuthenticatedClient();
            using var form = new MultipartFormDataContent();

            form.Add(new StringContent(currentUser.Id.ToString()), "Sender");
            form.Add(new StringContent(receiverId.ToString()), "Receiver");
            if (!string.IsNullOrEmpty(text))
            {
                form.Add(new StringContent(text), "Text");
            }

            var openStreams = new List<FileStream>();
            try
            {
                foreach (var path in filePaths)
                {
                    if (File.Exists(path))
                    {
                        var stream = File.OpenRead(path);
                        openStreams.Add(stream);
                        var fileContent = new StreamContent(stream);
                        var ext = Path.GetExtension(path).ToLowerInvariant();
                        var mime = GetMimeType(ext);
                        fileContent.Headers.ContentType = new MediaTypeHeaderValue(mime);
                        form.Add(fileContent, "Attachments", Path.GetFileName(path));
                    }
                }

                var url = $"{_configService.ServerUrl}/api/Chat/SendMessageWithAttachment";
                var response = await client.PostAsync(url, form);
                return response.IsSuccessStatusCode;
            }
            finally
            {
                foreach (var s in openStreams)
                    s.Dispose();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to send message with attachments: {ex.Message}");
            return false;
        }
    }

    public async Task MarkSeenAsync(Guid receiverId)
    {
        if (_hubConnection == null || _hubConnection.State != HubConnectionState.Connected)
            return;

        try
        {
            await _hubConnection.InvokeAsync("MarkSeen", receiverId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to mark seen: {ex.Message}");
        }
    }

    public async Task NotifyTypingAsync(Guid receiverId, bool isTyping)
    {
        if (_hubConnection == null || _hubConnection.State != HubConnectionState.Connected)
            return;

        try
        {
            await _hubConnection.InvokeAsync("TypingNotify", receiverId.ToString(), isTyping);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to notify typing: {ex.Message}");
        }
    }

    public async Task HeartbeatAsync()
    {
        if (_hubConnection?.State != HubConnectionState.Connected)
            return;

        try
        {
            await _hubConnection.InvokeAsync("Heartbeat");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to send heartbeat: {ex.Message}");
        }
    }

    public async Task ActivityAsync()
    {
        if (_hubConnection?.State != HubConnectionState.Connected ||
            DateTime.UtcNow - _lastActivitySentUtc < TimeSpan.FromSeconds(30))
            return;

        try
        {
            await _hubConnection.InvokeAsync("Activity");
            _lastActivitySentUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to send activity: {ex.Message}");
        }
    }

    public async Task<bool> SetStatusAsync(UserStatus status)
    {
        if (_hubConnection?.State != HubConnectionState.Connected)
            return false;

        try
        {
            await _hubConnection.InvokeAsync("SetStatus", status);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to set status: {ex.Message}");
            return false;
        }
    }

    public async Task<List<Guid>> GetOnlineUsersAsync()
    {
        if (_hubConnection?.State != HubConnectionState.Connected)
            return new List<Guid>();

        try
        {
            return await _hubConnection.InvokeAsync<List<Guid>>("GetOnlineUsers");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to fetch online users: {ex.Message}");
            return new List<Guid>();
        }
    }

    public async Task<UserPresence?> GetUserPresenceAsync(Guid userId)
    {
        if (_hubConnection?.State != HubConnectionState.Connected)
            return null;

        try
        {
            return await _hubConnection.InvokeAsync<UserPresence>("GetUserPresence", userId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to fetch user presence: {ex.Message}");
            return null;
        }
    }

    private void StartHeartbeat()
    {
        StopHeartbeat();
        _heartbeatTimer = new Timer(_ => _ = HeartbeatAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
        _presenceTimer = new Timer(_ => _ = RefreshCurrentPresenceAsync(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));
    }

    private void StopHeartbeat()
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
        _presenceTimer?.Dispose();
        _presenceTimer = null;
    }

    private async Task RefreshCurrentPresenceAsync()
    {
        var currentUser = _authService.CurrentUser;
        if (currentUser == null)
            return;

        var presence = await GetUserPresenceAsync(currentUser.Id);
        if (presence != null)
            UserPresenceChanged?.Invoke(presence);
    }

    private static bool TryParseStatus(JsonElement value, out UserStatus status)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            status = (UserStatus)number;
            return Enum.IsDefined(status);
        }

        return Enum.TryParse(value.ToString(), true, out status);
    }

    private static bool TryParsePresence(JsonElement data, out UserPresence presence)
    {
        presence = new UserPresence();
        if (data.ValueKind != JsonValueKind.Object ||
            !TryGetPropertyIgnoreCase(data, "userId", out var userIdValue) ||
            !Guid.TryParse(userIdValue.ToString(), out var userId) ||
            !TryGetPropertyIgnoreCase(data, "status", out var statusValue) ||
            !TryParseStatus(statusValue, out var status))
        {
            return false;
        }

        presence.UserId = userId;
        presence.Status = status;
        if (TryGetPropertyIgnoreCase(data, "lastSeen", out var lastSeen) &&
            lastSeen.ValueKind != JsonValueKind.Null)
        {
            DateTime.TryParse(lastSeen.ToString(), out var parsedLastSeen);
            presence.LastSeen = parsedLastSeen;
        }

        if (TryGetPropertyIgnoreCase(data, "lastActivity", out var lastActivity) &&
            lastActivity.ValueKind != JsonValueKind.Null)
        {
            DateTime.TryParse(lastActivity.ToString(), out var parsedLastActivity);
            presence.LastActivity = parsedLastActivity;
        }

        return true;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement data, string name, out JsonElement value)
    {
        foreach (var property in data.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    public async Task<List<RecipientDto>> GetRecipientsAsync()
    {
        try
        {
            using var client = _authService.CreateAuthenticatedClient();
            var url = $"{_configService.ServerUrl}/api/Chat/Receipients";
            var response = await client.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<List<RecipientDto>>(_jsonOptions);
                return result ?? new List<RecipientDto>();
            }
            else
            {
                var err = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"Receipients API returned {response.StatusCode}: {err}");
                return new List<RecipientDto>();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to fetch recipients: {ex.Message}");
            return new List<RecipientDto>();
        }
    }

    public async Task<List<ChatMessage>> GetChatHistoryAsync(Guid receiverId, int page = 0)
    {
        try
        {
            using var client = _authService.CreateAuthenticatedClient();
            var url = $"{_configService.ServerUrl}/api/Chat/ChatOf?ReceiverId={receiverId}&page={page}";
            var response = await client.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<List<ChatMessage>>(_jsonOptions);
                return result ?? new List<ChatMessage>();
            }
            else
            {
                var err = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"ChatOf API returned {response.StatusCode}: {err}");
                return new List<ChatMessage>();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to fetch chat history: {ex.Message}");
            return new List<ChatMessage>();
        }
    }

    public async Task<bool> SaveAttachmentToFileAsync(Guid attachmentId, string destinationFilePath)
    {
        try
        {
            using var client = _authService.CreateAuthenticatedClient();
            var url = $"{_configService.ServerUrl}/api/User/ChatAttachment?attachmentId={attachmentId}";

            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (response.IsSuccessStatusCode)
            {
                using var fs = File.Create(destinationFilePath);
                await response.Content.CopyToAsync(fs);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save attachment: {ex.Message}");
            return false;
        }
    }

    private static string GetMimeType(string extension) => extension switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".pdf" => "application/pdf",
        ".zip" => "application/zip",
        ".txt" => "text/plain",
        ".json" => "application/json",
        _ => "application/octet-stream"
    };
}
