using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.AspNetCore.SignalR.Client;
using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.Core;

namespace UserIdentityDesktop.ViewModels;

public partial class ChatViewModel : ViewModelBase
{
    public WorkspaceViewModel? Workspace { get; internal set; }
    private readonly IAuthService _authService;
    private readonly IChatService _chatService;
    private readonly IUserService _userService;
    private readonly IImageService _imageService;
    private readonly IFileService _fileService;
    private readonly INotificationService _notificationService;

    private Timer? _typingTimer;
    private Timer? _remoteTypingResetTimer;
    private Timer? _searchDebounceTimer;
    private bool _isCurrentlyTypingNotified;
    private bool _isRefreshingRecipientList;
    private long _searchVersion;
    private long _profileLoadVersion;
    private string? activeConversationKey;
    private long historyVersion;
    private sealed record ConversationSnapshot(List<ChatMessageItemViewModel> Items, int Page, bool HasMore);
    private readonly Dictionary<string, ConversationSnapshot> historyCache = new();
    private readonly HashSet<string> loadedHistory = new();

    [ObservableProperty]
    private UserData _currentUser;

    [ObservableProperty]
    private Bitmap? _myAvatar;

    [ObservableProperty]
    private RecipientItemViewModel? _selectedRecipient;

    [ObservableProperty]
    private string _messageDraft = string.Empty;

    [ObservableProperty]
    private bool _isOtherPartyTyping;

    // Backend Loading State Indicators
    [ObservableProperty]
    private bool _isLoadingRecipients;

    [ObservableProperty]
    private bool _isLoadingMessages;

    [ObservableProperty]
    private bool _isLoadingMore;

    [ObservableProperty]
    private bool _hasMoreMessages;

    [ObservableProperty]
    private int _currentPage;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _connectionStatusText = "Connecting...";

    [ObservableProperty]
    private UserStatus _currentUserStatus = UserStatus.Online;

    // Sidebar local conversation filter
    [ObservableProperty]
    private string _searchText = string.Empty;

    // Angular-style Identity Search Bar & Results Dropdown
    [ObservableProperty]
    private string _identitySearchText = string.Empty;

    [ObservableProperty]
    private bool _showSearchDropdown;

    [ObservableProperty]
    private bool _isSearching;

    // Angular-style View Profile Modal & Quick Message Overlay
    [ObservableProperty]
    private bool _isProfileOverlayOpen;

    [ObservableProperty]
    private Guid _profileUserId;

    [ObservableProperty]
    private string _profileUserName = string.Empty;

    [ObservableProperty]
    private ViewUserProfileDto? _profileData;

    [ObservableProperty]
    private Bitmap? _profileAvatar;

    [ObservableProperty]
    private string _quickMessageText = string.Empty;

    [ObservableProperty]
    private string? _quickMessageStatus;

    [ObservableProperty]
    private bool _isLoadingProfile;

    [ObservableProperty]
    private bool _isSendingQuickMessage;

    [ObservableProperty]
    private bool _isImagePreviewOpen;

    [ObservableProperty]
    private bool _isSendingMessage;
    [ObservableProperty] private string? _sendError;

    public ObservableCollection<RecipientItemViewModel> Recipients { get; } = new();
    public ObservableCollection<RecipientItemViewModel> FilteredRecipients { get; } = new();
    public ObservableCollection<ChatMessageItemViewModel> Messages { get; } = new();
    public ObservableCollection<PendingAttachmentViewModel> PendingAttachments { get; } = new();
    public ObservableCollection<UserSearchItemViewModel> SearchResults { get; } = new();

    public IReadOnlyList<StatusOption> StatusOptions { get; } = new List<StatusOption>
    {
        new(UserStatus.Online, "Online", "#22C55E"),
        new(UserStatus.Away, "Away", "#F59E0B"),
        new(UserStatus.Busy, "Busy", "#F97316"),
        new(UserStatus.DoNotDisturb, "Do Not Disturb", "#EF4444"),
        new(UserStatus.InCall, "In a call", "#8B5CF6"),
        new(UserStatus.InMeeting, "In a meeting", "#8B5CF6"),
        new(UserStatus.Presenting, "Presenting", "#06B6D4"),
        new(UserStatus.SharingScreen, "Sharing Screen", "#06B6D4")
    };

