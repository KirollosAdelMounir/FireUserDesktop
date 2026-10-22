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
    public bool IsScreen => Identity.Contains(" · screen", StringComparison.Ordinal);
    public bool HasFrame => Frame != null;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(MediaLabel)), NotifyPropertyChangedFor(nameof(MicrophoneLabel))] private bool _microphoneOn;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(MediaLabel))] private string _quality = "Checking";
    public string MicrophoneLabel => MicrophoneOn ? "Microphone on" : "Microphone muted";
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
    public ChatViewModel Chat { get; }
    private readonly ICollaborationApi api;
    private readonly INativeMediaSession media;
    private readonly IAuthService auth;
    private readonly IChatService chatService;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim mediaGate = new(1, 1);
    private readonly ConcurrentDictionary<string, VideoPicture> pendingFrames = new();
    private int frameScheduled;
    private bool disposed, refreshing, refreshAgain;
    private CancellationTokenSource? shareSelection;
    private long screenCaptureVersion;
    public Func<System.Collections.Generic.IReadOnlyList<ScreenShareSource>, Task<ScreenShareSource?>>? ChooseScreenShareSourceAsync { get; set; }
    private string? callId, meetingLocator;
    private readonly UserIdentityDesktop.Service.Media.CallSounds callSounds = new();
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
    public bool HasFocusedScreen => FocusedVideo?.IsScreen == true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasFocusedVideo)), NotifyPropertyChangedFor(nameof(HasFocusedScreen))] private VideoTileViewModel? _focusedVideo;
    [ObservableProperty] private bool _showCallMembers;
    [ObservableProperty] private bool _showCallChat;
    [ObservableProperty] private PersonSelection? _selectedCallInvitee;
    public bool CanInviteToCall => InCall && callId != null;
    [RelayCommand] private void ToggleCallChat() => ShowCallChat = !ShowCallChat;
    [RelayCommand] private Task InviteCallAsync() => RunMedia(async () =>
    {
        if (callId == null || SelectedCallInvitee == null) throw new InvalidOperationException("Choose a colleague to invite to this call.");
        await api.InviteCallAsync(callId, SelectedCallInvitee.Id, lifetime.Token); Feedback = "Call invitation sent.";
    });
    [ObservableProperty] private string _callElapsed = "00:00";
    private DateTimeOffset callStarted;
    private readonly DispatcherTimer callClock = new() { Interval = TimeSpan.FromSeconds(1) };
    public string ParticipantSummary => $"{Videos.Count(x => !x.IsScreen)} participants · {CallElapsed}";
    partial void OnCallElapsedChanged(string value) => OnPropertyChanged(nameof(ParticipantSummary));
    [RelayCommand] private void FocusVideo(VideoTileViewModel? tile) => FocusedVideo = tile;
    [RelayCommand] private void ClearVideoFocus() => FocusedVideo = null;
    [RelayCommand] private void ToggleCallMembers() => ShowCallMembers = !ShowCallMembers;
    public ObservableCollection<CalendarConflict> Conflicts { get; } = new();
    public ObservableCollection<CalendarConflict> ResponseConflicts { get; } = new();
    public ObservableCollection<PersonSelection> People { get; } = new();
    public ObservableCollection<PersonSelection> Audience { get; } = new();
    public ObservableCollection<NativePeer> RemotePeers { get; } = new();
    [ObservableProperty] private NativePeer? _selectedRemotePeer;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(RemoteDesktopAvailable))] private string? _controlRequester;
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
    public CalendarItem[] DaySchedule => Calendar.Where(x => !x.Cancelled && x.StartUtc.ToLocalTime() < (RangeDate?.Date ?? DateTime.Today).AddDays(1) && x.EndUtc.ToLocalTime() > (RangeDate?.Date ?? DateTime.Today)).OrderBy(x => x.StartUtc).ToArray();
    public CalendarItem[] UpcomingMeetings => Calendar.Where(x => x.Kind == "Meeting" && !x.Cancelled).OrderBy(x => x.StartUtc).ToArray();
    public bool HasDaySchedule => DaySchedule.Length != 0;
    public bool HasAnnouncements => Announcements.Count != 0;
    public string DeviceHelp => OperatingSystem.IsMacOS()
        ? "Allow microphone, camera and Screen Recording access in System Settings. Remote control also requires Accessibility permission."
        : OperatingSystem.IsLinux() ? "Audio uses the system audio service; cameras use V4L2. Wayland screen sharing uses your compositor's ScreenCast portal and PipeWire; X11 sessions capture the selected screen or window. Remote control requires an X11 session and a supported desktop source."
        : "Choose your devices below. Allow camera and microphone access in Windows privacy settings.";
    public WorkspaceViewModel(ChatViewModel chat, ICollaborationApi api, INativeMediaSession media, IAuthService auth, IChatService chatService, INotificationService? notifications = null)
    {
        chat.Workspace = this;
        Chat = chat; this.api = api; this.media = media; this.auth = auth; this.chatService = chatService;
        InitializeNotifications(notifications);
        api.MeetingChanged += OnMeetingChanged;
        chatService.WorkplaceChanged += OnInvalidated;
        chatService.ConnectionStateChanged += OnConnection;
        chatService.IncomingCallReceived += OnIncoming;
        chatService.CallParticipantLeft += OnCallLeft;
        media.VideoReceived += OnVideo;
        media.ParticipantJoined += OnParticipantJoined;
        media.ParticipantUpdated += OnParticipantUpdated;
        Videos.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ParticipantSummary));
        callClock.Tick += (_, _) => CallElapsed = (DateTimeOffset.UtcNow - callStarted).ToString(@"mm\:ss");
        media.Error += OnMediaError;
        media.ParticipantLeft += OnParticipantLeft;
        media.CaptureStopped += OnCaptureStopped;
        media.RemoteControlChanged += OnRemoteControl;
    }
    partial void OnIncomingChanged(IncomingCall? value)
    {
        OnPropertyChanged(nameof(IncomingLabel));
        callSounds.Play(value == null ? null : "Ringtone");
    }
    partial void OnRangeDateChanged(DateTimeOffset? oldValue, DateTimeOffset? newValue)
    {
        OnPropertyChanged(nameof(DateRange)); OnPropertyChanged(nameof(DaySchedule)); OnPropertyChanged(nameof(HasDaySchedule));
        EventDate = newValue;
        if (oldValue?.Year != newValue?.Year || oldValue?.Month != newValue?.Month) OnInvalidated();
    }
    private void OnInvalidated() => Dispatcher.UIThread.Post(() => { if (!disposed) _ = RefreshAsync(); });
    private void OnConnection(HubConnectionState state) { if (state == HubConnectionState.Connected) OnInvalidated(); }
    private void OnIncoming(IncomingCall call) => Dispatcher.UIThread.Post(() =>
    {
        if (disposed) return;
        Incoming = call;
        notifications?.ShowNotification(call.MeetingTitle ?? "Incoming call", call.CallerName + " is calling", call.MeetingLocator is {} locator ? "fireuserconnect://meeting/" + locator : "fireuserconnect://call/" + Uri.EscapeDataString(call.CallId));
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
        if (tile == null && Videos.Count < 64) { tile = new VideoTileViewModel(identity); Videos.Add(tile); }
        if (tile == null) return;
        if (update.Kind == "microphone") tile.MicrophoneOn = update.Value == "1";
        if (update.Kind == "camera" && update.Value == "0") tile.Frame = null;
        if (update.Kind == "quality") tile.Quality = update.Value switch { "0" => "Poor", "1" => "Good", "2" => "Excellent", "3" => "Disconnected", _ => "Checking" };
    });
    private void OnParticipantJoined(MediaParticipant participant) => Dispatcher.UIThread.Post(() =>
    {
        if (disposed) return;
        if (Incoming == null) callSounds.Play(null);
        var tile = Videos.FirstOrDefault(x => x.Identity == participant.Identity);
        if (tile == null && Videos.Count < 64) { tile = new VideoTileViewModel(participant.Identity, media.SendRemoteInput); Videos.Add(tile); }
        if (tile != null) tile.Name = participant.Name;
    });
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
    private void OnRemoteControl(RemoteControlStatus status) => Dispatcher.UIThread.Post(() =>
    {
        if (disposed) return;
        remoteStatus = status;
        var selected = SelectedRemotePeer?.Identity;
        RemotePeers.Clear(); foreach (var peer in status.Peers.Where(x => x.CanHost && x.Sharing)) RemotePeers.Add(peer);
        SelectedRemotePeer = RemotePeers.FirstOrDefault(x => x.Identity == selected);
        ControlRequester = status.RequestedBy;
        OnPropertyChanged(nameof(RemoteDesktopAvailable));
        HasRemoteControl = status.Controller != null || status.Controlling != null;
        RemoteControlLabel = status.Controller != null ? status.Controller + " is controlling your desktop · Ctrl+Alt+Escape revokes access" : status.Controlling != null ? "Controlling " + status.Controlling + " · Ctrl+Alt+Escape stops control" : "Remote control requires a native participant sharing their default desktop. Browser clients are excluded.";
        foreach (var tile in Videos) tile.CanControl = status.Controlling != null && tile.Identity.StartsWith(status.Controlling + " · screen · ", StringComparison.Ordinal);
        OnPropertyChanged(nameof(CanRequestControl));
    });
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
                if (tile is null && Videos.Count < 16) { tile = new VideoTileViewModel(tileKey, media.SendRemoteInput) { Name = tileKey.Contains(" · screen", StringComparison.Ordinal) ? (tileKey.StartsWith("You · ", StringComparison.Ordinal) ? "Your screen" : (Videos.FirstOrDefault(x => x.Identity == tileKey.Split(" · ")[0])?.Name ?? "Participant") + " · Screen share") : tileKey, CanControl = remoteStatus.Controlling != null && key.StartsWith(remoteStatus.Controlling + " · screen · ", StringComparison.Ordinal) }; Videos.Add(tile); }
                if (tile != null)
                {
                    var firstFrame = tile.Frame == null;
                    tile.Frame = frame;
                    if (firstFrame && tile.IsScreen) FocusedVideo = tile;
                }
            }
        }, DispatcherPriority.Background);
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (disposed) return;
        if (refreshing) { refreshAgain = true; return; }
        refreshing = true;
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
                    Load(LoadCalendar(date, date.AddDays(42)), items =>
                    {
                        Calendar.Clear(); foreach (var entry in items) Calendar.Add(entry);
                        OnPropertyChanged(nameof(DaySchedule)); OnPropertyChanged(nameof(HasDaySchedule)); OnPropertyChanged(nameof(UpcomingMeetings));
                    }),
                    Load(api.AnnouncementsAsync(lifetime.Token), items => { Announcements.Clear(); foreach (var item in items) Announcements.Add(item); OnPropertyChanged(nameof(HasAnnouncements)); }),
                    Load(LoadNotificationSnapshot(), _ => { }),
                    Load(api.CallingEnabledAsync(lifetime.Token), enabled => CallingEnabled = enabled),
                    Load(api.CatalogAsync(lifetime.Token), catalog => { UpdatePeople(People, catalog.People); UpdatePeople(Audience, catalog.People); ApplyCalendarPeople(catalog.People); ApplySharedCalendars(catalog.SharedCalendars); })
                );
            } while (refreshAgain && !disposed);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Feedback = ex.Message; }
        finally { refreshing = false; }
    }

    private static void UpdatePeople(ObservableCollection<PersonSelection> target, System.Collections.Generic.List<WorkplacePerson> people)
    {
        var selected = target.Where(x => x.Selected).Select(x => x.Id).ToHashSet();
        target.Clear(); foreach (var person in people) target.Add(new PersonSelection(person) { Selected = selected.Contains(person.Id) });
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
    private async Task JoinMedia(MediaToken token, string title)
    {
        Videos.Clear(); FocusedVideo = null;
        Videos.Add(new VideoTileViewModel("You") { Name = "You", AvatarInitials = auth.CurrentUser?.Initials });
        CallTitle = title; ConnectingCall = true;
        try { await media.ConnectAsync(token, lifetime.Token); }
        catch { ConnectingCall = false; throw; }
        CallTitle = title; callStarted = DateTimeOffset.UtcNow; CallElapsed = "00:00"; callClock.Start(); InCall = true;
        ConnectingCall = false;
        // Devices start disabled. Joining never activates a microphone/camera implicitly.
        MicrophoneEnabled = CameraEnabled = ScreenEnabled = false;
    }
    public Task JoinExistingCallAsync(string id) => RunMedia(async () =>
    {
        if (InCall) throw new InvalidOperationException("Leave the current call first.");
        var context = await api.CallContextAsync(id, lifetime.Token);
        callId = context.CallId;
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
        callId = call.CallId; callSounds.Play("Ringback");
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
            callId = incoming.CallId;
            CallTitle = incoming.CallerName; ConnectingCall = true;
            try { await JoinMedia(await api.CallTokenAsync(callId, lifetime.Token), incoming.CallerName); }
            catch { callSounds.Play(null); await api.LeaveCallAsync(callId); callId = null; throw; }
        }
        Incoming = null;
    });
    [RelayCommand] private Task DeclineAsync() => RunMedia(async () =>
    {
        if (Incoming is not { } incoming) return;
        await api.DeclineCallAsync(incoming.CallId, lifetime.Token); Incoming = null;
    });
    public void OpenMeeting(string locator)
    {
        if (!MeetingLink.IsValidLocator(locator)) throw new ArgumentException("Invalid meeting code.", nameof(locator));
        MeetingCode = locator;
        SelectedWorkspaceTab = 1;
        Feedback = "Meeting link opened. Choose Join meeting when you are ready.";
    }

    [RelayCommand] private Task JoinMeetingAsync() => RunMedia(JoinMeetingCore);
    private async Task JoinMeetingCore()
    {
        if (InCall) throw new InvalidOperationException("Leave the current call first.");
        var locator = MeetingCode.Trim();
        if (string.IsNullOrEmpty(locator) || locator.Contains('/') || locator.Contains('?')) throw new InvalidOperationException("Enter the meeting code, rather than a URL.");
        var detail = await api.MeetingAsync(locator, lifetime.Token);
        if (!detail.Online) throw new InvalidOperationException("This is an in-person meeting.");
        if (detail.InvitationPending)
        {
            SelectedWorkspaceTab = 1;
            PendingMeetingInvitation = detail; MeetingInvitationReview = null;
            throw new InvalidOperationException("Accept this invitation before joining. Review the invitation below, acknowledge any scheduling conflicts, then choose Accept invitation.");
        }
        if (detail.State is 2 or 3) throw new InvalidOperationException("This meeting has ended or was cancelled.");
        // Let the server apply attendance and host/start policy, as Angular does.
        PendingMeetingInvitation = null; MeetingInvitationReview = null;
        CallTitle = detail.Title; ConnectingCall = true;
        var token = await api.MeetingTokenAsync(locator, lifetime.Token);
        meetingLocator = locator;
        try { await api.WatchMeetingAsync(locator, lifetime.Token); await JoinMedia(token, detail.Title); }
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
    [RelayCommand] public Task LeaveAsync() { shareSelection?.Cancel(); return RunMedia(LeaveCore, wait: true); }
    private async Task LeaveCore()
    {
        callSounds.Play(null);
        var call = callId; var meeting = meetingLocator;
        // Stop native capture immediately, even if server acknowledgement is unavailable.
        try { await media.DisconnectAsync(); }
        finally
        {
            InCall = false; ConnectingCall = false; MicrophoneEnabled = CameraEnabled = ScreenEnabled = false;
            callClock.Stop(); FocusedVideo = null; Videos.Clear(); pendingFrames.Clear();
        }
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
        return new(null, 0, IsMeeting ? 1 : 0, EventTitle.Trim(), EventDescription, start, start.AddMinutes(minutes), TimeZoneInfo.Local.Id, false, participants);
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
        if (IsLoading) return;
        IsLoading = true;
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
        AcknowledgeConflicts = false; Reviewed = true;
        Feedback = hasConflicts ? "Review the overlaps and acknowledge them before saving." : "No conflicts found. Ready to save.";
    });
    [RelayCommand] private Task SaveEventAsync() => Work(async () =>
    {
        var draft = Draft();
        if (reviewedDraft is null || !SameDraft(draft, reviewedDraft)) throw new InvalidOperationException("Review the current event before saving.");
        if (hasConflicts && !AcknowledgeConflicts) throw new InvalidOperationException("Acknowledge the reviewed conflicts before saving.");
        var saved = await api.SaveCalendarAsync(draft with { ConflictToken = reviewToken, AcknowledgeConflicts = AcknowledgeConflicts }, lifetime.Token);
        Reviewed = false; reviewedDraft = null;
        // Configuring online media is a separate operation; retain the saved event if it fails.
        Feedback = "Calendar entry saved.";
        if (IsMeeting)
        {
            try { var meeting = await api.ConfigureMeetingAsync(saved.Id, lifetime.Token); MeetingCode = meeting.Locator; Feedback = "Meeting scheduled. Code: " + meeting.Locator; }
            catch (Exception ex) { Feedback = "Calendar entry saved; online meeting setup failed: " + ex.Message; }
        }
        OnInvalidated();
    });
    public static bool SameDraft(CalendarWrite a, CalendarWrite b) =>
        a with { Participants = Array.Empty<Guid>() } == b with { Participants = Array.Empty<Guid>() } && a.Participants.SequenceEqual(b.Participants);
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
        await api.RespondAsync(responseItem, 1, responseToken, AcknowledgeResponseConflicts, lifetime.Token); ResponseReviewed = false; OnInvalidated();
    });
    [RelayCommand] private Task DeclineEventAsync() => Respond(2);
    private Task Respond(int response) => Work(async () => { if (SelectedCalendar is { } item) { await api.RespondAsync(item, response, ct: lifetime.Token); OnInvalidated(); } });
    [RelayCommand] private Task CancelEventAsync() => Work(async () => { if (SelectedCalendar is { } item) { await api.CancelAsync(item, lifetime.Token); OnInvalidated(); } });
    [RelayCommand] private Task AcknowledgeAsync(Announcement? announcement) => Work(async () => { if ((announcement ?? SelectedAnnouncement) is { CanAcknowledge: true } item) { await api.AcknowledgeAsync(item, lifetime.Token); OnInvalidated(); } });
    [RelayCommand] private Task UnlockChatAsync() => Work(async () =>
    {
        var pin = ChatPin; ChatPin = "";
        await chatService.UnlockChatAsync(pin);
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
        await api.PublishAsync(new AnnouncementWrite(null, 0, AnnouncementTitle.Trim(), AnnouncementBody, AnnouncementCategory, publish, expiry, AnnouncementImportant, RequiresAcknowledgement, targets, SendAnnouncementEmail), lifetime.Token);
        AnnouncementTitle = AnnouncementBody = ""; Feedback = publish > now ? "Announcement scheduled." : "Announcement published."; OnInvalidated();
    });
    [RelayCommand] private Task JoinSelectedMeetingAsync() => RunMedia(async () =>
    {
        var link = SelectedCalendar?.MeetingLink;
        if (string.IsNullOrWhiteSpace(link)) throw new InvalidOperationException("Select an online meeting.");
        var path = Uri.TryCreate(link, UriKind.Absolute, out var uri) ? uri.AbsolutePath : link;
        MeetingCode = path.TrimEnd('/').Split('/').Last(); await JoinMeetingCore();
    });

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true; lifetime.Cancel();
        if (notifications != null) notifications.NotificationPosted -= OnNotificationPosted;
        api.MeetingChanged -= OnMeetingChanged;
        chatService.WorkplaceNotificationChanged -= OnWorkplaceNotification;
        chatService.WorkplaceNotificationsRead -= OnNotificationsRead;
        RecentNotifications.Clear();
        chatService.WorkplaceChanged -= OnInvalidated; chatService.ConnectionStateChanged -= OnConnection;
        chatService.IncomingCallReceived -= OnIncoming; chatService.CallParticipantLeft -= OnCallLeft;
        media.VideoReceived -= OnVideo; media.Error -= OnMediaError; media.ParticipantLeft -= OnParticipantLeft;
        media.ParticipantJoined -= OnParticipantJoined; media.ParticipantUpdated -= OnParticipantUpdated; callClock.Stop();
        media.RemoteControlChanged -= OnRemoteControl;
        media.CaptureStopped -= OnCaptureStopped;
        await mediaGate.WaitAsync();
        try { await LeaveCore(); }
        catch (Exception) { /* Local media is already stopped; the server expires disconnected state. */ }
        finally { MediaBusy = false; mediaGate.Release(); }
    }
}
