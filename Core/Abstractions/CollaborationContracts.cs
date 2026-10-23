using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UserIdentityDesktop.Core.Models;

namespace UserIdentityDesktop.Core;

public interface ICollaborationApi
{
    Task<AppearancePreferences> AppearanceAsync(CancellationToken ct = default);
    Task<AppearancePreferences> SaveAppearanceAsync(AppearancePreferences value, CancellationToken ct = default);
    Task<PolicyCatalog> PoliciesAsync(int country, CancellationToken ct = default);
    Task SavePolicyAsync(VacationPolicyEditor policy, IReadOnlyList<System.Text.Json.JsonElement> rules, CancellationToken ct = default, DateOnly? publishDate = null);
    Task<List<WorkTimeZoneOption>> TimeZonesAsync(DateOnly date, CancellationToken ct = default);
    Task<ScheduleClockPreview> PreviewScheduleClockAsync(string zone, DateOnly date, TimeOnly start, TimeOnly end, int endDayOffset, CancellationToken ct = default);
    Task<ScheduleCatalog> SchedulesAsync(int country, CancellationToken ct = default);
    Task SaveScheduleAsync(WorkScheduleEditor schedule, CancellationToken ct = default);
    Task<DirectoryPage<UnitOption>> UnitsAsync(Guid organization, int page, CancellationToken ct = default);
    Task SaveUnitAsync(UnitOption unit, Guid? schedule, long revision, bool create, CancellationToken ct = default);
    Task<OrganizationRevision> OrganizationRevisionAsync(CancellationToken ct = default);
    Task<List<AssignmentOption>> AssignmentsAsync(Guid user, CancellationToken ct = default);
    Task EndAssignmentAsync(Guid id, long revision, CancellationToken ct = default);
    event Action<string>? MeetingChanged;
    Task WatchMeetingAsync(string locator, CancellationToken ct = default);
    Task StopWatchingMeetingAsync();
    Task<NotificationPage> NotificationsAsync(string? ticks = null, Guid? beforeId = null, CancellationToken ct = default);
    Task<WorkplaceNotification> NotificationAsync(Guid id, CancellationToken ct = default);
    Task ReadNotificationAsync(Guid id, CancellationToken ct = default);
    Task<List<CalendarItem>> SharedCalendarAsync(string kind, Guid id, DateTime from, DateTime to, CancellationToken ct = default);
    Task<VacationReadiness> VacationReadinessAsync(Guid user, DateOnly start, CancellationToken ct = default);
    Task<VacationPreview> PreviewVacationRangeAsync(Guid user, DateOnly start, DateOnly end, string reason, CancellationToken ct = default);
    Task SubmitVacationRangeAsync(Guid user, DateOnly start, DateOnly end, string reason, string token, CancellationToken ct = default);
    Task<List<MeetingUnitOption>> MeetingUnitsAsync(CancellationToken ct = default);
    Task<List<MeetingAudiencePerson>> ResolveMeetingUnitsAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default);
    Task InviteMeetingAudienceAsync(string locator, IReadOnlyList<Guid> users, IReadOnlyList<Guid> units, CancellationToken ct = default);
    Task InviteMeetingEmailsAsync(string locator, IReadOnlyList<string> emails, CancellationToken ct = default);
    Task<VacationPreview> PreviewVacationAsync(Guid user, DateOnly start, int days, string reason, CancellationToken ct = default);
    Task SubmitVacationAsync(Guid user, DateOnly start, int days, string reason, string token, CancellationToken ct = default);
    Task<VacationPage> VacationItemAsync(Guid id, CancellationToken ct = default);
    Task<VacationPage> VacationsAsync(int page, CancellationToken ct = default);
    Task DecideVacationAsync(VacationRequest request, string action, CancellationToken ct = default);
    Task<bool> CallingEnabledAsync(CancellationToken ct = default);
    Task<WorkplaceCatalog> CatalogAsync(CancellationToken ct = default);
    Task<CallSession> CreateCallAsync(Guid peer, bool video, CancellationToken ct = default);
    Task<CallSession> CreateGroupCallAsync(string conversationId, bool video, CancellationToken ct = default);
    Task InviteCallAsync(string callId, Guid user, CancellationToken ct = default);
    Task<CallSession> CallContextAsync(string callId, CancellationToken ct = default);
    Task<MediaToken> CallTokenAsync(string callId, CancellationToken ct = default);
    Task LeaveCallAsync(string callId, CancellationToken ct = default);
    Task DeclineCallAsync(string callId, CancellationToken ct = default);
    Task<List<CalendarItem>> CalendarAsync(Guid userId, DateTime from, DateTime to, CancellationToken ct = default);
    Task<CalendarReview> ReviewAsync(CalendarWrite value, CancellationToken ct = default);
    Task<SavedCalendar> SaveCalendarAsync(CalendarWrite value, CancellationToken ct = default);
    Task<CalendarReview> ReviewResponseAsync(CalendarItem item, CancellationToken ct = default);
    Task RespondAsync(CalendarItem item, int response, string? conflictToken = null, bool acknowledge = false, CancellationToken ct = default);
    Task CancelAsync(CalendarItem item, CancellationToken ct = default);
    Task<List<Announcement>> AnnouncementsAsync(CancellationToken ct = default);
    Task AcknowledgeAsync(Announcement item, CancellationToken ct = default);
    Task PublishAsync(AnnouncementWrite value, CancellationToken ct = default);
    Task<MeetingDetail> MeetingAsync(string locator, CancellationToken ct = default);
    Task<MeetingCredentials> ConfigureMeetingAsync(Guid calendarId, CancellationToken ct = default);
    Task<MediaToken> MeetingTokenAsync(string locator, CancellationToken ct = default);
    Task LeaveMeetingAsync(string locator, CancellationToken ct = default);
}

public interface INativeMediaSession : IAsyncDisposable
{
    Task<MediaDeviceCatalog> GetDevicesAsync(CancellationToken ct = default);
    Task<double> CheckMicrophoneAsync(string device, CancellationToken ct = default);
    Task<VideoPicture> CheckCameraAsync(string device, CancellationToken ct = default);
    Task CheckSpeakerAsync(string device, CancellationToken ct = default);
    Task SetSpeakerAsync(string device);
    bool Connected { get; }
    event Action<VideoPicture>? VideoReceived;
    event Action<string>? ParticipantLeft;
    event Action<MediaParticipant>? ParticipantJoined;
    event Action<MediaParticipantUpdate>? ParticipantUpdated;
    event Action<string>? Error;
    event Action<string>? CaptureStopped;
    event Action<RemoteControlStatus>? RemoteControlChanged;
    Task RequestControlAsync(string identity);
    Task AnswerControlAsync(string identity, bool allow);
    Task RevokeControlAsync();
    void SendRemoteInput(RemoteInput input);
    Task ConnectAsync(MediaToken token, CancellationToken ct = default);
    Task SetMicrophoneAsync(bool enabled, MediaDevices devices, CancellationToken ct = default);
    Task SetCameraAsync(bool enabled, MediaDevices devices, CancellationToken ct = default);
    Task SetScreenAsync(bool enabled, MediaDevices devices, CancellationToken ct = default);
    Task<System.Collections.Generic.IReadOnlyList<ScreenShareSource>> GetScreenSourcesAsync(CancellationToken ct = default);
    Task DisconnectAsync();
}
