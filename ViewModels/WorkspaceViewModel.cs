using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.AspNetCore.SignalR.Client;
using UserIdentityDesktop.Core;
using UserIdentityDesktop.Core.Models;

namespace UserIdentityDesktop.ViewModels;

public partial class VideoTileViewModel(string identity, Action<RemoteInput>? sendInput = null) : ObservableObject
{
    public string Identity { get; } = identity;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Initials))] private string _name = identity;
    public string? AvatarInitials { get; init; }
    public string Initials => AvatarInitials ?? string.Concat(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(x => x[0])).ToUpperInvariant();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(MicrophoneLabel))] private bool _isSpeaking;
    public bool IsScreen => Identity.Contains(" · screen", StringComparison.Ordinal);
    public bool HasFrame => Frame != null;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasAvatar))] private Avalonia.Media.Imaging.Bitmap? _avatar;
    public bool HasAvatar => Avatar != null;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(MediaLabel)), NotifyPropertyChangedFor(nameof(MicrophoneLabel))] private bool _microphoneOn;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(MediaLabel))] private string _quality = "Checking";
    public string MicrophoneLabel => IsSpeaking ? "Speaking" : MicrophoneOn ? "Microphone on" : "Microphone muted";
    public string MediaLabel => (MicrophoneOn ? "Microphone on" : "Muted") + " · " + Quality;
    public ICommand InputCommand { get; } = new RelayCommand<RemoteInput>(value => { if (value != null) sendInput?.Invoke(value); });
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasFrame))] private VideoPicture? _frame;
    [ObservableProperty] private bool _canControl;
}

public partial class PersonSelection(WorkplacePerson person) : ObservableObject
{
    public Guid Id => person.Id;
    public string Name => person.Name;
    [ObservableProperty] private bool _selected;
}

public partial class WorkspaceViewModel : ViewModelBase, IAsyncDisposable
{
    public ClockDisplaySettings Clock => ClockDisplaySettings.Shared;
    public event Action<WorkspaceFormKind, string>? SubmissionCompleted;
    public ChatViewModel Chat { get; }
    public AttendeePickerViewModel Attendees { get; private set; } = null!;
    public AttendeePickerViewModel CallAttendees { get; private set; } = null!;
    public Func<string, string, Task<bool>>? ConfirmActionAsync { get; set; }
    private Task<bool> Confirm(string title, string details) => ConfirmActionAsync?.Invoke(title, details) ?? Task.FromResult(false);
    public bool IsQuietIncoming => Chat.CurrentUserStatus is UserStatus.Busy or UserStatus.DoNotDisturb or UserStatus.InCall or UserStatus.InMeeting or UserStatus.Presenting;
    public bool HandleCallMessage(ChatMessage message)
    {
        if (!InCall || message.Sender == auth.CurrentUser?.Id) return false;
        var matches = activeCallConversation != null ? message.ConversationId == activeCallConversation : message.ConversationId == null && activeCallUsers.Contains(message.Sender);
        if (!matches) return false;
        if (activeCallConversation != null) Chat.SelectedRecipient = Chat.Recipients.FirstOrDefault(x => x.ConversationId == activeCallConversation) ?? Chat.SelectedRecipient;
        else Chat.StartChatWithUser(message.Sender, message.SenderName ?? "Call participant", null);
        ShowCallChat = true; return true;
    }
    private string? activeCallConversation;
    private readonly System.Collections.Generic.HashSet<Guid> activeCallUsers = new();
    private UserStatus? statusBeforeCall;
    private readonly SemaphoreSlim presenceGate = new(1,1);
    private async Task UpdateCallPresence()
    {
        await presenceGate.WaitAsync();
        try
        {
            if (InCall) await Chat.SetStatusAsync(ScreenEnabled ? UserStatus.Presenting : meetingLocator != null ? UserStatus.InMeeting : UserStatus.InCall);
            else if (statusBeforeCall is {} previous) { statusBeforeCall = null; await Chat.SetStatusAsync(previous); }
        }
        catch (Exception ex) { if (!disposed) Feedback = "Presence could not be updated: " + ex.Message; }
        finally { presenceGate.Release(); }
    }
    partial void OnScreenEnabledChanged(bool value) { if (InCall) _ = UpdateCallPresence(); }

    private readonly ICollaborationApi api;
    private readonly INativeMediaSession media;
    private readonly IAuthService auth;
    private readonly IChatService chatService;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim mediaGate = new(1, 1);
    private readonly ConcurrentDictionary<string, VideoPicture> pendingFrames = new();
    private int frameScheduled;
    private bool disposed, refreshing, refreshAgain;
    private TaskCompletionSource? refreshCompletion;
    private CancellationTokenSource? shareSelection;
    private long screenCaptureVersion;
    public Func<System.Collections.Generic.IReadOnlyList<ScreenShareSource>, Task<ScreenShareSource?>>? ChooseScreenShareSourceAsync { get; set; }
    private string? callId, meetingLocator;
    private readonly UserIdentityDesktop.Service.Media.CallSounds callSounds = new();
    private SavedCalendar? persistedCalendar;
    private MeetingCredentials? configuredMeeting;
    partial void OnShowCalendarEditorChanged(bool value)
    {
        if (!value) { persistedCalendar = null; configuredMeeting = null; reviewedDraft = null; Reviewed = false; }
    }
    private CalendarWrite? reviewedDraft;
    private string? reviewToken;
    private bool hasConflicts;
    private RemoteControlStatus remoteStatus = new(Array.Empty<NativePeer>(), null, null, null);
    private CalendarItem? responseItem;
    private string? responseToken;

