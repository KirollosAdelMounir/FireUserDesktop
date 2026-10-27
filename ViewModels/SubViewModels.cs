using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.Core;

namespace UserIdentityDesktop.ViewModels;

public sealed class StatusOption
{
    public UserStatus Status { get; }
    public string Label { get; }
    public IBrush Color { get; }

    public StatusOption(UserStatus status, string label, string color)
    {
        Status = status;
        Label = label;
        Color = new SolidColorBrush(Avalonia.Media.Color.Parse(color));
    }
}

public partial class RecipientItemViewModel : ObservableObject
{
    public Guid ReceiverId { get; set; }
    public bool IsGroup { get; set; }
    public string? ConversationId { get; set; }

    [ObservableProperty]
    private string _receiverName = string.Empty;

    [ObservableProperty]
    private string? _lastMessage;
    [ObservableProperty] private int _attachmentsCount;
    public bool HasPreviewAttachments => AttachmentsCount > 0;
    [ObservableProperty] private string _activityLabel = string.Empty;
    partial void OnActivityLabelChanged(string value)=>OnPropertyChanged(nameof(PreviewText));
    partial void OnIsTypingChanged(bool value)=>OnPropertyChanged(nameof(PreviewText));
    public string PreviewText => !string.IsNullOrWhiteSpace(ActivityLabel)?ActivityLabel:IsTyping?"Typing…":string.IsNullOrWhiteSpace(LastMessage) && HasPreviewAttachments ? "Attachments" : LastMessage ?? "";
    partial void OnLastMessageChanged(string? value) => OnPropertyChanged(nameof(PreviewText));
    partial void OnAttachmentsCountChanged(int value) { OnPropertyChanged(nameof(PreviewText)); OnPropertyChanged(nameof(HasPreviewAttachments)); }

    [ObservableProperty]
    private DateTime _lastSent;

    [ObservableProperty]
    private bool _isSeen;

    [ObservableProperty]
    private Bitmap? _avatar;

    [ObservableProperty]
    private bool _isTyping;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private UserStatus _status = UserStatus.Offline;

    [ObservableProperty]
    private bool _hasMobileConnection;

    public string StatusLabel => IsGroup ? "Group conversation" : ChatViewModel.GetStatusLabel(Status);
    public IBrush StatusColor => ChatViewModel.GetStatusColor(Status);

    public string Initials
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ReceiverName)) return "?";
            var parts = ReceiverName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                return $"{parts[0][0]}{parts[1][0]}".ToUpper();
            return parts[0][0].ToString().ToUpper();
        }
    }

    public static RecipientItemViewModel FromDto(RecipientDto dto)
    {
        return new RecipientItemViewModel
        {
            ReceiverId = dto.ReceiverId,
            IsGroup = dto.IsGroup,
            ConversationId = dto.ConversationId,
            ReceiverName = dto.ReceiverName,
            LastMessage = dto.LastMessage,
            AttachmentsCount = dto.AttachmentsCount,
            LastSent = dto.LastSent,
            IsSeen = dto.IsSeen
        };
    }

    partial void OnStatusChanged(UserStatus value)
    {
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusColor));
    }
}

public partial class ChatAttachmentItemViewModel : ObservableObject
{
    private readonly IChatService _chatService;
    private readonly IFileService _fileService;
    private readonly IImageService _imageService;

    public ChatAttachment Attachment { get; }
    internal string? ReceiptMessageId { get; init; }
    internal bool CanReportPlayback { get; init; }
    internal Task ReportPlaybackStartedAsync() => CanReportPlayback && IsAudio && ReceiptMessageId is {} id
        ? _chatService.MarkAttachmentPlayedAsync(id, Attachment.Id) : Task.CompletedTask;

    [ObservableProperty]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    private bool _isDownloading;

    public string FileName => Attachment.FileName;
    public string FormattedFileSize => Attachment.FormattedFileSize;
    public bool IsImage => Attachment.IsImage;
    public bool IsVideo => Attachment.IsVideo;
    public bool IsAudio => Attachment.IsAudio;
    public bool IsDocument => Attachment.IsDocument;

