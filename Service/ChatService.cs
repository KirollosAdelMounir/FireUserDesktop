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
using UserIdentityDesktop.Data;
namespace UserIdentityDesktop.Service;

public partial class ChatService : IChatService
{
    private readonly IAuthService _authService;
    private readonly IConfigurationService _configService;
    private readonly ChatEncryption _encryption;
    private HubConnection? _hubConnection;
    private Timer? _heartbeatTimer;
    private DateTime _lastActivitySentUtc = DateTime.MinValue;

    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public HubConnectionState State => _hubConnection?.State ?? HubConnectionState.Disconnected;

    public event Action<ChatMessage>? MessageReceived;
    public event Action<ChatMessage>? MessageUpdated;
    public event Action<string>? MessageDeleted;
    public event Action<List<RecipientDto>>? RecipientsUpdated;
    public event Action<Guid, Guid>? MessageSeen;
    public event Action<bool, string, string>? TypingReceived;
    public event Action<UserPresence>? UserPresenceChanged;
    public event Action<Guid, UserStatus>? UserStatusChanged;
    public event Action<HubConnectionState>? ConnectionStateChanged;
    public event Action? WorkplaceChanged;
    public event Action<IncomingCall>? IncomingCallReceived;
    public event Action<CallParticipantLeft>? CallParticipantLeft;

    public ChatService(IAuthService authService, IConfigurationService configService, ChatEncryption? encryption = null)
    {
        _authService = authService;
        _configService = configService;
        _encryption = encryption ?? new ChatEncryption(authService, configService);
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

        _hubConnection.On("WorkplaceChanged", () => WorkplaceChanged?.Invoke());
        _hubConnection.On("ConversationsUpdated", async () => RecipientsUpdated?.Invoke(await GetRecipientsAsync()));
        _hubConnection.On<string>("MeetingConversationChanged", _ => WorkplaceChanged?.Invoke());
        _hubConnection.On<IncomingCall>("IncomingCall", call => IncomingCallReceived?.Invoke(call));
        _hubConnection.On<CallParticipantLeft>("CallParticipantLeft", change => CallParticipantLeft?.Invoke(change));

        _hubConnection.On<ChatMessage>("ReceiveMessageInChat", async msg =>
        {
            MessageReceived?.Invoke(await _encryption.DecryptAsync(msg));
        });

        _hubConnection.On<ChatMessage>("MessageUpdated", async msg => MessageUpdated?.Invoke(await _encryption.DecryptAsync(msg)));
        _hubConnection.On<ChatMessage>("MessageDeleted", msg => MessageDeleted?.Invoke(msg.Id));
        _hubConnection.On<List<RecipientDto>>("RecipientUpdated", recipients =>
        {
            foreach (var recipient in recipients)
                if (recipient.LastMessage?.StartsWith("FU1.", StringComparison.Ordinal) == true) recipient.LastMessage = "Encrypted message";
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

        _hubConnection.Reconnected += connectionId =>
        {
            ConnectionStateChanged?.Invoke(HubConnectionState.Connected);
            _ = RefreshCurrentPresenceAsync();
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
        _encryption.Lock();
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
                Text = await _encryption.EncryptAsync(receiverId, text),
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

    public async Task<bool> SendMessageWithAttachmentsAsync(Guid receiverId, string? text, IEnumerable<string> filePaths, string? conversationId = null, string? replyTo = null)
    {
        var currentUser = _authService.CurrentUser;
        if (currentUser == null)
            return false;

        var prepared = new List<ChatEncryption.PreparedFile>();
        try
        {
            using var client = _authService.CreateAuthenticatedClient();
            using var form = new MultipartFormDataContent();

            form.Add(new StringContent(currentUser.Id.ToString()), "Sender");
            form.Add(new StringContent(receiverId.ToString()), "Receiver");
            if (conversationId != null) form.Add(new StringContent(conversationId), "ConversationId");
            if (replyTo != null) form.Add(new StringContent(replyTo), "ReplyToMessageId");
            foreach (var path in filePaths) prepared.Add(await ChatEncryption.PrepareFileAsync(path));
            form.Add(new StringContent(await Protect(receiverId, conversationId, text, prepared.ConvertAll(x => x.Metadata).ToArray())), "Text");

            var openStreams = new List<FileStream>();
            try
            {
                foreach (var item in prepared)
                {
                    var path = item.Path;
                    if (File.Exists(path))
                    {
                        var stream = File.OpenRead(path);
                        openStreams.Add(stream);
                        var fileContent = new StreamContent(stream);
                        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                        form.Add(fileContent, "Attachments", item.Metadata.Id + ".fua");
                    }
                }

                var url = $"{_configService.ServerUrl}/api/Chat/SendMessageWithAttachment";
                using var response = await client.PostAsync(url, form);
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
        finally
        {
            foreach (var item in prepared) File.Delete(item.Path);
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
        _ = RefreshCurrentPresenceAsync();
    }

    private void StopHeartbeat()
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
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
            using var response = await client.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                var result = await ApiPayload.ReadAsync<List<RecipientDto>>(response);
                foreach (var recipient in result)
                    if (recipient.LastMessage?.StartsWith("FU1.", StringComparison.Ordinal) == true) recipient.LastMessage = "Encrypted message";
                return result;
            }
            else
            {
                Console.WriteLine($"Recipients API returned {response.StatusCode}");
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
            using var response = await client.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                var result = await ApiPayload.ReadAsync<List<ChatMessage>>(response);
                foreach (var message in result) await _encryption.DecryptAsync(message);
                return result;
            }
            else
            {
                Console.WriteLine($"Chat history API returned {response.StatusCode}");
                return new List<ChatMessage>();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to fetch chat history: {ex.Message}");
            return new List<ChatMessage>();
        }
    }

    public Task<ChatSecurityStatus> ChatSecurityAsync() => _encryption.StatusAsync();
    public Task ChangeChatPinAsync(string action, string pin, string confirmation, string password, string otp) => _encryption.ChangePinAsync(action, pin, confirmation, password, otp);
    public Task UnlockChatAsync(string pin) => _encryption.UnlockAsync(pin);

    public async Task<bool> SaveAttachmentToFileAsync(Guid attachmentId, string destinationFilePath, EncryptedFile? encryption = null)
    {
        try
        {
            using var client = _authService.CreateAuthenticatedClient();
            var url = $"{_configService.ServerUrl}/api/User/ChatAttachment?attachmentId={attachmentId}";

            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (response.IsSuccessStatusCode)
            {
                var temporary = destinationFilePath + "." + Guid.NewGuid().ToString("N") + ".partial";
                try
                {
                    await using (var fs = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                    {
                        if (encryption == null) await response.Content.CopyToAsync(fs);
                        else await ChatEncryption.TransformFileAsync(await response.Content.ReadAsStreamAsync(), fs, encryption, encrypt: false);
                    }
                    File.Move(temporary, destinationFilePath, overwrite: true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
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