    public string SelectedRecipientInitials => SelectedRecipient?.Initials ?? "?";
    public Bitmap? SelectedRecipientAvatar => SelectedRecipient?.Avatar;
    public string SelectedRecipientName => SelectedRecipient?.ReceiverName ?? string.Empty;
    partial void OnIsSearchingChanged(bool value)=>OnPropertyChanged(nameof(NoPeopleSearchResults));
    public bool NoPeopleSearchResults => !IsSearching && SearchResults.Count==0;
    private readonly Dictionary<Guid,long> presenceVersions=new();
    public bool SelectedRecipientHasMobileConnection => SelectedRecipient?.HasMobileConnection == true;
    public UserStatus SelectedRecipientStatus => SelectedRecipient?.Status ?? UserStatus.Offline;
    public bool SelectedRecipientHasPresence => SelectedRecipient is {IsGroup:false};
    public string SelectedRecipientStatusLabel => SelectedRecipient?.StatusLabel ?? string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasProfileOrganization))] private ProfileOrganizationViewModel? _profileOrganization;
    public bool HasProfileOrganization => ProfileOrganization != null;
    public string ProfileInitials => string.Concat(ProfileUserName.Split(' ',StringSplitOptions.RemoveEmptyEntries).Take(2).Select(x=>x[0])).ToUpperInvariant();
    partial void OnProfileUserNameChanged(string value) => OnPropertyChanged(nameof(ProfileInitials));
    public string ProfileEmailText => ProfileData?.Email ?? "Loading...";
    public string ProfilePhoneText => ProfileData?.FormattedPhone ?? "Not specified";
    public string ProfileAddressText => ProfileData?.FormattedAddress ?? "Not specified";
    public string CurrentStatusLabel => GetStatusLabel(CurrentUserStatus);
    public IBrush CurrentStatusColor => GetStatusColor(CurrentUserStatus);

    public event Action? RequestScrollToBottom;
    public event Action? LogoutRequested;
    public Func<Task>? BeforeLogoutAsync { get; set; }

    public ChatViewModel(
        IAuthService authService,
        IChatService chatService,
        IUserService userService,
        IImageService imageService,
        IFileService fileService,
        INotificationService notificationService)
    {
        _authService = authService;
        _chatService = chatService;
        _userService = userService;
        _imageService = imageService;
        _fileService = fileService;
        _notificationService = notificationService;

        _currentUser = _authService.CurrentUser ?? new UserData();

        activityExpiry.Tick += (_, _) =>
        {
            foreach (var key in activities.Where(x => x.Value.Expires <= DateTime.UtcNow).Select(x => x.Key).ToArray()) activities.Remove(key);
            RefreshRecipientActivity();OnPropertyChanged(nameof(ChatActivityLabel));OnPropertyChanged(nameof(HasChatActivity));OnPropertyChanged(nameof(IsRecordingActivity));
        };
        activityExpiry.Start();
        InitializeVoiceRecording();
        RegisterEvents();

    }

    public async Task InitializeAsync()
    {
        await Task.WhenAll(LoadMyAvatarAsync(), _chatService.ConnectAsync(), _userService.ConnectAsync());

        var presence = await _chatService.GetUserPresenceAsync(CurrentUser.Id);
        if (presence != null)
            CurrentUserStatus = presence.Status;

        await LoadRecipientsAsync();
    }

    private void RegisterEvents()
    {
        _chatService.MessageReceived += OnMessageReceived;
        _chatService.AttachmentPlayed += OnAttachmentPlayed;
        _chatService.MessageUpdated += OnMessageUpdated; _chatService.MessageDeleted += OnMessageDeleted;
        _chatService.RecipientsUpdated += OnRecipientsUpdated;
        _chatService.ReadReceiptReceived += OnReadReceipt;
        _chatService.TypingReceived += OnTypingReceived;
        _chatService.ChatActivityReceived += OnChatActivity;
        _chatService.ConversationTypingReceived += OnConversationTyping;
        _chatService.UserPresenceChanged += OnUserPresenceChanged;
        _chatService.UserStatusChanged += OnUserStatusChanged;
        _chatService.ConnectionStateChanged += OnConnectionStateChanged;
        _userService.SearchResultReceived += OnSearchResultReceived;
    }

    public void UnregisterEvents()
    {
        _chatService.MessageReceived -= OnMessageReceived;
        _chatService.AttachmentPlayed -= OnAttachmentPlayed;
        _chatService.MessageUpdated -= OnMessageUpdated; _chatService.MessageDeleted -= OnMessageDeleted;
        _chatService.RecipientsUpdated -= OnRecipientsUpdated;
        _chatService.ReadReceiptReceived -= OnReadReceipt;
        _chatService.TypingReceived -= OnTypingReceived;
        _chatService.ChatActivityReceived -= OnChatActivity;
        _chatService.ConversationTypingReceived -= OnConversationTyping;
        activityExpiry.Stop();
        _ = CancelVoiceRecording();
        foreach (var note in recordedNotes) _ = note.DisposeAsync();
        recordedNotes.Clear();
        _chatService.UserPresenceChanged -= OnUserPresenceChanged;
        _chatService.UserStatusChanged -= OnUserStatusChanged;
        _chatService.ConnectionStateChanged -= OnConnectionStateChanged;
        _userService.SearchResultReceived -= OnSearchResultReceived;

        _typingTimer?.Dispose();
        _remoteTypingResetTimer?.Dispose();
        _searchDebounceTimer?.Dispose();
    }

    private async Task LoadMyAvatarAsync()
    {
        if (CurrentUser.Id != Guid.Empty)
        {
            MyAvatar = await _imageService.GetProfilePictureAsync(CurrentUser.Id);
        }
    }

    public async Task LoadRecipientsAsync()
    {
        IsLoadingRecipients = true;
        try
        {
            var list = await _chatService.GetRecipientsAsync();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ApplyRecipientUpdates(list);
            });
        }
        finally
        {
            Dispatcher.UIThread.Post(() => IsLoadingRecipients = false);
        }
    }

    private async Task LoadRecipientAvatarAsync(RecipientItemViewModel recipient)
    {
        var avatar = await _imageService.GetProfilePictureAsync(recipient.ReceiverId);
        if (avatar != null)
        {
            Dispatcher.UIThread.Post(() => recipient.Avatar = avatar);
        }
    }

    internal async Task LoadRecipientPresenceAsync(RecipientItemViewModel recipient)
    {
        if(recipient.IsGroup)return;
        var version=presenceVersions.GetValueOrDefault(recipient.ReceiverId);
        var presence = await _chatService.GetUserPresenceAsync(recipient.ReceiverId);
        if (presence != null && presence.UserId==recipient.ReceiverId)
            Dispatcher.UIThread.Post(() => { if(presenceVersions.GetValueOrDefault(recipient.ReceiverId)==version){recipient.Status=presence.Status;recipient.HasMobileConnection=presence.HasMobileConnection;} });
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var selected = SelectedRecipient;
        var query = SearchText?.Trim() ?? string.Empty;

        var items = (string.IsNullOrEmpty(query)
            ? Recipients
            : Recipients.Where(r =>
                r.ReceiverName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (r.LastMessage != null && r.LastMessage.Contains(query, StringComparison.OrdinalIgnoreCase)))).ToList();

        _isRefreshingRecipientList = true;
        for (var index = FilteredRecipients.Count - 1; index >= 0; index--)
        {
            if (!items.Contains(FilteredRecipients[index]))
                FilteredRecipients.RemoveAt(index);
        }

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var currentIndex = FilteredRecipients.IndexOf(item);
            if (currentIndex < 0)
                FilteredRecipients.Insert(index, item);
            else if (currentIndex != index)
                FilteredRecipients.Move(currentIndex, index);
        }

        if (selected != null && SelectedRecipient == null)
            SelectedRecipient = selected;
        _isRefreshingRecipientList = false;
    }

    partial void OnIdentitySearchTextChanged(string value)
    {
        _searchDebounceTimer?.Dispose();

        if (string.IsNullOrWhiteSpace(value))
        {
            Interlocked.Increment(ref _searchVersion);
            ShowSearchDropdown = false;
            SearchResults.Clear();
            IsSearching = false;
            return;
        }

        ShowSearchDropdown = true;
        IsSearching = true;
        var version = Interlocked.Increment(ref _searchVersion);

        // Debounce 300ms to preserve performance on low-end Linux machines
        _searchDebounceTimer = new Timer(async _ =>
        {
            await PerformUserSearchAsync(value.Trim(), version);
        }, null, 300, Timeout.Infinite);
    }

    private async Task PerformUserSearchAsync(string query, long version)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            Dispatcher.UIThread.Post(() => IsSearching = false);
            return;
        }

        try
        {
            await _userService.SearchUsersAsync(query);
            if (version != Volatile.Read(ref _searchVersion)) return;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Search error: {ex.Message}");
            Dispatcher.UIThread.Post(() => IsSearching = false);
        }
    }

    private void OnSearchResultReceived(string query, List<SystemUserSearch> results)
    {
        var currentQuery = IdentitySearchText.Trim();
        Dispatcher.UIThread.Post(() =>
        {
            if (!string.Equals(query, currentQuery, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(IdentitySearchText.Trim(), query, StringComparison.OrdinalIgnoreCase))
                return;

            SearchResults.Clear();
            var queryText = IdentitySearchText.Trim();
            var rankedResults = results
                .Where(user => Guid.TryParse(user.Id, out var id) && id != CurrentUser.Id)
                .DistinctBy(user => user.Id)
                .OrderByDescending(user => string.Equals(user.FullName, queryText, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(user => user.FullName.StartsWith(queryText, StringComparison.OrdinalIgnoreCase))
                .ThenBy(user => user.FullName, StringComparer.OrdinalIgnoreCase);

            foreach (var user in rankedResults)
            {
                SearchResults.Add(new UserSearchItemViewModel(user, _imageService));
            }
            IsSearching = false;
            OnPropertyChanged(nameof(NoPeopleSearchResults));
        });
    }

    [RelayCommand]
    public void ClearIdentitySearch()
    {
        IdentitySearchText = string.Empty;
        ShowSearchDropdown = false;
        SearchResults.Clear();
        IsSearching = false;
    }

    [RelayCommand]
    public void CloseSearchDropdown()
    {
        ShowSearchDropdown = false;
    }

    partial void OnSelectedRecipientChanged(RecipientItemViewModel? oldValue, RecipientItemViewModel? newValue)
    {
        if (oldValue != null) oldValue.PropertyChanged -= OnSelectedRecipientPropertyChanged;
        if (newValue != null) newValue.PropertyChanged += OnSelectedRecipientPropertyChanged;
        OnPropertyChanged(nameof(SelectedRecipientInitials));
        OnPropertyChanged(nameof(SelectedRecipientAvatar));
        OnPropertyChanged(nameof(SelectedRecipientName));
        OnPropertyChanged(nameof(SelectedRecipientStatusLabel));
        OnPropertyChanged(nameof(SelectedRecipientStatus));OnPropertyChanged(nameof(SelectedRecipientHasPresence));OnPropertyChanged(nameof(SelectedRecipientHasMobileConnection));
        if (newValue != null)
            _ = LoadRecipientPresenceAsync(newValue);

        if (newValue == null && _isRefreshingRecipientList)
            return;
        var key = newValue == null ? null : newValue.ConversationId ?? newValue.ReceiverId.ToString();
        if (key == activeConversationKey) return;
        CancelMessageAction();

        if (oldValue != null) oldValue.IsSelected = false;
        if (newValue != null) newValue.IsSelected = true;

        if (oldValue != null && _isCurrentlyTypingNotified)
        {
            _isCurrentlyTypingNotified = false;
            _ = SendTyping(oldValue, false);
        }
        if (activeConversationKey is {} previous && loadedHistory.Contains(previous))
            historyCache[previous] = new(Messages.ToList(), CurrentPage, HasMoreMessages);
        _ = CancelVoiceRecording();
        OnPropertyChanged(nameof(ChatActivityLabel));OnPropertyChanged(nameof(HasChatActivity));OnPropertyChanged(nameof(IsRecordingActivity));
        activeConversationKey = key;
        ++historyVersion;

        IsOtherPartyTyping = false;
        Messages.Clear();

        if (newValue == null) { IsLoadingMessages = IsLoadingMore = false; return; }
        SendError = null;

        // 1. Immediately mark as seen in UI and declare seen to backend/opposite user
        newValue.IsSeen = true;
        _ = MarkConversationSeenAsync(newValue);
        if (key != null && historyCache.TryGetValue(key, out var cached))
        {
            foreach (var item in cached.Items) Messages.Add(item);
            CurrentPage = cached.Page; HasMoreMessages = cached.HasMore;
            IsLoadingMessages = IsLoadingMore = false;
            RequestScrollToBottom?.Invoke(); return;
        }

        // Load once; incoming messages and local activity update read receipts.
        CurrentPage = 0;
        HasMoreMessages = true;
        _ = LoadChatHistoryAsync(newValue.ReceiverId, 0);
    }

    internal async Task MarkConversationSeenAsync(RecipientItemViewModel recipient)
    {
        try
        {
            if (recipient.IsGroup) await _chatService.MarkConversationSeenAsync(recipient.ConversationId!);
            else await _chatService.MarkSeenAsync(recipient.ReceiverId);
        }
        catch (OperationCanceledException) { /* Navigation/sign-out can cancel a queued read receipt. */ }
        catch (Exception error)
        {
            Dispatcher.UIThread.Post(() => { if (SelectedRecipient == recipient) SendError = "Unable to update read status. " + error.Message; });
        }
    }

    private void OnSelectedRecipientPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if(e.PropertyName == nameof(RecipientItemViewModel.HasMobileConnection))OnPropertyChanged(nameof(SelectedRecipientHasMobileConnection));
        if(e.PropertyName == nameof(RecipientItemViewModel.Status))OnPropertyChanged(nameof(SelectedRecipientStatus));
        if (e.PropertyName == nameof(RecipientItemViewModel.StatusLabel)) OnPropertyChanged(nameof(SelectedRecipientStatusLabel));
        if (e.PropertyName == nameof(RecipientItemViewModel.ReceiverName)) OnPropertyChanged(nameof(SelectedRecipientName));
        if (e.PropertyName == nameof(RecipientItemViewModel.Initials)) OnPropertyChanged(nameof(SelectedRecipientInitials));
        if (e.PropertyName == nameof(RecipientItemViewModel.Avatar)) OnPropertyChanged(nameof(SelectedRecipientAvatar));
    }

    private async Task LoadChatHistoryAsync(Guid receiverId, int page, bool recover = false)
    {
        var version = historyVersion;
        var conversationId = SelectedRecipient?.ConversationId;
        if (page == 0)
        {
            IsLoadingMessages = true;
        }
        else
        {
            IsLoadingMore = true;
        }

        try
        {
            var history = conversationId == null ? await _chatService.GetChatHistoryAsync(receiverId, page) : await _chatService.GetConversationHistoryAsync(conversationId, page);


            // Angular sorts messages by timeSent ascending
            var sorted = history.OrderBy(m => m.TimeSent).ToList();

            Dispatcher.UIThread.Post(() =>
            {
                if (version != historyVersion || SelectedRecipient?.ReceiverId != receiverId || SelectedRecipient.ConversationId != conversationId) return;
                HasMoreMessages = history.Count >= 10;
                if (activeConversationKey != null) loadedHistory.Add(activeConversationKey);

                if (page == 0)
                {
                    foreach (var msg in sorted)
                    {
                        var isMine = msg.Sender == CurrentUser.Id;
                        var existing = Messages.FirstOrDefault(x => x.Id == msg.Id);
                        if (existing != null)
                        {
                            if (recover) Messages[Messages.IndexOf(existing)] = new ChatMessageItemViewModel(msg, isMine, _chatService, _fileService, _imageService);
                            continue;
                        }
                        var index = Messages.TakeWhile(x => x.TimeSent <= msg.TimeSent).Count();
                        Messages.Insert(index, new ChatMessageItemViewModel(msg, isMine, _chatService, _fileService, _imageService));
                    }
                    RequestScrollToBottom?.Invoke();
                }
                else
                {
                    var index = 0;
                    foreach (var msg in sorted)
                    {
                        var isMine = msg.Sender == CurrentUser.Id;
                        if (Messages.Any(x => x.Id == msg.Id)) continue;
                        Messages.Insert(index++, new ChatMessageItemViewModel(msg, isMine, _chatService, _fileService, _imageService));
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading chat history: {ex.Message}");
        }
        finally
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (version != historyVersion) return;
                IsLoadingMessages = false;
                IsLoadingMore = false;
            });
        }
    }

    [RelayCommand]
    public void SelectRecipient(RecipientItemViewModel recipient)
    {
        SelectedRecipient = recipient;
    }

    [RelayCommand]
    public async Task LoadMoreMessagesAsync()
    {
        if (IsLoadingMore || !HasMoreMessages || SelectedRecipient == null) return;

        CurrentPage++;
        await LoadChatHistoryAsync(SelectedRecipient.ReceiverId, CurrentPage);
    }

    partial void OnMessageDraftChanged(string value)
    {
        if (SelectedRecipient == null) return;

        var isTyping = !VoiceComposerActive && !string.IsNullOrWhiteSpace(value);
        if (isTyping != _isCurrentlyTypingNotified)
        {
            _isCurrentlyTypingNotified = isTyping;
            _ = SendTyping(SelectedRecipient, isTyping);
            _ = MarkConversationSeenAsync(SelectedRecipient);
        }

        if (isTyping)
        {
            _typingTimer?.Dispose();
            _typingTimer = new Timer(async _ =>
            {
                if (_isCurrentlyTypingNotified && SelectedRecipient != null)
                {
                    _isCurrentlyTypingNotified = false;
                    await SendTyping(SelectedRecipient, false);
                }
            }, null, 3000, Timeout.Infinite);
        }
    }

    [RelayCommand(AllowConcurrentExecutions=true)]
    public async Task SendMessageAsync()
    {
        if (SelectedRecipient == null || IsSendingMessage || IsRecordingVoice || IsPreparingVoice || IsOpeningMicrophone) return;
        var voicePath = VoiceNotePath;
        var text = voicePath != null ? null : MessageDraft?.Trim();
        var hasAttachments = voicePath != null || PendingAttachments.Count > 0;

        if (string.IsNullOrEmpty(text) && !hasAttachments)
            return;

        var queuedOffline=_authService.Actions?.IsConnected==false;
        IsSendingMessage = !queuedOffline;
        IsUploadingAttachments = hasAttachments && !queuedOffline;
        var sendingTo = SelectedRecipient;
        var recipientId = sendingTo.ReceiverId;
        var filesToSend = voicePath != null ? new List<string> { voicePath } : PendingAttachments.Select(p => p.FilePath).ToList();

        var originalDraft = MessageDraft;
        if(queuedOffline && voicePath==null && !hasAttachments && EditingMessage==null)MessageDraft="";
        var replyTo = ReplyingMessage?.Id;
        SendError = null;

        if (_isCurrentlyTypingNotified)
        {
            _isCurrentlyTypingNotified = false;
            _ = SendTyping(SelectedRecipient, false);
        }

        var kind = UploadActivityKind(filesToSend);
        using var uploadActivity = hasAttachments ? _chatService.BeginChatActivity(sendingTo.IsGroup ? null : sendingTo.ReceiverId, sendingTo.ConversationId, kind) : null;
        try
        {
            if (voicePath != null) await UserIdentityDesktop.Service.Media.AudioNotePlayback.StopAsync(voicePath);
            var success = false;
            if (EditingMessage != null)
            {
                if (hasAttachments) throw new InvalidOperationException("Remove pending uploads before editing a message.");
                await _chatService.EditMessageAsync(EditingMessage.Message, text ?? ""); success = true;
            }
            else if (hasAttachments)
            {
                success = await _chatService.SendMessageWithAttachmentsAsync(recipientId, text, filesToSend, sendingTo.ConversationId, replyTo);
            }
            else if (sendingTo.IsGroup)
            {
                success = await _chatService.SendConversationAsync(sendingTo.ConversationId!, text ?? "", ReplyingMessage?.Id);
            }
            else if (ReplyingMessage != null)
            {
                success = await _chatService.SendReplyAsync(recipientId, text ?? "", ReplyingMessage.Id);
            }
            else if (!string.IsNullOrEmpty(text))
            {
                success = await _chatService.SendMessageAsync(recipientId, text);
            }
            if (success)
            {
                if (SelectedRecipient == sendingTo) CancelMessageAction();
                if (voicePath == null && SelectedRecipient?.ReceiverId == recipientId && MessageDraft == originalDraft) MessageDraft = string.Empty;
                if (voicePath != null && VoiceNotePath == voicePath) VoiceNotePath = null;
                foreach (var pending in PendingAttachments.Where(x => filesToSend.Contains(x.FilePath)).ToList()) PendingAttachments.Remove(pending);
            }
            else SendError = "Message was not sent. Your draft is retained. Check your connection and unlock chat in Media settings if a PIN is required.";
        }
        catch (Exception ex) { SendError = ex.Message;if(queuedOffline && string.IsNullOrEmpty(MessageDraft) && SelectedRecipient==sendingTo)MessageDraft=originalDraft??""; }
        finally
        {
            IsSendingMessage = false;
            IsUploadingAttachments = false;
            await CleanupDetachedVoiceNotes();
        }
    }

    [RelayCommand]
    public async Task AttachFilesAsync()
    {
        if (VoiceComposerActive || IsSendingMessage) return;
        var picked = await _fileService.PickFilesAsync("Select files to attach", allowMultiple: true);
        if (VoiceComposerActive) return;
        foreach (var file in picked)
        {
            if (!PendingAttachments.Any(p => p.FilePath == file))
            {
                PendingAttachments.Add(new PendingAttachmentViewModel(file, _imageService));
            }
        }
    }

    [RelayCommand]
    public void RemovePendingAttachment(PendingAttachmentViewModel item)
    {
        PendingAttachments.Remove(item);
        _ = CleanupDetachedVoiceNotes();
    }

    // --- Angular-Style User Profile & Quick Message Modal ---

    [RelayCommand]
    private void StartChatFromSearch(UserSearchItemViewModel? item)
    {
        if (item == null || item.UserId == Guid.Empty || item.UserId == CurrentUser.Id) return;
        CloseUserProfile();
        SearchText = string.Empty;
        StartChatWithUser(item.UserId, item.FullName, item.Avatar);
        ClearIdentitySearch();
        if (Workspace != null) Workspace.SelectedWorkspaceTab = 0;
    }

    [RelayCommand]
    public async Task OpenUserProfileAsync(UserSearchItemViewModel item)
    {
        var version = ++_profileLoadVersion;
        ShowSearchDropdown = false;
        ProfileUserId = item.UserId;
        ProfileUserName = item.FullName;
        ProfileAvatar = item.Avatar;
        ProfileData = null;
        QuickMessageText = string.Empty;
        QuickMessageStatus = null;
        IsProfileOverlayOpen = true;
        IsLoadingProfile = true;

        try
        {
            var profile = await _userService.GetUserProfileAsync(item.UserId);
            if (version != _profileLoadVersion || !IsProfileOverlayOpen) return;
            ProfileData = profile;

            if (ProfileAvatar == null && !string.IsNullOrEmpty(profile?.ProfilePicture))
            {
                ProfileAvatar = _imageService.GetBitmapFromBase64(profile.ProfilePicture);
            }
            if (ProfileAvatar == null)
            {
                var avatar = await _imageService.GetProfilePictureAsync(item.UserId);
                if (version == _profileLoadVersion && IsProfileOverlayOpen) ProfileAvatar = avatar;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading user profile: {ex.Message}");
        }
        finally
        {
            if (version == _profileLoadVersion) IsLoadingProfile = false;
        }
    }

    [RelayCommand]
    public void CloseUserProfile()
    {
        ++_profileLoadVersion;
        IsLoadingProfile = false;
        IsProfileOverlayOpen = false;
        IsImagePreviewOpen = false;
        QuickMessageText = string.Empty;
        QuickMessageStatus = null;
    }

    [RelayCommand]
    public void OpenImagePreview()
    {
        if (ProfileAvatar != null)
        {
            IsImagePreviewOpen = true;
        }
    }

    [RelayCommand]
    public void CloseImagePreview()
    {
        IsImagePreviewOpen = false;
    }

    [RelayCommand]
    public async Task SendQuickMessageAsync()
    {
        var text = QuickMessageText?.Trim();
        if (string.IsNullOrEmpty(text) || ProfileUserId == Guid.Empty || IsSendingQuickMessage) return;

        IsSendingQuickMessage = true;
        QuickMessageStatus = "Sending flame message...";

        try
        {
            var success = await _chatService.SendMessageAsync(ProfileUserId, text);
            if (success)
            {
                QuickMessageStatus = "✓ Flame message sent!";
                QuickMessageText = string.Empty;

                // Ensure user is opened in conversations
                StartChatWithUser(ProfileUserId, ProfileUserName, ProfileAvatar);

                await Task.Delay(800);
                CloseUserProfile();
            }
            else
            {
                QuickMessageStatus = "Could not send message. Please try again.";
            }
        }
        catch (Exception ex)
        {
            QuickMessageStatus = $"Error: {ex.Message}";
        }
        finally
        {
            IsSendingQuickMessage = false;
        }
    }

    [RelayCommand]
    public void StartChatFromProfile()
    {
        if (ProfileUserId == Guid.Empty) return;
        StartChatWithUser(ProfileUserId, ProfileUserName, ProfileAvatar);
        CloseUserProfile();
    }

    public void StartChatWithUser(Guid userId, string name, Bitmap? avatar)
    {
        var existing = Recipients.FirstOrDefault(r => !r.IsGroup && r.ReceiverId == userId);
        if (existing == null)
        {
            existing = new RecipientItemViewModel
            {
                ReceiverId = userId,
                ReceiverName = name,
                LastMessage = null,
                LastSent = DateTime.UtcNow,
                IsSeen = true,
                Avatar = avatar
            };
            Recipients.Insert(0, existing);
            ApplyFilter();
        }

        SelectedRecipient = existing;
    }

    private void OnMessageReceived(ChatMessage message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var isCurrentChat = SelectedRecipient != null && (message.ConversationId != null
                ? message.ConversationId == SelectedRecipient.ConversationId
                : !SelectedRecipient.IsGroup && (message.Sender == SelectedRecipient.ReceiverId || (message.Sender == CurrentUser.Id && message.Receiver == SelectedRecipient.ReceiverId)));
                var isMine = message.Sender == CurrentUser.Id;
            if (isCurrentChat && !Messages.Any(x => x.Id == message.Id))
            {
                Messages.Add(new ChatMessageItemViewModel(message, isMine, _chatService, _fileService, _imageService, isNew: !isMine));
                _ = ClearIncomingAnimationAsync(Messages.LastOrDefault());
                RequestScrollToBottom?.Invoke();

                if (!isMine && SelectedRecipient != null)
                {
                    _ = MarkConversationSeenAsync(SelectedRecipient);
                }
            }
            else if (!isCurrentChat)
            {
                var cacheKey = message.ConversationId ?? (isMine ? message.Receiver : message.Sender).ToString();
                if (historyCache.TryGetValue(cacheKey, out var cache) && !cache.Items.Any(x => x.Id == message.Id))
                    cache.Items.Add(new ChatMessageItemViewModel(message, isMine, _chatService, _fileService, _imageService));
            }

            // Update recipient item in sidebar
            var otherPartyId = message.Sender == CurrentUser.Id ? message.Receiver : message.Sender;
            var recipient = Recipients.FirstOrDefault(r => message.ConversationId != null ? r.ConversationId == message.ConversationId : !r.IsGroup && r.ReceiverId == otherPartyId);

            if (recipient != null)
            {
                recipient.LastMessage = message.Text;
                recipient.AttachmentsCount = message.Attachments?.Count ?? 0;
                recipient.LastSent = message.TimeSent;
                recipient.IsSeen = isCurrentChat || message.Sender == CurrentUser.Id;

                // Move recipient to top
                var idx = Recipients.IndexOf(recipient);
                if (idx > 0)
                {
                    Recipients.Move(idx, 0);
                    ApplyFilter();
                }
            }
            else
            {
                _ = LoadRecipientsAsync();
            }

            // Trigger Desktop Notification for incoming message on Windows, Linux, and macOS
            if (!isMine && message.CallSummary == null)
            {
                var notificationText = !string.IsNullOrWhiteSpace(message.Text)
                    ? message.Text
                    : (message.Attachments != null && message.Attachments.Count > 0
                        ? $"[Attachment: {message.Attachments[0].FileName}]"
                        : "Sent you a message");

                if (Workspace?.HandleCallMessage(message) != true) _ = NotifyIncomingMessageAsync(message, recipient, notificationText);
            }
        });
    }

    private static async Task ClearIncomingAnimationAsync(ChatMessageItemViewModel? message)
    {
        if (message == null)
            return;

        await Task.Delay(40);
        Dispatcher.UIThread.Post(() => message.IsNew = false);
    }

    partial void OnProfileDataChanged(ViewUserProfileDto? value)
    {
        OnPropertyChanged(nameof(ProfileEmailText));
        ProfileOrganization = value?.Organization is {} organization ? new ProfileOrganizationViewModel(organization, ProfileUserName) : null;
        OnPropertyChanged(nameof(ProfilePhoneText));
        OnPropertyChanged(nameof(ProfileAddressText));
    }

    public async Task<Bitmap?> GetParticipantAvatarAsync(Guid user) => Recipients.FirstOrDefault(x=>!x.IsGroup && x.ReceiverId==user)?.Avatar ?? await _imageService.GetProfilePictureAsync(user);

    public async Task<(Bitmap? Avatar, bool Owned)> GetIncomingCallerAvatarAsync(IncomingCall call)
    {
        var embedded = _imageService.GetBitmapFromBase64(call.CallerAvatar);
        if (embedded != null) return (embedded, true);
        var cached = Recipients.FirstOrDefault(x => !x.IsGroup && x.ReceiverId == call.CallerId)?.Avatar;
        return (cached ?? await _imageService.GetProfilePictureAsync(call.CallerId), false);
    }

    private async Task NotifyIncomingMessageAsync(ChatMessage message, RecipientItemViewModel? recipient, string text)
    {
        var senderName = message.SenderName;
        Bitmap? senderAvatar = recipient?.Avatar;

        if (string.IsNullOrWhiteSpace(senderName))
            senderName = recipient?.ReceiverName;

        if (string.IsNullOrWhiteSpace(senderName))
        {
            var profile = await _userService.GetUserProfileAsync(message.Sender);
            senderName = profile?.FullName;
            if (senderAvatar == null && !string.IsNullOrWhiteSpace(profile?.ProfilePicture))
                senderAvatar = _imageService.GetBitmapFromBase64(profile.ProfilePicture);
        }

        senderName = string.IsNullOrWhiteSpace(senderName) ? "New Message" : senderName;
        if (message.ConversationId is { } conversation)
            _notificationService.ShowNotification(senderName, text, "fireuserconnect://conversation/" + Uri.EscapeDataString(conversation));
        else _notificationService.ShowNotification(senderName, text, message.Sender);
    }

    private void OnRecipientsUpdated(List<RecipientDto> updated)
    {
        Dispatcher.UIThread.Post(() => ApplyRecipientUpdates(updated));
    }
    private void ApplyRecipientUpdates(List<RecipientDto> updated)
    {
        foreach (var dto in updated)
        {
            if (dto.ReceiverId == CurrentUser.Id)
                continue;

            var existing = Recipients.FirstOrDefault(r => r.ReceiverId == dto.ReceiverId && r.ConversationId == dto.ConversationId);
            if (existing != null)
            {
                existing.ReceiverName = dto.ReceiverName;
                if (existing.LastSent > dto.LastSent) continue;
                existing.LastMessage = dto.LastMessage;
                existing.AttachmentsCount = dto.AttachmentsCount;
                existing.LastSent = dto.LastSent;
                // If currently chatting with this user, preserve seen state
                if (SelectedRecipient?.ReceiverId == dto.ReceiverId && SelectedRecipient.ConversationId == dto.ConversationId)
                {
                    existing.IsSeen = true;
                }
                else
                {
                    existing.IsSeen = dto.IsSeen;
                }
            }
            else
            {
                var vm = RecipientItemViewModel.FromDto(dto);
                Recipients.Insert(0, vm);
                _ = LoadRecipientAvatarAsync(vm);
                _ = LoadRecipientPresenceAsync(vm);
            }
        }
        ApplyFilter();
    }

    private void OnReadReceipt(Guid reader, Guid sender, DateTime through) => Dispatcher.UIThread.Post(() =>
    {
        if (sender != CurrentUser.Id || reader == CurrentUser.Id) return;
        foreach (var message in Messages.Concat(historyCache.Values.SelectMany(x => x.Items)).Where(x => x.IsMine && x.Message.ConversationId == null && x.Receiver == reader && x.TimeSent <= through))
            message.Seen = true;
    });

    private void OnTypingReceived(bool isTyping, string senderId, string receiverId)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (Guid.TryParse(senderId, out var sId))
            {
                var recipient = Recipients.FirstOrDefault(r => r.ReceiverId == sId);
                if (recipient != null)
                {
                    recipient.IsTyping = isTyping;
                }

                if (SelectedRecipient?.ReceiverId == sId)
                {
                    IsOtherPartyTyping = isTyping;

                    if (isTyping)
                    {
                        _remoteTypingResetTimer?.Dispose();
                        _remoteTypingResetTimer = new Timer(_ =>
                        {
                            Dispatcher.UIThread.Post(() =>
                            {
                                IsOtherPartyTyping = false;
                                if (recipient != null) recipient.IsTyping = false;
                            });
                        }, null, 5000, Timeout.Infinite);
                    }
                }
            }
        });
    }

    private void OnConnectionStateChanged(HubConnectionState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var recovered = state == HubConnectionState.Connected && !IsConnected;
            IsConnected = state == HubConnectionState.Connected;
            ConnectionStatusText = state switch
            {
                HubConnectionState.Connected => "Connected",
                HubConnectionState.Reconnecting => "Reconnecting...",
                _ => "Disconnected"
            };
            if (recovered)
            {
                historyCache.Clear(); loadedHistory.Clear();
                _ = LoadRecipientsAsync();
                if (SelectedRecipient is {} selected) _ = LoadChatHistoryAsync(selected.ReceiverId, 0, recover: true);
            }
        });
    }

    private void OnUserPresenceChanged(UserPresence presence)
    {
        Dispatcher.UIThread.Post(() => ApplyPresence(presence));
    }

    private void OnUserStatusChanged(Guid userId, UserStatus status)
    {
        Dispatcher.UIThread.Post(() => ApplyPresence(new UserPresence
        {
            UserId = userId,
            HasMobileConnection = Recipients.FirstOrDefault(r => r.ReceiverId == userId)?.HasMobileConnection == true,
            Status = status
        }));
    }

    private void ApplyPresence(UserPresence presence)
    {
        presenceVersions[presence.UserId]=presenceVersions.GetValueOrDefault(presence.UserId)+1;
        if (presence.UserId == CurrentUser.Id)
            CurrentUserStatus = presence.Status;

        var recipient = Recipients.FirstOrDefault(item => item.ReceiverId == presence.UserId);
        if (recipient != null && !recipient.IsGroup)
            { recipient.Status = presence.Status; recipient.HasMobileConnection = presence.HasMobileConnection; }
        if(SelectedRecipient is {IsGroup:false} selected && selected.ReceiverId==presence.UserId){ selected.Status=presence.Status; selected.HasMobileConnection=presence.HasMobileConnection; }
    }

    public void RecordActivity()
    {
        _ = _chatService.ActivityAsync();
    }

    [RelayCommand]
    public async Task SetStatusAsync(UserStatus status)
    {
        if (await _chatService.SetStatusAsync(status))
            CurrentUserStatus = status;
    }

    public static string GetStatusLabel(UserStatus status) => status switch
    {
        UserStatus.Online => "Online",
        UserStatus.Away => "Away",
        UserStatus.Busy => "Busy",
        UserStatus.DoNotDisturb => "Do Not Disturb",
        UserStatus.InCall => "In a call",
        UserStatus.InMeeting => "In a meeting",
        UserStatus.Presenting => "Presenting",
        UserStatus.SharingScreen => "Sharing screen",
        _ => "Offline"
    };

    public static IBrush GetStatusColor(UserStatus status) => new SolidColorBrush(status switch
    {
        UserStatus.Online => Color.Parse("#22C55E"),
        UserStatus.Away => Color.Parse("#F59E0B"),
        UserStatus.Busy => Color.Parse("#F97316"),
        UserStatus.DoNotDisturb => Color.Parse("#EF4444"),
        UserStatus.InCall or UserStatus.InMeeting => Color.Parse("#8B5CF6"),
        UserStatus.Presenting or UserStatus.SharingScreen => Color.Parse("#06B6D4"),
        _ => Color.Parse("#94A3B8")
    });

    partial void OnCurrentUserStatusChanged(UserStatus value)
    {
        OnPropertyChanged(nameof(CurrentStatusLabel));
        OnPropertyChanged(nameof(CurrentStatusColor));
    }

    [RelayCommand]
    public async Task LogoutAsync()
    {
        if (BeforeLogoutAsync != null) await BeforeLogoutAsync();
        UnregisterEvents();

        await _chatService.DisconnectAsync();
        await _userService.DisconnectAsync();
        await _authService.LogoutAsync();

        LogoutRequested?.Invoke();
    }
}