    public ChatAttachmentItemViewModel(
        ChatAttachment attachment,
        IChatService chatService,
        IFileService fileService,
        IImageService imageService)
    {
        Attachment = attachment;
        _chatService = chatService;
        _fileService = fileService;
        _imageService = imageService;

        // Authenticated previews are loaded only by an attached preview surface.

    }

    private async Task LoadThumbnailAsync()
    {
        Thumbnail = await _imageService.GetAttachmentThumbnailAsync(Attachment.Id);
    }

    public async Task<string> PrepareAudioPlaybackAsync(System.Threading.CancellationToken cancellationToken = default)
    {
        if (!IsAudio) throw new InvalidOperationException("This attachment is not audio.");
        return await PreparePreviewAsync(cancellationToken);
    }

    internal ITransferTracker? Transfers => _chatService.Transfers;
    internal long? TransferSize
    {
        get
        {
            var size = Attachment.Encryption?.Size ?? Attachment.FileSize;
            if (size < 0) return null;
            if (Attachment.Encryption is not {} file) return size;
            if (file.ChunkSize <= 0) return null;
            try { return checked(size + 16 * Math.Max(1, size / file.ChunkSize + (size % file.ChunkSize == 0 ? 0 : 1))); }
            catch (OverflowException) { return null; }
        }
    }

    public async Task<string> PreparePreviewAsync(System.Threading.CancellationToken cancellationToken = default, IProgress<long>? progress = null)
    {
        var path = Path.Combine(Path.GetTempPath(), "fireuser-preview-" + Guid.NewGuid().ToString("N") + Path.GetExtension(FileName));
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (new FileStream(path, options)) { }
        try
        {
            if (!await _chatService.SaveAttachmentToFileAsync(Attachment.Id, path, Attachment.Encryption, cancellationToken, progress)) throw new IOException("Unable to load this attachment.");
            return path;
        }
        catch { File.Delete(path); throw; }
    }

    [RelayCommand]
    public async Task DownloadAsync()
    {
        if (IsDownloading) return;

        try
        {
            IsDownloading = true;

            var savePath = await _fileService.PickSaveFileAsync(Attachment.FileName, "Save Attachment");
            if (!string.IsNullOrEmpty(savePath))
            {
                using var transfer = Transfers?.Start(TransferDirection.Download, 1, TransferSize);
                var success = await _chatService.SaveAttachmentToFileAsync(Attachment.Id, savePath, Attachment.Encryption, transfer?.CancellationToken??default, transfer);
                if (success) transfer?.FileCompleted();
                transfer?.Complete(success);
                if (!success) throw new IOException("Unable to download this attachment.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error downloading attachment: {ex.Message}");
        }
        finally
        {
            IsDownloading = false;
        }
    }
}

public partial class ChatMessageItemViewModel : ObservableObject
{
    private readonly IFileService fileService;
    [ObservableProperty] private string? _attachmentError;
    public ChatMessage Message { get; }
    public bool IsMine { get; }
    public bool IsIncoming => !IsMine && IsNew;

    [ObservableProperty]
    private bool _isNew;

    [ObservableProperty]
    private bool _seen;
    public string ReceiptLabel => Seen ? "Seen" : "Sent";
    partial void OnSeenChanged(bool value) => OnPropertyChanged(nameof(ReceiptLabel));

