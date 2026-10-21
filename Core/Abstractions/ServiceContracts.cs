using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.AspNetCore.SignalR.Client;
using UserIdentityDesktop.Core.Models;

namespace UserIdentityDesktop.Core;

public interface IAppInstanceService : IDisposable
{
    bool TryAcquire();
    Task StartServerAsync(CancellationToken cancellationToken, Func<string, Task> onMessageReceived);
    Task<bool> SendToExistingInstanceAsync(string message);
    void Release();
}

public interface IConfigurationService
{
    string ServerUrl { get; set; }
    string? SavedRefreshToken { get; set; }
    string? SavedEmail { get; set; }
    bool IsDarkMode { get; set; }
    void Save();
}

public interface IAuthService
{
    UserData? CurrentUser { get; }
    string? AccessToken { get; }
    string? RefreshToken { get; }
    bool IsAuthenticated { get; }
    event Action<UserData?>? CurrentUserChanged;
    event Action<bool>? AuthenticationStateChanged;
    Task<(bool Success, string? ErrorMessage)> LoginAsync(string email, string password);
    Task<bool> RefreshTokenAsync();
    Task LogoutAsync();
    HttpClient CreateAuthenticatedClient();
}

public interface IFileService
{
    Task<IReadOnlyList<string>> PickFilesAsync(string title = "Select files", bool allowMultiple = true);
    Task<string?> PickSaveFileAsync(string defaultFileName, string? title = "Save file");
    void OpenFileWithDefaultApp(string filePath);
}

public interface IImageService
{
    Task<Avalonia.Media.Imaging.Bitmap?> GetProfilePictureAsync(Guid userId);
    Avalonia.Media.Imaging.Bitmap? GetBitmapFromBase64(string? base64);
    Avalonia.Media.Imaging.Bitmap? LoadLocalImage(string filePath);
    Task<Avalonia.Media.Imaging.Bitmap?> GetAttachmentThumbnailAsync(Guid attachmentId);
    void ClearCache();
}

public interface IChatService
{
    event Action? WorkplaceChanged;
    event Action<IncomingCall>? IncomingCallReceived;
    event Action<CallParticipantLeft>? CallParticipantLeft;
    HubConnectionState State { get; }
    event Action<ChatMessage>? MessageReceived;
    event Action<ChatMessage>? MessageUpdated;
    event Action<string>? MessageDeleted;
    event Action<List<RecipientDto>>? RecipientsUpdated;
    event Action<Guid, Guid>? MessageSeen;
    event Action<bool, string, string>? TypingReceived;
    event Action<UserPresence>? UserPresenceChanged;
    event Action<Guid, UserStatus>? UserStatusChanged;
    event Action<HubConnectionState>? ConnectionStateChanged;
    Task ConnectAsync();
    Task DisconnectAsync();
    Task UnlockChatAsync(string pin);
    Task<ChatSecurityStatus> ChatSecurityAsync();
    Task ChangeChatPinAsync(string action, string pin, string confirmation, string password, string otp);
    Task<bool> SendMessageAsync(Guid receiverId, string text);
    Task<bool> SendConversationAsync(string conversationId, string text, string? replyTo = null);
    Task<List<ChatMessage>> GetConversationHistoryAsync(string conversationId, int page = 0);
    Task<List<ChatAttachment>> GetAttachmentsAsync(Guid receiverId, string? conversationId, int page);
    Task<string> AddMembersAsync(RecipientDto recipient, List<Guid> ids);
    Task<bool> SendReplyAsync(Guid receiverId, string text, string replyTo);
    Task EditMessageAsync(ChatMessage message, string text);
    Task DeleteMessageAsync(string messageId);
    Task ReactAsync(string messageId, string emoji);
    Task<bool> SendMessageWithAttachmentsAsync(Guid receiverId, string? text, IEnumerable<string> filePaths, string? conversationId = null, string? replyTo = null);
    Task MarkSeenAsync(Guid receiverId);
    Task MarkConversationSeenAsync(string conversationId);
    Task NotifyTypingAsync(Guid receiverId, bool isTyping);
    Task HeartbeatAsync();
    Task ActivityAsync();
    Task<bool> SetStatusAsync(UserStatus status);
    Task<List<Guid>> GetOnlineUsersAsync();
    Task<UserPresence?> GetUserPresenceAsync(Guid userId);
    Task<List<RecipientDto>> GetRecipientsAsync();
    Task<List<ChatMessage>> GetChatHistoryAsync(Guid receiverId, int page = 0);
    Task<bool> SaveAttachmentToFileAsync(Guid attachmentId, string destinationFilePath, EncryptedFile? encryption = null);
}

public interface IUserService
{
    event Action<string, List<SystemUserSearch>>? SearchResultReceived;
    Task ConnectAsync();
    Task DisconnectAsync();
    Task SearchUsersAsync(string query);
    Task<ViewUserProfileDto?> GetUserProfileAsync(Guid userId);
}

public interface INotificationService
{
    event Action<Guid>? NotificationActivated;
    void ShowNotification(string title, string message, Guid senderId);
}
