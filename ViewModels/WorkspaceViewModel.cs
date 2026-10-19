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
    public ICommand InputCommand { get; } = new RelayCommand<RemoteInput>(value => { if (value != null) sendInput?.Invoke(value); });
    [ObservableProperty] private VideoPicture? _frame;
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
    private string? callId, meetingLocator;
    private CalendarWrite? reviewedDraft;
    private string? reviewToken;
    private bool hasConflicts;
    private RemoteControlStatus remoteStatus = new(Array.Empty<NativePeer>(), null, null, null);
    private CalendarItem? responseItem;
    private string? responseToken;

    public ObservableCollection<CalendarItem> Calendar { get; } = new();
    public ObservableCollection<Announcement> Announcements { get; } = new();
    public ObservableCollection<VideoTileViewModel> Videos { get; } = new();
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
    partial void OnInCallChanged(bool value) { OnPropertyChanged(nameof(CanRequestControl)); OnPropertyChanged(nameof(CanStartCall)); }
    [ObservableProperty] private CalendarItem? _selectedCalendar;
    [ObservableProperty] private Announcement? _selectedAnnouncement;
    [ObservableProperty] private string _feedback = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _callingEnabled;
    [ObservableProperty] private bool _inCall;
    [ObservableProperty] private bool _microphoneEnabled;
    [ObservableProperty] private bool _cameraEnabled;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(RemoteDesktopAvailable))] private bool _screenEnabled;
    public bool RemoteDesktopAvailable => ScreenEnabled || RemotePeers.Any(x => x.CanHost && x.Sharing) || HasRemoteControl || ControlRequester != null;
    [ObservableProperty] private string _callTitle = "";
    [ObservableProperty] private IncomingCall? _incoming;
    [ObservableProperty] private string _meetingCode = "";
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
    [ObservableProperty] private bool _announcementImportant;
    [ObservableProperty] private bool _requiresAcknowledgement;
    [ObservableProperty] private bool _acknowledgeResponseConflicts;
    [ObservableProperty] private bool _responseReviewed;
    [ObservableProperty] private bool _isMeeting = true;
    [ObservableProperty] private bool _acknowledgeConflicts;
    [ObservableProperty] private bool _reviewed;
    [ObservableProperty] private DateTimeOffset? _rangeDate = DateTimeOffset.Now.Date;
    public string IncomingLabel => Incoming is null ? "" : $"{Incoming.CallerName} · {Incoming.MeetingTitle ?? Incoming.CallType + " call"}";
    public string DateRange => RangeDate is { } date ? $"{date:dd MMM} – {date.AddDays(14):dd MMM yyyy}" : "Choose a date";
    public string DeviceHelp => OperatingSystem.IsMacOS()
        ? "Allow microphone, camera and Screen Recording access in System Settings. Remote control also requires Accessibility permission."
        : OperatingSystem.IsLinux() ? "Audio uses your system audio service. Camera devices use V4L2. Screen sharing and remote control currently require X11."
        : "Choose your devices below. Allow camera and microphone access in Windows privacy settings.";

    public WorkspaceViewModel(ChatViewModel chat, ICollaborationApi api, INativeMediaSession media, IAuthService auth, IChatService chatService)
    {
        chat.Workspace = this;
        Chat = chat; this.api = api; this.media = media; this.auth = auth; this.chatService = chatService;
        chatService.WorkplaceChanged += OnInvalidated;
        chatService.ConnectionStateChanged += OnConnection;
        chatService.IncomingCallReceived += OnIncoming;
        chatService.CallParticipantLeft += OnCallLeft;
        media.VideoReceived += OnVideo;
        media.Error += OnMediaError;
        media.ParticipantLeft += OnParticipantLeft;
        media.CaptureStopped += OnCaptureStopped;
        media.RemoteControlChanged += OnRemoteControl;
    }
    partial void OnIncomingChanged(IncomingCall? value) => OnPropertyChanged(nameof(IncomingLabel));
    partial void OnRangeDateChanged(DateTimeOffset? value) { OnPropertyChanged(nameof(DateRange)); OnInvalidated(); }
    private void OnInvalidated() => Dispatcher.UIThread.Post(() => { if (!disposed) _ = RefreshAsync(); });
    private void OnConnection(HubConnectionState state) { if (state == HubConnectionState.Connected) OnInvalidated(); }
    private void OnIncoming(IncomingCall call) => Dispatcher.UIThread.Post(() => { if (!disposed) Incoming = call; });
    private void OnCallLeft(CallParticipantLeft change) => Dispatcher.UIThread.Post(() =>
    {
        if (Incoming?.CallId == change.CallId && change.CallEnded) Incoming = null;
        if (callId == change.CallId && change.CallEnded) _ = LeaveAsync();
    });
    private void OnMediaError(string message) => Dispatcher.UIThread.Post(() => { if (!disposed) Feedback = message; });
    private void OnParticipantLeft(string identity) => Dispatcher.UIThread.Post(() =>
    {
        foreach (var tile in Videos.Where(x => x.Identity == identity || x.Identity.StartsWith(identity + " · ", StringComparison.Ordinal)).ToList()) Videos.Remove(tile);
    });
    private void OnCaptureStopped(string kind) => Dispatcher.UIThread.Post(() =>
    {
        if (disposed) return;
        if (kind == "microphone") MicrophoneEnabled = false;
        if (kind == "camera") CameraEnabled = false;
        if (kind == "screen") ScreenEnabled = false;
        RemoveLocalVideo(kind);
    });
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
                var tile = Videos.FirstOrDefault(x => x.Identity == key);
                if (tile is null && Videos.Count < 16) { tile = new VideoTileViewModel(key, media.SendRemoteInput) { CanControl = remoteStatus.Controlling != null && key.StartsWith(remoteStatus.Controlling + " · screen · ", StringComparison.Ordinal) }; Videos.Add(tile); }
                if (tile != null) tile.Frame = frame;
            }
        }, DispatcherPriority.Background);
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (disposed) return;
        if (refreshing) { refreshAgain = true; return; }
        refreshing = true; IsLoading = true;
        try
        {
            do
            {
                refreshAgain = false;
                var date = RangeDate?.Date ?? DateTime.Today;
                var calendar = api.CalendarAsync(auth.CurrentUser!.Id, date.ToUniversalTime(), date.AddDays(14).ToUniversalTime(), lifetime.Token);
                var announcements = api.AnnouncementsAsync(lifetime.Token);
                var capability = api.CallingEnabledAsync(lifetime.Token);
                var catalog = api.CatalogAsync(lifetime.Token);
                try { await Task.WhenAll(calendar, announcements, capability, catalog); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
                catch (Exception ex) { Feedback = ex.Message; }
                if (disposed) return;
                if (calendar.IsCompletedSuccessfully) { Calendar.Clear(); foreach (var entry in await calendar) Calendar.Add(entry); }
                if (announcements.IsCompletedSuccessfully) { Announcements.Clear(); foreach (var item in await announcements) Announcements.Add(item); }
                CallingEnabled = capability.IsCompletedSuccessfully && await capability;
                if (catalog.IsCompletedSuccessfully)
                {
                    var people = (await catalog).People;
                    UpdatePeople(People, people); UpdatePeople(Audience, people);
                }
            } while (refreshAgain && !disposed);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Feedback = ex.Message; }
        finally { refreshing = false; IsLoading = false; }
    }

    private static void UpdatePeople(ObservableCollection<PersonSelection> target, System.Collections.Generic.List<WorkplacePerson> people)
    {
        var selected = target.Where(x => x.Selected).Select(x => x.Id).ToHashSet();
        target.Clear(); foreach (var person in people) target.Add(new PersonSelection(person) { Selected = selected.Contains(person.Id) });
    }

    private MediaDevices Devices => new(MicrophoneDevice, CameraDevice, ScreenDevice);
    private async Task RunMedia(Func<Task> action)
    {
        if (!await mediaGate.WaitAsync(0)) return;
        try { Feedback = ""; await action(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Feedback = ex.Message; }
        finally { mediaGate.Release(); }
    }
    private async Task JoinMedia(MediaToken token, string title)
    {
        await media.ConnectAsync(token, lifetime.Token);
        InCall = true; CallTitle = title;
        // Devices start disabled. Joining never activates a microphone/camera implicitly.
        MicrophoneEnabled = CameraEnabled = ScreenEnabled = false;
    }
    [RelayCommand] private Task StartVoiceAsync() => StartCall(false);
    [RelayCommand] private Task StartVideoAsync() => StartCall(true);
    private Task StartCall(bool video) => RunMedia(async () =>
    {
        if (InCall) throw new InvalidOperationException("Leave the current call first.");
        if (!CallingEnabled) throw new InvalidOperationException("Calling is not configured on the server.");
        var peer = Chat.SelectedRecipient ?? throw new InvalidOperationException("Select a conversation first.");
        if (peer.IsGroup) throw new InvalidOperationException("Use a meeting code for group calls in this desktop build.");
        var call = await api.CreateCallAsync(peer.ReceiverId, video, lifetime.Token);
        callId = call.CallId;
        try { await JoinMedia(await api.CallTokenAsync(callId, lifetime.Token), peer.ReceiverName); }
        catch { await api.LeaveCallAsync(callId); callId = null; throw; }
    });
    [RelayCommand] private Task AcceptAsync() => RunMedia(async () =>
    {
        if (InCall) throw new InvalidOperationException("Leave the current call first.");
        var incoming = Incoming ?? throw new InvalidOperationException("This invitation is no longer available.");
        if (incoming.MeetingLocator != null)
        {
            MeetingCode = incoming.MeetingLocator;
            await JoinMeetingCore();
        }
        else
        {
            callId = incoming.CallId;
            try { await JoinMedia(await api.CallTokenAsync(callId, lifetime.Token), incoming.CallerName); }
            catch { await api.LeaveCallAsync(callId); callId = null; throw; }
        }
        Incoming = null;
    });
    [RelayCommand] private Task DeclineAsync() => RunMedia(async () =>
    {
        if (Incoming is not { } incoming) return;
        await api.DeclineCallAsync(incoming.CallId, lifetime.Token); Incoming = null;
    });
    [RelayCommand] private Task JoinMeetingAsync() => RunMedia(JoinMeetingCore);
    private async Task JoinMeetingCore()
    {
        if (InCall) throw new InvalidOperationException("Leave the current call first.");
        var locator = MeetingCode.Trim();
        if (string.IsNullOrEmpty(locator) || locator.Contains('/') || locator.Contains('?')) throw new InvalidOperationException("Enter the meeting code, rather than a URL.");
        var detail = await api.MeetingAsync(locator, lifetime.Token);
        if (!detail.Online) throw new InvalidOperationException("This is an in-person meeting.");
        var token = await api.MeetingTokenAsync(locator, lifetime.Token);
        meetingLocator = locator;
        try { await JoinMedia(token, detail.Title); }
        catch { await api.LeaveMeetingAsync(locator); meetingLocator = null; throw; }
    }
    [RelayCommand] private Task ToggleMicrophoneAsync() => RunMedia(async () => { await media.SetMicrophoneAsync(!MicrophoneEnabled, Devices, lifetime.Token); MicrophoneEnabled = !MicrophoneEnabled; });
    [RelayCommand] private Task ToggleCameraAsync() => RunMedia(async () =>
    {
        await media.SetCameraAsync(!CameraEnabled, Devices, lifetime.Token); CameraEnabled = !CameraEnabled;
        if (!CameraEnabled) RemoveLocalVideo("camera");
    });
    [RelayCommand] private Task ToggleScreenAsync() => RunMedia(async () =>
    {
        await media.SetScreenAsync(!ScreenEnabled, Devices, lifetime.Token); ScreenEnabled = !ScreenEnabled;
        if (!ScreenEnabled) RemoveLocalVideo("screen");
    });
    private void RemoveLocalVideo(string kind)
    {
        var tile = Videos.FirstOrDefault(x => x.Identity == "You · " + kind); if (tile != null) Videos.Remove(tile);
    }
    [RelayCommand] public Task LeaveAsync() => RunMedia(LeaveCore);
    private async Task LeaveCore()
    {
        var call = callId; var meeting = meetingLocator;
        // Stop native capture immediately, even if server acknowledgement is unavailable.
        try { await media.DisconnectAsync(); }
        finally
        {
            InCall = false; MicrophoneEnabled = CameraEnabled = ScreenEnabled = false;
            Videos.Clear(); pendingFrames.Clear();
        }
        if (call != null) { await api.LeaveCallAsync(call); callId = null; }
        if (meeting != null) { await api.LeaveMeetingAsync(meeting); meetingLocator = null; }
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
    [RelayCommand] private Task AcknowledgeAsync() => Work(async () => { if (SelectedAnnouncement is { } item) { await api.AcknowledgeAsync(item, lifetime.Token); OnInvalidated(); } });
    [RelayCommand] private Task UnlockChatAsync() => Work(async () =>
    {
        var pin = ChatPin; ChatPin = "";
        await chatService.UnlockChatAsync(pin);
        Feedback = "Chat unlocked for this session. Reopen a conversation to load its messages.";
    });
    [RelayCommand] private Task PublishAsync() => Work(async () =>
    {
        var targets = Audience.Where(x => x.Selected).Select(x => new AnnouncementTarget("User", x.Id)).ToArray();
        if (string.IsNullOrWhiteSpace(AnnouncementTitle) || string.IsNullOrWhiteSpace(AnnouncementBody) || targets.Length == 0)
            throw new InvalidOperationException("Enter a title, message and audience.");
        var now = DateTime.UtcNow;
        await api.PublishAsync(new AnnouncementWrite(null, 0, AnnouncementTitle.Trim(), AnnouncementBody, "General", now, now.AddDays(7), AnnouncementImportant, RequiresAcknowledgement, targets), lifetime.Token);
        AnnouncementTitle = AnnouncementBody = ""; Feedback = "Announcement published for 7 days."; OnInvalidated();
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
        chatService.WorkplaceChanged -= OnInvalidated; chatService.ConnectionStateChanged -= OnConnection;
        chatService.IncomingCallReceived -= OnIncoming; chatService.CallParticipantLeft -= OnCallLeft;
        media.VideoReceived -= OnVideo; media.Error -= OnMediaError; media.ParticipantLeft -= OnParticipantLeft;
        media.RemoteControlChanged -= OnRemoteControl;
        media.CaptureStopped -= OnCaptureStopped;
        await mediaGate.WaitAsync();
        try { await LeaveCore(); }
        catch (Exception) { /* Local media is already stopped; the server expires disconnected state. */ }
        finally { mediaGate.Release(); }
    }
}