    public string Id => Message.Id;
    public Guid Sender => Message.Sender;
    public Guid Receiver => Message.Receiver;
    public string? Text => Message.IsDeleted == true ? "Message deleted" : Message.CallSummary != null ? null : Message.Text;
    public bool IsCall => Message.CallSummary != null;
    public bool CanJoinCall => Message.CallSummary?.IsActive == true;
    public string CallLabel => Message.CallSummary is { } call ? $"☎ {call.CallType} call · {call.Status}" + (call.DurationSeconds is {} seconds ? $" · {TimeSpan.FromSeconds(seconds):mm\\:ss}" : "") : "";
    public bool CanEdit => CanModify && !Message.IsLocked;
    public bool CanModify => IsMine && !IsCall && Message.IsDeleted != true;
    public string ReplyLabel => Message.ReplyToMessageId != null ? "↩ Reply to " + (Message.ReplyToSenderName ?? "message") : "";
    public bool HasReply => Message.ReplyToMessageId != null;
    public bool HasReactions => Message.Reactions?.Count > 0;
    public string ReactionLabel => string.Join("  ", Message.Reactions?.GroupBy(x => x.Emoji).Select(x => x.Key + " " + x.Count()) ?? Array.Empty<string>());
    public bool Edited => Message.IsEdited == true;
    public DateTime TimeSent => Message.TimeSent;
    public bool HasText => !string.IsNullOrWhiteSpace(Text);
    public bool HasBubble => HasText || HasReply;
    public bool HasAttachments => Attachments.Count > 0;
    public string AttachmentSummary => $"{Attachments.Count} " + (Attachments.All(x => x.IsImage) ? "photos" : Attachments.All(x => x.IsVideo) ? "videos" : Attachments.All(x => x.IsAudio) ? "audio files" : "attachments");

    public ObservableCollection<ChatAttachmentItemViewModel> Attachments { get; } = new();

    public ChatMessageItemViewModel(
        ChatMessage message,
        bool isMine,
        IChatService chatService,
        IFileService fileService,
        IImageService imageService,
        bool isNew = false)
    {
        this.fileService = fileService;
        Message = message;
        IsMine = isMine;
        _isNew = isNew;
        _seen = message.Seen;

        if (message.Attachments != null)
        {
            foreach (var att in message.Attachments)
            {
                Attachments.Add(new ChatAttachmentItemViewModel(att, chatService, fileService, imageService) { ReceiptMessageId=message.MessageId, CanReportPlayback=!isMine && !message.SuppressReadReceipts });
            }
        }
    }

    [RelayCommand]
    public async Task DownloadAllAsync()
    {
        AttachmentError = null;
        try
        {
            var destination = await fileService.PickSaveFileAsync("Attachments.zip", "Download All");
            if (destination != null) await AttachmentArchive.SaveAsync(Attachments, destination);
        }
        catch (Exception error) { AttachmentError = error.Message; }
    }

    partial void OnIsNewChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIncoming));
    }
}

public partial class PendingAttachmentViewModel : ObservableObject
{
    public string FilePath { get; }
    public string FileName { get; }
    public string FormattedFileSize { get; }
    public bool IsImage { get; }

    [ObservableProperty]
    private Bitmap? _previewThumbnail;

    public PendingAttachmentViewModel(string filePath, IImageService imageService)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        IsImage = ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp";

        var fi = new FileInfo(filePath);
        var bytes = fi.Exists ? fi.Length : 0;
        if (bytes < 1024) FormattedFileSize = $"{bytes} B";
        else if (bytes < 1024 * 1024) FormattedFileSize = $"{bytes / 1024.0:F1} KB";
        else FormattedFileSize = $"{bytes / (1024.0 * 1024.0):F1} MB";

        if (IsImage)
        {
            PreviewThumbnail = imageService.LoadLocalImage(filePath);
        }
    }
}

public partial class UserSearchItemViewModel : ObservableObject
{
    public SystemUserSearch User { get; }

    [ObservableProperty]
    private Bitmap? _avatar;

    public string Id => User.Id;
    public Guid UserId => Guid.TryParse(User.Id, out var g) ? g : Guid.Empty;
    public string FullName => User.FullName;
    public string Initials => User.Initials;

    public UserSearchItemViewModel(SystemUserSearch user, IImageService imageService)
    {
        User = user;
        if (!string.IsNullOrEmpty(user.CompressedBase64Profile))
        {
            Avatar = imageService.GetBitmapFromBase64(user.CompressedBase64Profile);
        }
    }
}
