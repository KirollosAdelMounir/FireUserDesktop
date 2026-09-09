using System;
using System.Collections.ObjectModel;
using System.IO;
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

    [ObservableProperty]
    private string _receiverName = string.Empty;

    [ObservableProperty]
    private string? _lastMessage;

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

    public string StatusLabel => ChatViewModel.GetStatusLabel(Status);
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
            ReceiverName = dto.ReceiverName,
            LastMessage = dto.LastMessage,
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

        if (IsImage)
        {
            _ = LoadThumbnailAsync();
        }
    }

    private async Task LoadThumbnailAsync()
    {
        Thumbnail = await _imageService.GetAttachmentThumbnailAsync(Attachment.Id);
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
                var success = await _chatService.SaveAttachmentToFileAsync(Attachment.Id, savePath);
                if (success)
                {
                    _fileService.OpenFileWithDefaultApp(savePath);
                }
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
    public ChatMessage Message { get; }
    public bool IsMine { get; }
    public bool IsIncoming => !IsMine && IsNew;

    [ObservableProperty]
    private bool _isNew;

    [ObservableProperty]
    private bool _seen;

    public string Id => Message.Id;
    public Guid Sender => Message.Sender;
    public Guid Receiver => Message.Receiver;
    public string? Text => Message.Text;
    public DateTime TimeSent => Message.TimeSent;
    public bool HasText => !string.IsNullOrWhiteSpace(Text);
    public bool HasAttachments => Attachments.Count > 0;

    public ObservableCollection<ChatAttachmentItemViewModel> Attachments { get; } = new();

    public ChatMessageItemViewModel(
        ChatMessage message,
        bool isMine,
        IChatService chatService,
        IFileService fileService,
        IImageService imageService,
        bool isNew = false)
    {
        Message = message;
        IsMine = isMine;
        _isNew = isNew;
        _seen = message.Seen;

        if (message.Attachments != null)
        {
            foreach (var att in message.Attachments)
            {
                Attachments.Add(new ChatAttachmentItemViewModel(att, chatService, fileService, imageService));
            }
        }
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
