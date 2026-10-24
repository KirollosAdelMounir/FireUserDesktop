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
    private CancellationTokenSource? connectionLifetime;
    private sealed class ContinuousReconnect : IRetryPolicy { public TimeSpan? NextRetryDelay(RetryContext context)=>TimeSpan.FromSeconds(Math.Min(30,Math.Pow(2,Math.Min(context.PreviousRetryCount,5)))); }
    private void ConnectionChanged(HubConnectionState state){_authService.Actions?.SetConnected(state==HubConnectionState.Connected);ConnectionStateChanged?.Invoke(state);}
    private async Task RecoverConnection(HubConnection connection,CancellationToken ct)
    {
        var attempt=0;
        while(!ct.IsCancellationRequested&&ReferenceEquals(connection,_hubConnection)&&_authService.IsAuthenticated){
            try{await Task.Delay(TimeSpan.FromSeconds(Math.Min(30,Math.Pow(2,Math.Min(attempt++,5)))),ct);await connection.StartAsync(ct);StartHeartbeat();ConnectionChanged(HubConnectionState.Connected);return;}
            catch(OperationCanceledException)when(ct.IsCancellationRequested){return;}
            catch{ConnectionChanged(HubConnectionState.Reconnecting);}
        }
    }
    private Task QueueHubAction(string method,params object?[] args)
    {
        if(_hubConnection?.State!=HubConnectionState.Connected)_authService.Actions?.SetConnected(false);
        async Task<bool> Send(CancellationToken ct){if(_hubConnection?.State!=HubConnectionState.Connected)throw new InvalidOperationException("Reconnect before sending this action.");await _hubConnection.InvokeCoreAsync(method,args,ct);return true;}
        return _authService.Actions is {} queue?queue.RunAsync(Send):Send(CancellationToken.None);
    }

    private Timer? _heartbeatTimer;
    private DateTime _lastActivitySentUtc = DateTime.MinValue;

    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public HubConnectionState State => _hubConnection?.State ?? HubConnectionState.Disconnected;

    public async Task RespondToMeetingInvitationAsync(string locator, Guid invitationId, bool accept)
    {
        await QueueHubAction("RespondToMeetingInvitation", locator, invitationId, accept);
    }
    public event Action<ChatMessage>? MessageReceived;
    public event Action<ChatMessage>? MessageUpdated;
    public event Action<PlaybackReceipt>? AttachmentPlayed;
    public Task MarkAttachmentPlayedAsync(string messageId, Guid attachmentId) => QueueHubAction("MarkAttachmentPlayed", messageId, attachmentId);
    public event Action<string>? MessageDeleted;
    public event Action<List<RecipientDto>>? RecipientsUpdated;
    public event Action<Guid, Guid, DateTime>? ReadReceiptReceived;
    public event Action<bool, string, string>? TypingReceived;
    public event Action<ChatActivity>? ChatActivityReceived;
    public event Action<ConversationTyping>? ConversationTypingReceived;
    public Task NotifyConversationTypingAsync(string conversationId, bool isTyping) => _hubConnection?.InvokeAsync("ConversationTyping", conversationId, isTyping) ?? Task.CompletedTask;
    public IDisposable BeginChatActivity(Guid? receiverId, string? conversationId, string kind) => new ActivityLease(this, receiverId, conversationId, kind);
    private sealed class ActivityLease : IDisposable
    {
        private readonly ChatService owner;
        private readonly Guid? receiver;
        private readonly string? conversation;
        private readonly string kind;
        private readonly Timer timer;
        private int stopped;
        public ActivityLease(ChatService owner, Guid? receiver, string? conversation, string kind)
        {
            this.owner = owner; this.receiver = receiver; this.conversation = conversation; this.kind = kind;
            timer = new Timer(_ => Send(true), null, 0, 3000);
        }
        private async void Send(bool active)
        {
            if (active && Volatile.Read(ref stopped) != 0) return;
            try { if (owner._hubConnection?.State == HubConnectionState.Connected) await owner._hubConnection.InvokeAsync("NotifyChatActivity", new { ReceiverId = receiver, ConversationId = conversation, Kind = kind, Active = active }); }
            catch { /* Activity is ephemeral; a lost connection must not interrupt sending. */ }
        }
        public void Dispose() { if (Interlocked.Exchange(ref stopped, 1) == 0) { timer.Dispose(); Send(false); } }
    }
    public event Action<UserPresence>? UserPresenceChanged;
    public event Action<Guid, UserStatus>? UserStatusChanged;
    public event Action<HubConnectionState>? ConnectionStateChanged;
    public event Action? WorkplaceChanged;
    public event Action? AppearancePreferencesChanged;
    public event Action<Guid>? WorkplaceNotificationChanged;
    public event Action? WorkplaceNotificationsRead;
    public event Action<IncomingCall>? IncomingCallReceived;
    public event Action<CallParticipantLeft>? CallParticipantLeft;

    public ChatService(IAuthService authService, IConfigurationService configService, ChatEncryption? encryption = null, ITransferTracker? transfers = null)
    {
        Transfers = transfers ?? new TransferTracker();
        _authService = authService;
        _configService = configService;
        _encryption = encryption ?? new ChatEncryption(authService, configService);
    }

    public ITransferTracker Transfers { get; }

    public async Task ConnectAsync()
    {
        if (_hubConnection != null && _hubConnection.State == HubConnectionState.Connected)
            return;

        if (!_authService.IsAuthenticated)
            return;

        await DisconnectAsync();
        connectionLifetime=new();
        var connectToken=connectionLifetime.Token;

        var hubUrl = $"{_configService.ServerUrl}/chathub";

        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.AccessTokenProvider = () => _authService.GetCurrentCredentialAsync();
            })
            .WithAutomaticReconnect(new ContinuousReconnect())
            .Build();
        _hubConnection.On<Guid>("WorkplaceNotificationChanged", id => WorkplaceNotificationChanged?.Invoke(id));
        _hubConnection.On("WorkplaceNotificationsRead", () => WorkplaceNotificationsRead?.Invoke());

        _hubConnection.On("AppearancePreferencesChanged", () => AppearancePreferencesChanged?.Invoke());
        _hubConnection.On("WorkplaceChanged", () => WorkplaceChanged?.Invoke());
        _hubConnection.On<ChatActivity>("ChatActivity", activity => ChatActivityReceived?.Invoke(activity));
        _hubConnection.On<ConversationTyping>("ConversationTyping", typing => ConversationTypingReceived?.Invoke(typing));
        _hubConnection.On("ConversationsUpdated", async () => RecipientsUpdated?.Invoke(await GetRecipientsAsync()));
        _hubConnection.On<string>("MeetingConversationChanged", _ => WorkplaceChanged?.Invoke());
        _hubConnection.On<IncomingCall>("IncomingCall", call => IncomingCallReceived?.Invoke(call));
        _hubConnection.On<CallParticipantLeft>("CallParticipantLeft", change => CallParticipantLeft?.Invoke(change));

        _hubConnection.On<ChatMessage>("ReceiveMessageInChat", async msg =>
        {
            MessageReceived?.Invoke(await _encryption.DecryptAsync(msg));
        });

        _hubConnection.On<PlaybackReceipt>("AttachmentPlayed", receipt => AttachmentPlayed?.Invoke(receipt));
        _hubConnection.On<ChatMessage>("MessageUpdated", async msg => MessageUpdated?.Invoke(await _encryption.DecryptAsync(msg)));
        _hubConnection.On<ChatMessage>("MessageDeleted", msg => MessageDeleted?.Invoke(msg.Id));
        _hubConnection.On<List<RecipientDto>>("RecipientUpdated", async recipients =>
        {
            foreach (var recipient in recipients) await _encryption.DecryptPreviewAsync(recipient);
            RecipientsUpdated?.Invoke(recipients);
        });

        _hubConnection.On<JsonElement, JsonElement, DateTime>("MessageSeen", (senderEl, receiverEl, through) =>
        {
            var senderStr = senderEl.ValueKind == JsonValueKind.String ? senderEl.GetString() : senderEl.ToString();
            var receiverStr = receiverEl.ValueKind == JsonValueKind.String ? receiverEl.GetString() : receiverEl.ToString();

            Guid.TryParse(senderStr, out var senderId);
            Guid.TryParse(receiverStr, out var receiverId);

            ReadReceiptReceived?.Invoke(senderId, receiverId, through);
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
            ConnectionChanged(HubConnectionState.Reconnecting);
            return Task.CompletedTask;
        };

        _hubConnection.Reconnected += connectionId =>
        {
            ConnectionChanged(HubConnectionState.Connected);
            _ = RefreshCurrentPresenceAsync();
            return Task.CompletedTask;
        };

        _hubConnection.Closed += _ =>
        {
            ConnectionChanged(HubConnectionState.Disconnected);
            return Task.CompletedTask;
        };

        try
        {
            await _hubConnection.StartAsync(connectToken);
            StartHeartbeat();
            ConnectionChanged(HubConnectionState.Connected);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ChatHub connection error: {ex.Message}");
            ConnectionChanged(HubConnectionState.Reconnecting);
            _ = RecoverConnection(_hubConnection,connectToken);
        }
    }

    public async Task DisconnectAsync()
    {
        connectionLifetime?.Cancel();connectionLifetime?.Dispose();connectionLifetime=null;
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
                ConnectionChanged(HubConnectionState.Disconnected);
            }
        }
    }

    public async Task<bool> SendMessageAsync(Guid receiverId, string text)
    {
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

            await QueueHubAction("SendMessageToUser", chatDto);
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

        var paths = new List<string>(filePaths);
        if (paths.Count == 0) return false;
        using var transfer = Transfers.Start(TransferDirection.Upload, paths.Count);
        var prepared = new List<ChatEncryption.PreparedFile>();
        try
        {
            using var client = _authService.CreateAuthenticatedClient();
            foreach (var path in paths) prepared.Add(await ChatEncryption.PrepareFileAsync(path));
            var encrypted = await Protect(receiverId, conversationId, text, prepared.ConvertAll(x => x.Metadata).ToArray());
            if (_authService.CurrentUser?.Id != currentUser.Id) throw new OperationCanceledException("The signed-in account changed.");
            await ResumableAttachmentUpload.SendAsync(client, _configService.ServerUrl,
                new AttachmentUploadRequest(currentUser.Id, receiverId, conversationId, encrypted, replyTo,
                    prepared.ConvertAll(x => new AttachmentUploadFile(x.Metadata.Id + ".fua", "application/octet-stream", new FileInfo(x.Path).Length))),
                prepared.ConvertAll(x => x.Path), transfer);
            transfer.Complete(true);
            return true;
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
        try
        {
            await QueueHubAction("MarkSeen", receiverId);
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
            if(_authService.Actions?.IsConnected==false)_authService.Actions.SetConnected(true);
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
        try
        {
            await QueueHubAction("SetStatus", status);
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
                foreach (var recipient in result) await _encryption.DecryptPreviewAsync(recipient);
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

    public async Task<bool> SaveAttachmentToFileAsync(Guid attachmentId, string destinationFilePath, EncryptedFile? encryption = null, CancellationToken cancellationToken = default, IProgress<long>? progress = null)
    {
        try
        {
            using var client = _authService.CreateAuthenticatedClient();
            var url = $"{_configService.ServerUrl}/api/User/ChatAttachment?attachmentId={attachmentId}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Options.Set(AttachmentTransferRequests.UnlimitedDuration, true);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var temporary = destinationFilePath + "." + Guid.NewGuid().ToString("N") + ".partial";
                try
                {
                    var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, BufferSize = 65536, Options = FileOptions.Asynchronous };
                    if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                    await using (var fs = new FileStream(temporary, options))
                    {
                        if (progress is ITransferOperation operation) { if (response.Content.Headers.ContentLength is {} length) operation.SetTotal(length); operation.SetPhase("Downloading…"); }
                        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                        using var counted = new TransferReadStream(source, progress);
                        if (encryption == null) await counted.CopyToAsync(fs, cancellationToken);
                        else await ChatEncryption.TransformFileAsync(counted, fs, encryption, encrypt: false, cancellationToken);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Move(temporary, destinationFilePath, overwrite: true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                return true;
            }
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save attachment: {ex.Message}");
            return false;
        }
    }

    internal static string GetMimeType(string extension) => extension switch
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
        ".m4a" => "audio/mp4",
        ".aac" => "audio/aac",
        ".ogg" or ".oga" or ".opus" => "audio/ogg",
        ".weba" => "audio/webm",
        ".flac" => "audio/flac",
        ".pdf" => "application/pdf",
        ".zip" => "application/zip",
        ".txt" => "text/plain",
        ".json" => "application/json",
        _ => "application/octet-stream"
    };
}