    public ObservableCollection<CalendarItem> Calendar { get; } = new();
    public ObservableCollection<Announcement> Announcements { get; } = new();
    public ObservableCollection<VideoTileViewModel> Videos { get; } = new();
    [ObservableProperty] private int _galleryColumns = 2;
    public string MicrophoneActionLabel => MicrophoneEnabled ? "Mute" : "Unmute";
    public string CameraActionLabel => CameraEnabled ? "Camera off" : "Camera on";
    public string ScreenActionLabel => ScreenEnabled ? "Stop sharing" : "Share screen";
    public bool HasFocusedVideo => FocusedVideo != null;
    public VideoTileViewModel[] GalleryVideos => Videos.Where(x=>x != FocusedVideo).ToArray();
    partial void OnFocusedVideoChanged(VideoTileViewModel? value) { OnPropertyChanged(nameof(GalleryVideos)); OnPropertyChanged(nameof(CanRequestFocusedControl)); }
    public bool CanRequestFocusedControl => FocusedVideo is {IsScreen:true} tile && RemotePeers.Any(x=>x.Identity==tile.Identity.Split(" · ")[0] && x.CanHost && x.Sharing);
    public bool HasFocusedScreen => FocusedVideo?.IsScreen == true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasFocusedVideo)), NotifyPropertyChangedFor(nameof(HasFocusedScreen))] private VideoTileViewModel? _focusedVideo;
    public bool IsWaylandSession=>OperatingSystem.IsLinux()&&(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))||string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),"wayland",StringComparison.OrdinalIgnoreCase));
    [ObservableProperty] private bool _showCallMembers;
    [ObservableProperty] private bool _showCallChat;
    [ObservableProperty] private PersonSelection? _selectedCallInvitee;
    public bool IsMeetingCall => meetingLocator != null;
    public bool NoUpcomingMeetings => UpcomingMeetings.Length==0;
    public bool CanInviteToCall => InCall && (callId != null || meetingLocator != null);
    [RelayCommand] private void ToggleCallChat() => ShowCallChat = !ShowCallChat;
    [ObservableProperty] private string _callInviteEmail="";
    [RelayCommand] private Task InviteCallEmailAsync() => Work(async()=>
    {
        var address=CallInviteEmail.Trim();
        if(meetingLocator==null||!InCall)throw new InvalidOperationException("Join a meeting before inviting an external attendee.");
        if(address.Length>254||!System.Text.RegularExpressions.Regex.IsMatch(address,@"^[^\s@<>]+@[^\s@<>]+\.[^\s@<>]+$")||!System.Net.Mail.MailAddress.TryCreate(address,out var parsed)||parsed.Address!=address)throw new InvalidOperationException("Enter a valid email address.");
        await api.InviteMeetingEmailsAsync(meetingLocator,new[]{address},lifetime.Token);
        if(CallInviteEmail.Trim()==address)CallInviteEmail="";
        Feedback="Invitation sent.";
    });
    [RelayCommand] private Task InviteCallAsync() => Work(async () =>
    {
        if (meetingLocator != null)
        {
            if (CallAttendees.UserIds.Length == 0 && CallAttendees.Emails.Count == 0) throw new InvalidOperationException("Choose employees, a business unit or guest emails first.");
            if (CallAttendees.UserIds.Length > 0 || CallAttendees.UnitIds.Length > 0) await api.InviteMeetingAudienceAsync(meetingLocator, CallAttendees.UserIds, CallAttendees.UnitIds, lifetime.Token);
            if (CallAttendees.Emails.Count > 0) await api.InviteMeetingEmailsAsync(meetingLocator, CallAttendees.Emails.ToArray(), lifetime.Token);
        }
        else if (callId != null)
        {
            var users = CallAttendees.UserIds.Length > 0 ? CallAttendees.UserIds : SelectedCallInvitee != null ? new[] { SelectedCallInvitee.Id } : Array.Empty<Guid>();
            if (users.Length == 0) throw new InvalidOperationException("Choose a colleague to invite.");
            foreach (var id in users) await api.InviteCallAsync(callId, id, lifetime.Token);
        }
        else throw new InvalidOperationException("Join a call before inviting people.");
        CallAttendees.ClearCommand.Execute(null); CallAttendees.Emails.Clear(); Feedback = "Invitations sent.";
    });
    [ObservableProperty] private string _callElapsed = "00:00";
    private DateTimeOffset callStarted;
    private readonly DispatcherTimer callClock = new() { Interval = TimeSpan.FromSeconds(1) };
    public string ParticipantSummary => $"{Videos.Count(x => !x.IsScreen)} participants · {CallElapsed}";
    partial void OnCallElapsedChanged(string value) => OnPropertyChanged(nameof(ParticipantSummary));
    [RelayCommand] private void FocusVideo(VideoTileViewModel? tile) => FocusedVideo = FocusedVideo == tile ? null : tile;
    [RelayCommand] private void ClearVideoFocus() => FocusedVideo = null;
    [RelayCommand] private void ToggleCallMembers() => ShowCallMembers = !ShowCallMembers;
    public ObservableCollection<CalendarConflict> Conflicts { get; } = new();
    public ObservableCollection<CalendarConflict> ResponseConflicts { get; } = new();
    public ObservableCollection<PersonSelection> People { get; } = new();
    public ObservableCollection<PersonSelection> Audience { get; } = new();
    public ObservableCollection<NativePeer> RemotePeers { get; } = new();
    [ObservableProperty] private NativePeer? _selectedRemotePeer;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(RemoteDesktopAvailable)), NotifyPropertyChangedFor(nameof(ControlRequesterName))] private string? _controlRequester;
    [ObservableProperty] private string _remoteControlLabel = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(RemoteDesktopAvailable))] private bool _hasRemoteControl;
    [ObservableProperty] private bool _reduceMotion = Environment.GetEnvironmentVariable("FIREUSER_REDUCED_MOTION") == "1";
    public bool CanRequestControl => InCall && SelectedRemotePeer is { CanHost: true, Sharing: true };
    partial void OnSelectedRemotePeerChanged(NativePeer? value) => OnPropertyChanged(nameof(CanRequestControl));
    public bool CanStartCall => CallingEnabled && !InCall;
    partial void OnCallingEnabledChanged(bool value) => OnPropertyChanged(nameof(CanStartCall));
    partial void OnInCallChanged(bool value) { OnPropertyChanged(nameof(CanInviteToCall)); OnPropertyChanged(nameof(CanRequestControl)); OnPropertyChanged(nameof(CanStartCall)); }
    [ObservableProperty] private CalendarItem? _selectedCalendar;
    [ObservableProperty] private Announcement? _selectedAnnouncement;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasWorkspaceStatus))] private string _feedback = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasWorkspaceStatus))] private bool _isLoading;
    public bool HasWorkspaceStatus => IsLoading || !string.IsNullOrWhiteSpace(Feedback);
    [ObservableProperty] private bool _callingEnabled;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CallWindowVisible))] private bool _inCall;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CallWindowVisible))] private bool _connectingCall;
    public bool CallWindowVisible => InCall || ConnectingCall;
    [ObservableProperty] private bool _mediaBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(MicrophoneActionLabel))] private bool _microphoneEnabled;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CameraActionLabel))] private bool _cameraEnabled;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(RemoteDesktopAvailable)), NotifyPropertyChangedFor(nameof(ScreenActionLabel))] private bool _screenEnabled;
    public bool RemoteDesktopAvailable => ScreenEnabled || RemotePeers.Any(x => x.CanHost && x.Sharing) || HasRemoteControl || ControlRequester != null;
    [ObservableProperty] private string _callTitle = "";
    [ObservableProperty] private IncomingCall? _incoming;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasIncomingAvatar))] private Avalonia.Media.Imaging.Bitmap? _incomingAvatar;
    private Avalonia.Media.Imaging.Bitmap? ownedIncomingAvatar;
    public bool HasIncomingAvatar => IncomingAvatar != null;
    [ObservableProperty] private bool _joinMeetingMuted = true;
    public bool IsIncomingMeeting => Incoming?.MeetingLocator != null;
    public string IncomingCallerName => Incoming?.CallerName ?? "";
    public string IncomingCallerInitials => string.Concat(IncomingCallerName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(x => x[0])).ToUpperInvariant();
    private async Task LoadIncomingAvatar(IncomingCall call)
    {
        try
        {
            var result = await Chat.GetIncomingCallerAvatarAsync(call);
            if (disposed || !ReferenceEquals(Incoming,call)) { if (result.Owned) result.Avatar?.Dispose(); return; }
            IncomingAvatar = result.Avatar;
            if (result.Owned) ownedIncomingAvatar = result.Avatar;
        }
        catch { /* Keep the caller's initials if their compressed photo is unavailable. */ }
    }
    [ObservableProperty] private string _meetingCode = "";
    [ObservableProperty] private int _selectedWorkspaceTab;
    [ObservableProperty] private bool _showCalendarEditor;
    [ObservableProperty] private bool _showAnnouncementEditor;
    [ObservableProperty] private string _microphoneDevice = "";
    [ObservableProperty] private string _cameraDevice = "";
    [ObservableProperty] private string _screenDevice = "";
    [ObservableProperty] private string _chatPin = "";
    [ObservableProperty] private string _eventTitle = "";
    [ObservableProperty] private string _eventDescription = "";
    [ObservableProperty] private DateTimeOffset? _eventDate = DateTimeOffset.Now.Date.AddDays(1);
    [ObservableProperty] private TimeSpan? _eventTime = new TimeSpan(9, 0, 0);
    [ObservableProperty] private string _durationMinutes = "30";
    [ObservableProperty] private string _announcementTitle = "";
    [ObservableProperty] private string _announcementBody = "";
    public string[] AnnouncementCategories { get; } = ["General", "Event", "Important action"];
    [ObservableProperty] private string _announcementCategory = "General";
    [ObservableProperty] private DateTimeOffset? _publishDate;
    [ObservableProperty] private TimeSpan? _publishTime = new TimeSpan(9, 0, 0);
    [ObservableProperty] private DateTimeOffset? _expiryDate = DateTimeOffset.Now.Date.AddDays(7);
    [ObservableProperty] private TimeSpan? _expiryTime = new TimeSpan(23, 59, 0);
    [ObservableProperty] private bool _sendAnnouncementEmail = true;
    [ObservableProperty] private bool _announcementImportant;
    [ObservableProperty] private bool _requiresAcknowledgement;
    [ObservableProperty] private bool _acknowledgeResponseConflicts;
    [ObservableProperty] private bool _responseReviewed;
    [ObservableProperty] private bool _isMeeting = true;
    [ObservableProperty] private bool _acknowledgeConflicts;
    [ObservableProperty] private bool _reviewed;
    [ObservableProperty] private DateTimeOffset? _rangeDate = DateTimeOffset.Now.Date;
    public string IncomingLabel => Incoming is null ? "" : $"{Incoming.CallerName} · {Incoming.MeetingTitle ?? Incoming.CallType + " call"}";
    public string DateRange => RangeDate is { } date ? date.ToString("dddd, d MMMM yyyy") : "Choose a date";
    public CalendarItem[] DaySchedule => Calendar.Where(x => !x.Cancelled && x.StartUtc.ToLocalTime() < (RangeDate?.Date ?? DateTime.Today).AddDays(1) && x.EndUtc.ToLocalTime() > (RangeDate?.Date ?? DateTime.Today)).OrderBy(x => x.StartUtc).Select(x=>x with{}).ToArray();
    public CalendarItem[] UpcomingMeetings => Calendar.Where(x => x.Kind == "Meeting" && !x.Cancelled).OrderBy(x => x.StartUtc).Select(x=>x with{}).ToArray();
    public bool HasDaySchedule => DaySchedule.Length != 0;
    public bool HasAnnouncements => Announcements.Count != 0;
    public string DeviceHelp => OperatingSystem.IsMacOS()
        ? "Allow microphone, camera and Screen Recording access in System Settings. Remote control also requires Accessibility permission."
        : OperatingSystem.IsLinux() ? "Audio uses the system audio service; cameras use V4L2. Wayland screen sharing uses your compositor's ScreenCast portal and PipeWire; X11 sessions capture the selected screen or window. Remote control requires an X11 session and a supported desktop source."
        : "Choose your devices below. Allow camera and microphone access in Windows privacy settings.";
    public bool ShowConnectionBanner => auth.Actions?.IsConnected==false || !Chat.IsConnected || (auth.Actions?.PendingCount??0)>0;
    public string ConnectionBannerText => (auth.Actions?.IsConnected==false||!Chat.IsConnected?"Reconnecting to Fire User. You can keep working.":"Sending queued actions…")+((auth.Actions?.PendingCount??0)>0?$" {auth.Actions!.PendingCount} action(s) queued.":"");
    private void ConnectionQueueChanged()=>Dispatcher.UIThread.Post(()=>{if(disposed)return;if(auth.Actions?.IsConnected==false)IsLoading=false;OnPropertyChanged(nameof(ShowConnectionBanner));OnPropertyChanged(nameof(ConnectionBannerText));});
    private void ChatConnectionChanged(object? sender,System.ComponentModel.PropertyChangedEventArgs args){if(args.PropertyName==nameof(Chat.IsConnected))ConnectionQueueChanged();}
    public AppearanceSettingsViewModel Appearance { get; }
    private void AppearanceInvalidated() => Dispatcher.UIThread.Post(()=>{if(!disposed)_=Appearance.RefreshAsync();});
    public WorkspaceViewModel(ChatViewModel chat, ICollaborationApi api, INativeMediaSession media, IAuthService auth, IChatService chatService, INotificationService? notifications = null)
    {
        Appearance=new(api);
        Clock.PropertyChanged += OnClockFormatChanged;
        chat.Workspace = this;
        Chat = chat;
        this.api = api; this.media = media; this.auth = auth; this.chatService = chatService;
        if(auth.Actions!=null)auth.Actions.Changed+=ConnectionQueueChanged;
        chat.PropertyChanged+=ChatConnectionChanged;
        Attendees = new AttendeePickerViewModel(api, People);
        CallAttendees = new AttendeePickerViewModel(api, new ObservableCollection<PersonSelection>());
        InitializeNotifications(notifications);
        api.MeetingChanged += OnMeetingChanged;
        chatService.WorkplaceChanged += OnInvalidated;
        chatService.AppearancePreferencesChanged += AppearanceInvalidated;
        chatService.ConnectionStateChanged += OnConnection;
        chatService.IncomingCallReceived += OnIncoming;
        chatService.CallParticipantLeft += OnCallLeft;
        media.VideoReceived += OnVideo;
        media.ParticipantJoined += OnParticipantJoined;
        media.ParticipantUpdated += OnParticipantUpdated;
        Videos.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(ParticipantSummary)); OnPropertyChanged(nameof(GalleryVideos)); };
        callClock.Tick += (_, _) => CallElapsed = (DateTimeOffset.UtcNow - callStarted).ToString(@"mm\:ss");
        media.ConnectionChanged += OnMediaConnectionChanged;
        media.Error += OnMediaError;
        media.ParticipantLeft += OnParticipantLeft;
        media.CaptureStopped += OnCaptureStopped;
        media.RemoteControlChanged += OnRemoteControl;
        media.ClipboardTransferChanged += OnClipboardTransfer;
    }
    partial void OnIncomingChanged(IncomingCall? value)
    {
        var oldAvatar = ownedIncomingAvatar; ownedIncomingAvatar = null; IncomingAvatar = null; oldAvatar?.Dispose();
        OnPropertyChanged(nameof(IncomingCallerName)); OnPropertyChanged(nameof(IncomingCallerInitials)); OnPropertyChanged(nameof(IsIncomingMeeting));
        if (value != null) _ = LoadIncomingAvatar(value);
        OnPropertyChanged(nameof(IncomingLabel));
        callSounds.Play(value == null || IsQuietIncoming ? null : "Ringtone");
    }
    partial void OnRangeDateChanged(DateTimeOffset? oldValue, DateTimeOffset? newValue)
    {
        OnPropertyChanged(nameof(DateRange)); OnPropertyChanged(nameof(DaySchedule)); OnPropertyChanged(nameof(HasDaySchedule));
        EventDate = newValue;
        if (oldValue?.Year != newValue?.Year || oldValue?.Month != newValue?.Month) OnInvalidated();
    }
    private void OnInvalidated() => Dispatcher.UIThread.Post(() => { if (!disposed) _ = RefreshAsync(); });
    private void OnConnection(HubConnectionState state) { if (state == HubConnectionState.Connected) {OnInvalidated();AppearanceInvalidated();} }
    private void OnIncoming(IncomingCall call) => Dispatcher.UIThread.Post(() =>
    {
        if (disposed) return;
        if (Incoming?.CallId == call.CallId && Incoming.MeetingInvitationId == call.MeetingInvitationId) return;
        Incoming = call;
    });
    private void OnCallLeft(CallParticipantLeft change) => Dispatcher.UIThread.Post(() =>
    {
        if (Incoming?.CallId == change.CallId && change.CallEnded) Incoming = null;
        if (callId == change.CallId && change.CallEnded) _ = LeaveAsync();
    });
    private void OnMediaError(string message) => Dispatcher.UIThread.Post(() => { if (!disposed) Feedback = message; });
    private void OnParticipantUpdated(MediaParticipantUpdate update) => Dispatcher.UIThread.Post(() =>
    {
        if (disposed) return;
        var identity = update.Identity == auth.CurrentUser?.Id.ToString() ? "You" : update.Identity;
        var tile = Videos.FirstOrDefault(x => x.Identity == identity);
        if (tile == null && update.Kind=="speaking" && update.Value!="1") return;
        if (tile == null && Videos.Count < 64) { tile = new VideoTileViewModel(identity); Videos.Add(tile); }
        if (tile == null) return;
        if (update.Kind == "microphone") { tile.MicrophoneOn = update.Value == "1"; if (!tile.MicrophoneOn) tile.IsSpeaking = false; }
        if (update.Kind == "speaking") tile.IsSpeaking = update.Value == "1";
        if (update.Kind == "camera" && update.Value == "0") tile.Frame = null;
        if (update.Kind == "quality") tile.Quality = update.Value switch { "0" => "Poor", "1" => "Good", "2" => "Excellent", "3" => "Disconnected", _ => "Checking" };
    });
    private void OnParticipantJoined(MediaParticipant participant) => Dispatcher.UIThread.Post(() =>
    {
        if (disposed) return;
        if (Incoming == null) callSounds.Play(null);
        var tile = Videos.FirstOrDefault(x => x.Identity == participant.Identity);
        if (tile == null && Videos.Count < 64) { tile = new VideoTileViewModel(participant.Identity, ForwardRemoteInput); Videos.Add(tile); }
        if (tile != null) { tile.Name = participant.Name; if (Guid.TryParse(participant.Identity,out var user)) _ = LoadParticipantAvatar(tile,user); }
        foreach (var share in Videos.Where(x=>x.IsScreen && x.Identity.StartsWith(participant.Identity + " · screen",StringComparison.Ordinal))) share.Name = ParticipantName(participant.Identity) + "'s screen";
        ApplyRemoteControl(remoteStatus);
    });
    private async Task LoadParticipantAvatar(VideoTileViewModel tile, Guid user)
    {
        try { var avatar = await Chat.GetParticipantAvatarAsync(user); if (!disposed && Videos.Contains(tile)) tile.Avatar = avatar; }
        catch { /* Initials remain available when no compressed picture can be loaded. */ }
    }
    private void OnParticipantLeft(string identity) => Dispatcher.UIThread.Post(() =>
    {
        if (identity.Contains(" · camera · ", StringComparison.Ordinal))
        {
            var participant = Videos.FirstOrDefault(x => x.Identity == identity.Split(" · ")[0]);
            if (participant != null) participant.Frame = null;
            return;
        }
        foreach (var tile in Videos.Where(x => x.Identity == identity || x.Identity.StartsWith(identity + " · ", StringComparison.Ordinal)).ToList())
        { if (FocusedVideo == tile) FocusedVideo = null; Videos.Remove(tile); }
    });
    private void OnCaptureStopped(string kind)
    {
        if (kind == "screen") Interlocked.Increment(ref screenCaptureVersion);
        Dispatcher.UIThread.Post(() =>
        {
            if (disposed) return;
            if (kind == "microphone") MicrophoneEnabled = false;
            if (kind == "camera") CameraEnabled = false;
            if (kind == "screen") ScreenEnabled = false;
            RemoveLocalVideo(kind);
        });
    }
    private string ParticipantName(string identity)
    {
        var name = Videos.FirstOrDefault(x => x.Identity == identity)?.Name;
        if (!string.IsNullOrWhiteSpace(name) && name != identity && !Guid.TryParse(name,out _)) return name;
        if (Guid.TryParse(identity,out var id))
        {
            name = People.FirstOrDefault(x => x.Id == id)?.Name;
            if (!string.IsNullOrWhiteSpace(name)) return name;
            if (Chat.SelectedRecipient?.ReceiverId == id && !string.IsNullOrWhiteSpace(Chat.SelectedRecipient.ReceiverName)) return Chat.SelectedRecipient.ReceiverName;
        }
        return "Participant";
    }
    public string ControlRequesterName => ControlRequester is {} id ? ParticipantName(id) : "";
    public DateTimeOffset? ControlRequestExpiresAt => remoteStatus.RequestExpiresAt;
    public string RemoteInputLatency => remoteStatus.InputRoundTripMs is {} ms ? $"Input round trip: {ms:0} ms · target ≤50 ms" : "Input target: ≤50 ms on a healthy low-latency connection";
    public VideoTileViewModel? ControlledScreen => Videos.FirstOrDefault(x => x.IsScreen && x.CanControl);
    public bool CanRequestScreenControl(VideoTileViewModel? tile) => tile is {IsScreen:true,CanControl:false} && RemotePeers.Any(x => x.Identity == tile.Identity.Split(" · ")[0] && x.CanHost && x.Sharing);
    [RelayCommand(CanExecute=nameof(CanRequestScreenControl))]
    private Task RequestScreenControlAsync(VideoTileViewModel? tile)
    {
        if (!CanRequestScreenControl(tile)) return Task.CompletedTask;
        FocusedVideo = tile;
        SelectedRemotePeer = RemotePeers.First(x => x.Identity == tile!.Identity.Split(" · ")[0]);
        return RequestControlCommand.ExecuteAsync(null);
    }
    private void OnRemoteControl(RemoteControlStatus status) => Dispatcher.UIThread.Post(() => ApplyRemoteControl(status));
    private void OnClipboardTransfer(string message) => Dispatcher.UIThread.Post(() => { if(!disposed) Feedback=message; });
    private bool remoteInputPaused;
    private void ForwardRemoteInput(RemoteInput input) { if(!remoteInputPaused)media.SendRemoteInput(input); }
    internal void ForwardRemoteKeyboard(RemoteInput input)=>ForwardRemoteInput(input);
    internal void PauseRemoteInput()
    {
        remoteInputPaused=true;
        foreach(var tile in Videos)tile.CanControl=false;
        EmergencyRemoteStop();
    }
    internal void EmergencyRemoteStop()=>_=Task.Run(async()=>{try{await media.RevokeControlAsync();}catch{}});
    private void ApplyRemoteControl(RemoteControlStatus status)
    {
        if (disposed) return;
        var previousController = remoteStatus.Controlling;
        var previousScreen = ControlledScreen;
        remoteStatus = status;
        if(status.Controlling == null)remoteInputPaused=false;
        var selected = SelectedRemotePeer?.Identity;
        RemotePeers.Clear(); foreach (var peer in status.Peers.Where(x => x.CanHost && x.Sharing)) RemotePeers.Add(peer with {DisplayName=ParticipantName(peer.Identity)});
        SelectedRemotePeer = RemotePeers.FirstOrDefault(x => x.Identity == selected);
        ControlRequester = status.RequestedBy;
        OnPropertyChanged(nameof(RemoteDesktopAvailable));
        HasRemoteControl = status.Controller != null || status.Controlling != null;
        OnPropertyChanged(nameof(RemoteInputLatency));
        RemoteControlLabel = status.Controller != null ? ParticipantName(status.Controller) + " is controlling your desktop · Ctrl+Alt+Escape revokes access" : status.Controlling != null ? "Controlling " + ParticipantName(status.Controlling) + " · Ctrl+Alt+Escape stops control" : status.HostUnavailableReason ?? "Remote control requires a supported native desktop share and the sharer’s approval. Browser participants can request control.";
        foreach (var tile in Videos) tile.CanControl = !remoteInputPaused && status.Controlling != null && tile.Identity.StartsWith(status.Controlling + " · screen · ", StringComparison.Ordinal);
        OnPropertyChanged(nameof(CanRequestControl)); OnPropertyChanged(nameof(CanRequestFocusedControl)); OnPropertyChanged(nameof(ControlRequesterName));
        RequestScreenControlCommand.NotifyCanExecuteChanged();
        if (previousController != status.Controlling || previousScreen != ControlledScreen)
        {
            if (ControlledScreen is {} controlled) FocusedVideo = controlled;
            OnPropertyChanged(nameof(ControlledScreen));
        }
    }
    [RelayCommand] private Task RequestControlAsync() => RunMedia(async () =>
    {
        if (SelectedRemotePeer == null) throw new InvalidOperationException("Select a native participant sharing their screen.");
        await media.RequestControlAsync(SelectedRemotePeer.Identity);
    });
    [RelayCommand] private Task AllowControlAsync() => RunMedia(async () => { if (ControlRequester != null) await media.AnswerControlAsync(ControlRequester, true); });
    [RelayCommand] private Task DenyControlAsync() => RunMedia(async () => { if (ControlRequester != null) await media.AnswerControlAsync(ControlRequester, false); });
    [RelayCommand] private Task StopControlAsync() => RunMedia(media.RevokeControlAsync);
    private void OnVideo(VideoPicture picture)
    {
        if (disposed || !InCall) return;
        // One pending frame per tile and one UI dispatch, regardless of the producer rate.
        if (pendingFrames.Count >= 16 && !pendingFrames.ContainsKey(picture.Identity)) return;
        pendingFrames[picture.Identity] = picture;
        if (Interlocked.Exchange(ref frameScheduled, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref frameScheduled, 0);
            if (disposed || !InCall) { pendingFrames.Clear(); return; }
            foreach (var key in pendingFrames.Keys)
            {
                if (!pendingFrames.TryRemove(key, out var frame)) continue;
                var tileKey = key.Contains(" · camera", StringComparison.Ordinal) ? key.Split(" · ")[0] : key;
                var tile = Videos.FirstOrDefault(x => x.Identity == tileKey);
                if (tile is null && Videos.Count < 16) { tile = new VideoTileViewModel(tileKey, ForwardRemoteInput) { Name = tileKey.Contains(" · screen", StringComparison.Ordinal) ? (tileKey.StartsWith("You · ", StringComparison.Ordinal) ? "Your screen" : ParticipantName(tileKey.Split(" · ")[0]) + "'s screen") : tileKey, CanControl = !remoteInputPaused && remoteStatus.Controlling != null && key.StartsWith(remoteStatus.Controlling + " · screen · ", StringComparison.Ordinal) }; Videos.Add(tile); }
                if (tile != null)
                {
                    var firstFrame = tile.Frame == null;
                    tile.Frame = frame;
                    if (firstFrame && tile.IsScreen) { FocusedVideo = tile; if (tile.CanControl) OnPropertyChanged(nameof(ControlledScreen)); }
                }
            }
        }, OperatingSystem.IsLinux() ? DispatcherPriority.Background : DispatcherPriority.Render);
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (disposed) return;
        if (refreshing) { refreshAgain = true; if (refreshCompletion != null) await refreshCompletion.Task; return; }
        refreshing = true; refreshCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            do
            {
                refreshAgain = false;
                var date = RangeDate?.Date ?? DateTime.Today;
                var first = new DateTime(date.Year, date.Month, 1);
                date = first.AddDays(-(int)first.DayOfWeek);
                async Task Load<T>(Task<T> pending, Action<T> apply)
                {
                    try { var result = await pending; if (!disposed) apply(result); }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                    catch (Exception ex) { if (!disposed) Feedback = ex.Message; }
                }
                await Task.WhenAll(
                    Appearance.RefreshAsync(),
                    Load(LoadCalendar(date, date.AddDays(42)), items =>
                    {
                        Calendar.Clear(); foreach (var entry in items) Calendar.Add(entry);
                        OnPropertyChanged(nameof(DaySchedule)); OnPropertyChanged(nameof(HasDaySchedule)); OnPropertyChanged(nameof(UpcomingMeetings));OnPropertyChanged(nameof(NoUpcomingMeetings));
                    }),
                    Load(api.AnnouncementsAsync(lifetime.Token), items => { Announcements.Clear(); foreach (var item in items) Announcements.Add(item); OnPropertyChanged(nameof(HasAnnouncements)); }),
                    Load(LoadNotificationSnapshot(), _ => { }),
                    Load(api.CallingEnabledAsync(lifetime.Token), enabled => CallingEnabled = enabled),
                    Load(api.CatalogAsync(lifetime.Token), catalog => { UpdatePeople(People, catalog.People); UpdatePeople(CallAttendees.People, catalog.People); UpdatePeople(Audience, catalog.People); ApplyCalendarPeople(catalog.People); ApplySharedCalendars(catalog.SharedCalendars); })
                );
                if (IsVacationTab) { vacationPage = 1; await LoadVacations(); }
            } while (refreshAgain && !disposed);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Feedback = ex.Message; }
        finally { refreshing = false; refreshCompletion?.TrySetResult(); }
    }

    private static void UpdatePeople(ObservableCollection<PersonSelection> target, System.Collections.Generic.List<WorkplacePerson> people)
    {
        var selected = target.Where(x => x.Selected).Select(x => x.Id).ToHashSet();
        var retained = target.Where(x => x.Selected && !people.Any(p => p.Id == x.Id)).Select(x => new WorkplacePerson(x.Id,x.Name)).ToArray();
        target.Clear(); foreach (var person in people.Concat(retained)) target.Add(new PersonSelection(person) { Selected = selected.Contains(person.Id) });
    }

    private MediaDevices Devices => new(MicrophoneDevice, CameraDevice, ScreenDevice);
    private async Task RunMedia(Func<Task> action, bool wait = false)
    {
        if (wait) await mediaGate.WaitAsync();
        else if (!await mediaGate.WaitAsync(0)) return;
        try { MediaBusy = true; Feedback = ""; await action(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { ConnectingCall = false; Feedback = ex.Message; }
        finally { MediaBusy = false; mediaGate.Release(); }
    }
    private async Task JoinMedia(MediaToken token, string title, bool enableMicrophone = true)
    {
        Videos.Clear(); FocusedVideo = null;
        var local = new VideoTileViewModel("You") { Name = "You", AvatarInitials = auth.CurrentUser?.Initials }; Videos.Add(local);
        if (auth.CurrentUser is {} current) _ = LoadParticipantAvatar(local,current.Id);
        CallTitle = title; ConnectingCall = true;
        try { await media.ConnectAsync(token, lifetime.Token); }
        catch { ConnectingCall = false; throw; }
        statusBeforeCall ??= Chat.CurrentUserStatus;
        CallTitle = title; callStarted = DateTimeOffset.UtcNow; CallElapsed = "00:00"; callClock.Start(); InCall = true;
        ConnectingCall = false; await UpdateCallPresence();
        // Answer/Join is the explicit capture action; meetings honor the prejoin choice.
        MicrophoneEnabled = CameraEnabled = ScreenEnabled = false;
        if (enableMicrophone)
        {
            try { await media.SetMicrophoneAsync(true, Devices, lifetime.Token); MicrophoneEnabled = true; }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
            catch (Exception ex) { Feedback = "Joined with microphone muted. Could not enable the microphone: " + ex.Message; }
        }
    }
    public Task JoinExistingCallAsync(string id) => RunMedia(async () =>
    {
        if (InCall) throw new InvalidOperationException("Leave the current call first.");
        var context = await api.CallContextAsync(id, lifetime.Token);
        callId = context.CallId; activeCallConversation = context.ConversationId; activeCallUsers.UnionWith(context.ParticipantIds);
        CallTitle = "Fire User call"; ConnectingCall = true;
        try { await JoinMedia(await api.CallTokenAsync(callId, lifetime.Token), "Fire User call"); }
        catch { await api.LeaveCallAsync(callId); callId = null; throw; }
    });
    [RelayCommand] private Task StartVoiceAsync() => StartCall(false);
    [RelayCommand] private Task StartVideoAsync() => StartCall(true);
    private Task StartCall(bool video) => RunMedia(async () =>
    {
        if (InCall) throw new InvalidOperationException("Leave the current call first.");
        if (!CallingEnabled) throw new InvalidOperationException("Calling is not configured on the server.");
        var peer = Chat.SelectedRecipient ?? throw new InvalidOperationException("Select a conversation first.");
        var call = peer.IsGroup ? await api.CreateGroupCallAsync(peer.ConversationId!, video, lifetime.Token) : await api.CreateCallAsync(peer.ReceiverId, video, lifetime.Token);
        callId = call.CallId; activeCallConversation = call.ConversationId; activeCallUsers.UnionWith(call.ParticipantIds); activeCallUsers.Add(peer.ReceiverId); callSounds.Play("Ringback");
        CallTitle = peer.ReceiverName; ConnectingCall = true;
        try { await JoinMedia(await api.CallTokenAsync(callId, lifetime.Token), peer.ReceiverName); }
        catch { callSounds.Play(null); await api.LeaveCallAsync(callId); callId = null; throw; }
    });
    [RelayCommand] private Task AcceptAsync() => RunMedia(async () =>
    {
        if (InCall) throw new InvalidOperationException("Leave the current call first.");
        var incoming = Incoming ?? throw new InvalidOperationException("This invitation is no longer available.");
        callSounds.Play(null);
        if (incoming.MeetingLocator != null)
        {
            MeetingCode = incoming.MeetingLocator;
            await JoinMeetingCore();
        }
        else
        {
            callId = incoming.CallId; activeCallUsers.Add(incoming.CallerId);
            CallTitle = incoming.CallerName; ConnectingCall = true;
            try { await JoinMedia(await api.CallTokenAsync(callId, lifetime.Token), incoming.CallerName); }
            catch { callSounds.Play(null); await api.LeaveCallAsync(callId); callId = null; throw; }
        }
        Incoming = null;
    });
    [RelayCommand] private Task DeclineAsync() => RunMedia(async () =>
    {
        if (Incoming is not { } incoming) return;
        if (incoming.MeetingLocator is {} locator && incoming.MeetingInvitationId is {} invitationId) await chatService.RespondToMeetingInvitationAsync(locator,invitationId,false);
        else await api.DeclineCallAsync(incoming.CallId, lifetime.Token);
        Incoming = null;
    });
    public void OpenMeeting(string locator)
    {
        if (!MeetingLink.IsValidLocator(locator)) throw new ArgumentException("Invalid meeting code.", nameof(locator));
        MeetingCode = locator;
        SelectedWorkspaceTab = 1;
        _ = JoinMeetingCommand.ExecuteAsync(null);
    }

    [RelayCommand] private Task JoinMeetingAsync() => RunMedia(JoinMeetingCore);
    private async Task JoinMeetingCore()
    {
        if (InCall) throw new InvalidOperationException("Leave the current call first.");
        var locator = MeetingCode.Trim();
        if (string.IsNullOrEmpty(locator) || locator.Contains('/') || locator.Contains('?')) throw new InvalidOperationException("Enter the meeting code, rather than a URL.");
        var detail = await api.MeetingAsync(locator, lifetime.Token);
        if (!detail.Online) throw new InvalidOperationException("This is an in-person meeting.");
        if (detail.State is 2 or 3) throw new InvalidOperationException("This meeting has ended or was cancelled.");
        // Let the server apply attendance and host/start policy, as Angular does.
        PendingMeetingInvitation = null; MeetingInvitationReview = null;
        CallTitle = detail.Title; ConnectingCall = true;
        var token = await api.MeetingTokenAsync(locator, lifetime.Token);
        meetingLocator = locator;
        try { await api.WatchMeetingAsync(locator, lifetime.Token); await JoinMedia(token, detail.Title, !JoinMeetingMuted); }
        catch { await api.StopWatchingMeetingAsync(); await api.LeaveMeetingAsync(locator); meetingLocator = null; throw; }
    }
    partial void OnMicrophoneEnabledChanged(bool value) { var tile = Videos.FirstOrDefault(x => x.Identity == "You"); if (tile != null) tile.MicrophoneOn = value; }
    [RelayCommand] private Task ToggleMicrophoneAsync() => RunMedia(async () => { await media.SetMicrophoneAsync(!MicrophoneEnabled, Devices, lifetime.Token); MicrophoneEnabled = !MicrophoneEnabled; });
    [RelayCommand] private Task ToggleCameraAsync() => RunMedia(async () =>
    {
        await media.SetCameraAsync(!CameraEnabled, Devices, lifetime.Token); CameraEnabled = !CameraEnabled;
        if (!CameraEnabled) RemoveLocalVideo("camera");
    });
    [RelayCommand] private async Task ToggleScreenAsync()
    {
        if (ScreenEnabled)
        {
            await RunMedia(async () => { await media.SetScreenAsync(false, Devices, lifetime.Token); ScreenEnabled = false; RemoveLocalVideo("screen"); });
            return;
        }
        if (!InCall || disposed) return;
        using var selection = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); shareSelection = selection;
        try
        {
            Feedback = "Choose what to share.";
            var sources = await media.GetScreenSourcesAsync(selection.Token);
            selection.Token.ThrowIfCancellationRequested();
            var choose = ChooseScreenShareSourceAsync ?? throw new InvalidOperationException("Open the call window to choose a sharing source.");
            var portal = sources.FirstOrDefault(x => x.Backend == ScreenCaptureBackend.Portal && x.Kind == ScreenShareKind.Screen)
                ?? sources.FirstOrDefault(x => x.Backend == ScreenCaptureBackend.Portal);
            var selected = portal ?? await choose(sources);
            if (selected == null || selection.IsCancellationRequested || !InCall) { Feedback = ""; return; }
            if (!sources.Contains(selected) && selected.Kind != ScreenShareKind.Screen) throw new InvalidOperationException("The selected window is no longer available.");
            await RunMedia(async () =>
            {
                var version = Volatile.Read(ref screenCaptureVersion);
                try { await media.SetScreenAsync(true, Devices with { ShareSource = selected }, selection.Token); selection.Token.ThrowIfCancellationRequested(); ScreenEnabled = version == Volatile.Read(ref screenCaptureVersion); }
                catch (OperationCanceledException) when (selection.IsCancellationRequested) { }
                catch (UserIdentityDesktop.Service.Media.ScreenShareCanceledException) { Feedback = ""; }
            });
        }
        catch (OperationCanceledException) when (selection.IsCancellationRequested) { }
        catch (Exception ex) { Feedback = ex.Message; }
        finally { if (shareSelection == selection) shareSelection = null; }
    }
    private void RemoveLocalVideo(string kind)
    {
        pendingFrames.TryRemove("You · " + kind, out _);
        var tile = Videos.FirstOrDefault(x => x.Identity == (kind == "camera" ? "You" : "You · " + kind));
        if (tile != null) { if (FocusedVideo == tile) FocusedVideo = null; if (kind == "camera") tile.Frame = null; else Videos.Remove(tile); }
    }
    [RelayCommand] public Task LeaveAsync() { mediaRecovery?.Cancel(); shareSelection?.Cancel(); return RunMedia(LeaveCore, wait: true); }
    private async Task LeaveCore()
    {
        callSounds.Play(null);
        var call = callId; var meeting = meetingLocator;
        // Stop native capture immediately, even if server acknowledgement is unavailable.
        try { await media.DisconnectAsync(); }
        finally
        {
            InCall = false; activeCallUsers.Clear(); activeCallConversation = null; ConnectingCall = false; MicrophoneEnabled = CameraEnabled = ScreenEnabled = false;
            callClock.Stop(); FocusedVideo = null; Videos.Clear(); pendingFrames.Clear();
        }
        await UpdateCallPresence();
        if (call != null) { await api.LeaveCallAsync(call); callId = null; }
        if (meeting != null) { await api.StopWatchingMeetingAsync(); await api.LeaveMeetingAsync(meeting); meetingLocator = null; }
    }

    private CalendarWrite Draft()
    {
        if (string.IsNullOrWhiteSpace(EventTitle) || EventDate is null || EventTime is null) throw new InvalidOperationException("Enter a title, date and time.");
        if (!int.TryParse(DurationMinutes, out var minutes) || minutes <= 0 || minutes > 10080) throw new InvalidOperationException("Duration must be between 1 minute and 7 days.");
        var local = DateTime.SpecifyKind(EventDate.Value.Date + EventTime.Value, DateTimeKind.Unspecified);
        if (TimeZoneInfo.Local.IsInvalidTime(local) || TimeZoneInfo.Local.IsAmbiguousTime(local)) throw new InvalidOperationException("This local time falls in a daylight-saving transition. Choose another time.");
        var participants = People.Where(x => x.Selected).Select(x => x.Id).ToArray();
        var start = TimeZoneInfo.ConvertTimeToUtc(local);
        return new(persistedCalendar?.Id, persistedCalendar?.Version ?? 0, IsMeeting ? 1 : 0, EventTitle.Trim(), EventDescription, start, start.AddMinutes(minutes), TimeZoneInfo.Local.Id, false, participants, ParticipantUnitIds: IsMeeting ? Attendees.UnitIds : null);
    }
    [RelayCommand] private async Task SubmitCalendarFormAsync()
    {
        var ready = false;
        try { ready = Reviewed && reviewedDraft != null && SameDraft(Draft(), reviewedDraft); }
        catch (InvalidOperationException) { }
        if (ready) await SaveEventCommand.ExecuteAsync(null);
        else await ReviewEventCommand.ExecuteAsync(null);
    }
    private async Task Work(Func<Task> action)
    {
        if (IsLoading && auth.Actions?.IsConnected!=false) return;
        IsLoading = auth.Actions?.IsConnected!=false;
        try { Feedback = ""; await action(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Feedback = ex.Message; }
        finally { IsLoading = false; }
    }
    [RelayCommand] private Task ReviewEventAsync() => Work(async () =>
    {
        Reviewed = false; reviewedDraft = null; Conflicts.Clear();
        var draft = Draft(); var review = await api.ReviewAsync(draft, lifetime.Token);
        reviewedDraft = draft; reviewToken = review.Token; hasConflicts = review.Conflicts.Count > 0;
        foreach (var conflict in review.Conflicts) Conflicts.Add(conflict);
        AcknowledgeConflicts = false;
        Reviewed = await Confirm("Review schedule", $"{draft.Title}\n{draft.Description}\n{WorkClock.DateTime(draft.StartUtc.ToLocalTime())} – {WorkClock.DateTime(draft.EndUtc.ToLocalTime())}\nEmployees: {string.Join(", ",Attendees.SelectedPeople.Select(x=>x.Name))}\nBusiness units and children: {string.Join(", ",Attendees.SelectedUnits.Select(x=>x.Name))}\nGuest emails: {string.Join(", ",Attendees.Emails)}\n" + string.Join("\n", Conflicts.Select(x => $"{x.Title}: {WorkClock.DateTime(x.StartUtc.ToLocalTime())} – {WorkClock.DateTime(x.EndUtc.ToLocalTime())}")));
        Feedback = hasConflicts ? "Review the overlaps and acknowledge them before saving." : "No conflicts found. Ready to save.";
    });
    [RelayCommand] private Task SaveEventAsync() => Work(async () =>
    {
        var draft = Draft();
        if (!Reviewed || reviewedDraft is null || !SameDraft(draft, reviewedDraft)) throw new InvalidOperationException("Review the current event before saving.");
        if (hasConflicts && !AcknowledgeConflicts) throw new InvalidOperationException("Acknowledge the reviewed conflicts before saving.");
        if (!await Confirm("Save schedule", $"{draft.Title}\n{draft.Description}\n{WorkClock.DateTime(draft.StartUtc.ToLocalTime())} – {WorkClock.DateTime(draft.EndUtc.ToLocalTime())}\nEmployees: {string.Join(", ",Attendees.SelectedPeople.Select(x=>x.Name))}\nBusiness units and children: {string.Join(", ",Attendees.SelectedUnits.Select(x=>x.Name))}\nGuest emails: {string.Join(", ",Attendees.Emails)}\nConflicts:\n" + string.Join("\n",Conflicts.Select(x=>$"{x.Title}: {WorkClock.DateTime(x.StartUtc.ToLocalTime())} – {WorkClock.DateTime(x.EndUtc.ToLocalTime())}")))) return;
        var saved = await api.SaveCalendarAsync(draft with { ConflictToken = reviewToken, AcknowledgeConflicts = AcknowledgeConflicts }, lifetime.Token);
        persistedCalendar = saved; Reviewed = false; reviewedDraft = null;
        // Configuring online media is a separate operation; retain the saved event if it fails.
        Feedback = "Calendar entry saved.";
        if (IsMeeting)
        {
            try { var meeting = configuredMeeting ?? await api.ConfigureMeetingAsync(saved.Id, lifetime.Token); configuredMeeting = meeting; MeetingCode = meeting.Locator; if (Attendees.Emails.Count > 0) await api.InviteMeetingEmailsAsync(meeting.Locator, Attendees.Emails.ToArray(), lifetime.Token); Feedback = "Meeting scheduled. Link: " + meeting.MeetingLink; }
            catch (Exception ex) { Feedback = "Calendar entry saved; meeting setup or guest email is incomplete: " + ex.Message + " Review and save again to retry this entry."; await RefreshAsync(); return; }
        }
        persistedCalendar = null; configuredMeeting = null; Attendees.Emails.Clear();
        await RefreshAsync();
        SubmissionCompleted?.Invoke(WorkspaceFormKind.Calendar, Feedback);
    });
    public static bool SameDraft(CalendarWrite a, CalendarWrite b) =>
        a with { Participants = Array.Empty<Guid>(), ParticipantUnitIds = null } == b with { Participants = Array.Empty<Guid>(), ParticipantUnitIds = null } && a.Participants.SequenceEqual(b.Participants) && (a.ParticipantUnitIds ?? Array.Empty<Guid>()).SequenceEqual(b.ParticipantUnitIds ?? Array.Empty<Guid>());
    partial void OnSelectedCalendarChanged(CalendarItem? value) { ResponseReviewed = false; responseItem = null; ResponseConflicts.Clear(); }
    [RelayCommand] private Task ReviewInvitationAsync() => Work(async () =>
    {
        if (SelectedCalendar is not { } item) return;
        var review = await api.ReviewResponseAsync(item, lifetime.Token);
        responseItem = item; responseToken = review.Token;
        ResponseConflicts.Clear(); foreach (var conflict in review.Conflicts) ResponseConflicts.Add(conflict);
        AcknowledgeResponseConflicts = false; ResponseReviewed = true;
        Feedback = ResponseConflicts.Count == 0 ? "No conflicts. You can accept the invitation." : "Review and acknowledge these overlaps before accepting.";
    });
    [RelayCommand] private Task AcceptEventAsync() => Work(async () =>
    {
        if (responseItem == null || responseItem != SelectedCalendar || !ResponseReviewed) throw new InvalidOperationException("Review this invitation first.");
        if (ResponseConflicts.Count > 0 && !AcknowledgeResponseConflicts) throw new InvalidOperationException("Acknowledge the invitation conflicts first.");
        if (!await Confirm("Accept invitation", $"{responseItem.Title}\n{responseItem.Description}\n{responseItem.When}\n{responseItem.MeetingLink}\nConflicts:\n" + string.Join("\n",ResponseConflicts.Select(x=>$"{x.Title}: {WorkClock.DateTime(x.StartUtc.ToLocalTime())} – {WorkClock.DateTime(x.EndUtc.ToLocalTime())}")))) return;
        await api.RespondAsync(responseItem, 1, responseToken, AcknowledgeResponseConflicts, lifetime.Token); ResponseReviewed = false; OnInvalidated();
    });
    [RelayCommand] private Task DeclineEventAsync() => Respond(2);
    private Task Respond(int response) => Work(async () => { if (SelectedCalendar is { } item) { if (!await Confirm("Decline invitation", $"{item.Title}\n{item.Description}\n{item.When}")) return; await api.RespondAsync(item, response, ct: lifetime.Token); OnInvalidated(); } });
    [RelayCommand] private Task CancelEventAsync() => Work(async () => { if (SelectedCalendar is { } item) { if (!await Confirm("Cancel schedule", $"{item.Title}\n{item.Description}\n{item.When}")) return; await api.CancelAsync(item, lifetime.Token); OnInvalidated(); } });
    [RelayCommand] private Task AcknowledgeAsync(Announcement? announcement) => Work(async () => { if ((announcement ?? SelectedAnnouncement) is { CanAcknowledge: true } item) { await api.AcknowledgeAsync(item, lifetime.Token); OnInvalidated(); } });
    [RelayCommand] private Task UnlockChatAsync() => Work(async () =>
    {
        var pin = ChatPin; ChatPin = "";
        await chatService.UnlockChatAsync(pin);
        await Chat.LoadRecipientsAsync();
        Feedback = "Chat unlocked for this session. Reopen a conversation to load its messages.";
    });
    [RelayCommand] private void ClearPublishDate() => PublishDate = null;
    [RelayCommand] private Task PublishAsync() => Work(async () =>
    {
        var targets = Audience.Where(x => x.Selected).Select(x => new AnnouncementTarget("User", x.Id)).ToArray();
        if (string.IsNullOrWhiteSpace(AnnouncementTitle) || string.IsNullOrWhiteSpace(AnnouncementBody) || targets.Length == 0)
            throw new InvalidOperationException("Enter a title, message and audience.");
        var now = DateTime.UtcNow;
        var publish = PublishDate is { } day ? DateTime.SpecifyKind(day.Date + (PublishTime ?? TimeSpan.Zero), DateTimeKind.Local).ToUniversalTime() : now;
        if (ExpiryDate is not { } expiryDay) throw new InvalidOperationException("Choose an expiry date.");
        var expiry = DateTime.SpecifyKind(expiryDay.Date + (ExpiryTime ?? TimeSpan.Zero), DateTimeKind.Local).ToUniversalTime();
        if (expiry <= publish) throw new InvalidOperationException("Expiry must be after publication.");
        if (!AnnouncementCategories.Contains(AnnouncementCategory)) throw new InvalidOperationException("Choose a supported category.");
        if (!await Confirm("Publish announcement", $"{AnnouncementTitle}\n{AnnouncementBody}\n{publish.ToLocalTime():f} – {expiry.ToLocalTime():f}\n{targets.Length} recipients")) return;
        await api.PublishAsync(new AnnouncementWrite(null, 0, AnnouncementTitle.Trim(), AnnouncementBody, AnnouncementCategory, publish, expiry, AnnouncementImportant, RequiresAcknowledgement, targets, SendAnnouncementEmail), lifetime.Token);
        AnnouncementTitle = AnnouncementBody = ""; Feedback = publish > now ? "Announcement scheduled." : "Announcement published.";
        await RefreshAsync(); SubmissionCompleted?.Invoke(WorkspaceFormKind.Announcement, Feedback);
    });
    [RelayCommand] private Task JoinSelectedMeetingAsync() => RunMedia(async () =>
    {
        var link=SelectedCalendar?.MeetingLink;
        if(string.IsNullOrWhiteSpace(link))throw new InvalidOperationException("Select an online meeting.");
        var path=Uri.TryCreate(link,UriKind.Absolute,out var uri)?uri.AbsolutePath:link.Split('?')[0];
        var locator=path.TrimEnd('/').Split('/').Last();
        if(!MeetingLink.IsValidLocator(locator))throw new InvalidOperationException("Invalid meeting link.");
        MeetingCode=locator;await JoinMeetingCore();
    });

    private void OnClockFormatChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if(args.PropertyName!=nameof(Clock.TwelveHour))return;
        if (VacationReview is {} review) VacationReview=review with { Shifts=review.Shifts.Select(x=>x with{}).ToList() };
        OnPropertyChanged(nameof(VacationShifts));OnPropertyChanged(nameof(DaySchedule));OnPropertyChanged(nameof(UpcomingMeetings));OnPropertyChanged(nameof(NoUpcomingMeetings));
    }
    public async ValueTask DisposeAsync()
    {
        if(auth.Actions!=null)auth.Actions.Changed-=ConnectionQueueChanged;
        Chat.PropertyChanged-=ChatConnectionChanged;
        Appearance.Dispose();
        chatService.AppearancePreferencesChanged -= AppearanceInvalidated;
        Clock.PropertyChanged -= OnClockFormatChanged;
        if (disposed) return;
        disposed = true;
        mediaRecovery?.Cancel(); Incoming = null; lifetime.Cancel();
        if (notifications != null) notifications.NotificationPosted -= OnNotificationPosted;
        api.MeetingChanged -= OnMeetingChanged;
        chatService.WorkplaceNotificationChanged -= OnWorkplaceNotification;
        chatService.WorkplaceNotificationsRead -= OnNotificationsRead;
        RecentNotifications.Clear();
        chatService.WorkplaceChanged -= OnInvalidated; chatService.ConnectionStateChanged -= OnConnection;
        chatService.IncomingCallReceived -= OnIncoming; chatService.CallParticipantLeft -= OnCallLeft;
        media.ConnectionChanged -= OnMediaConnectionChanged;
        media.VideoReceived -= OnVideo; media.Error -= OnMediaError; media.ParticipantLeft -= OnParticipantLeft;
        media.ParticipantJoined -= OnParticipantJoined; media.ParticipantUpdated -= OnParticipantUpdated; callClock.Stop();
        media.RemoteControlChanged -= OnRemoteControl;
        media.ClipboardTransferChanged -= OnClipboardTransfer;
        media.CaptureStopped -= OnCaptureStopped;
        await mediaGate.WaitAsync();
        try { await LeaveCore(); }
        catch (Exception) { /* Local media is already stopped; the server expires disconnected state. */ }
        finally { MediaBusy = false; mediaGate.Release(); }
    }
}
