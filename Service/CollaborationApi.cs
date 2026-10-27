using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using UserIdentityDesktop.Core;
using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.Data;

namespace UserIdentityDesktop.Service;

public sealed partial class CollaborationApi(IAuthService auth, IConfigurationService config) : ICollaborationApi
{
    public Task<CallLogPage> CallLogsAsync(string? before = null, CancellationToken ct = default) => Get<CallLogPage>("Calls/Logs" + (before == null ? "" : "?before=" + Segment(before)), ct);
    public async Task MarkCallLogsSeenAsync(DateTime through, CancellationToken ct = default) => await Post<bool>("Calls/Logs/Seen", new { through }, ct);
    public Task<AppearancePreferences> AppearanceAsync(CancellationToken ct = default) => Get<AppearancePreferences>("Workplace/appearance/preferences",ct);
    public Task<AppearancePreferences> SaveAppearanceAsync(AppearancePreferences value, CancellationToken ct = default) => Post<AppearancePreferences>("Workplace/appearance/preferences",value,ct);
    private async Task<T> Send<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var client = auth.CreateAuthenticatedClient();
        using var request = new HttpRequestMessage(method, config.ServerUrl.TrimEnd('/') + "/api/" + path);
        if (method != HttpMethod.Get) request.Content = JsonContent.Create(body ?? new { }, options: ApiPayload.Json);
        using var response = await client.SendAsync(request, ct);
        return await ApiPayload.ReadAsync<T>(response, ct);
    }
    private Task<T> Get<T>(string path, CancellationToken ct) => Send<T>(HttpMethod.Get, path, null, ct);
    private Task<T> Post<T>(string path, object? body, CancellationToken ct) => Send<T>(HttpMethod.Post, path, body, ct);
    private static string Segment(string value) => Uri.EscapeDataString(value);
    public Task<NotificationPage> NotificationsAsync(string? ticks = null, Guid? beforeId = null, CancellationToken ct = default) => Get<NotificationPage>("Workplace/notifications?size=50" + (ticks == null ? "" : $"&beforeTicks={Segment(ticks)}&beforeId={beforeId}"), ct);
    public Task<WorkplaceNotification> NotificationAsync(Guid id, CancellationToken ct = default) => Get<WorkplaceNotification>($"Workplace/notifications/{id}", ct);
    public async Task ReadNotificationAsync(Guid id, CancellationToken ct = default) => await Post<object>($"Workplace/notifications/{id}/read", null, ct);
    public Task<List<CalendarItem>> SharedCalendarAsync(string kind, Guid id, DateTime from, DateTime to, CancellationToken ct = default) => Get<List<CalendarItem>>($"Workplace/shared-calendar/{Segment(kind)}/{id}?from={Segment(from.ToUniversalTime().ToString("O"))}&to={Segment(to.ToUniversalTime().ToString("O"))}", ct);
    public Task<PolicyCatalog> PoliciesAsync(int country, CancellationToken ct = default) => Get<PolicyCatalog>($"Workplace/policies/{country}", ct);
    public async Task SavePolicyAsync(VacationPolicyEditor policy, IReadOnlyList<System.Text.Json.JsonElement> rules, CancellationToken ct = default, DateOnly? publishDate = null) => await Post<object>("Workplace/policies", new { policy, rules, publishDate }, ct);
    public Task<List<WorkTimeZoneOption>> TimeZonesAsync(DateOnly date, CancellationToken ct = default) => Get<List<WorkTimeZoneOption>>($"Workplace/time-zones?date={date:yyyy-MM-dd}", ct);
    public Task<ScheduleClockPreview> PreviewScheduleClockAsync(string zone, DateOnly date, TimeOnly start, TimeOnly end, int endDayOffset, CancellationToken ct = default) => Post<ScheduleClockPreview>("Workplace/schedules/clock-preview", new { timeZoneId=zone,date,startTime=start,endTime=end,endDayOffset },ct);
    public Task<ScheduleCatalog> SchedulesAsync(int country, CancellationToken ct = default) => Get<ScheduleCatalog>($"Workplace/schedules/{country}", ct);
    public async Task SaveScheduleAsync(WorkScheduleEditor schedule, CancellationToken ct = default) => await Post<object>("Workplace/schedules", schedule, ct);
    public Task<DirectoryPage<UnitOption>> UnitsAsync(Guid organization, int page, CancellationToken ct = default) => Get<DirectoryPage<UnitOption>>($"Organization/units?organizationId={organization}&page={page}&pageSize=200", ct);
    public async Task SaveUnitAsync(UnitOption unit, Guid? schedule, long revision, bool create, CancellationToken ct = default) => await Send<object>(create ? HttpMethod.Post : HttpMethod.Put, create ? "Organization/units" : $"Organization/units/{unit.Id}", new { unit.OrganizationId, unit.ParentId, unit.Type, unit.Code, unit.Name, unit.Description, workingScheduleId = schedule, updateWorkingSchedule = true, expectedRevision = revision }, ct);
    public Task<OrganizationRevision> OrganizationRevisionAsync(CancellationToken ct = default) => Get<OrganizationRevision>("Organization/group", ct);
    public Task<List<AssignmentOption>> AssignmentsAsync(Guid user, CancellationToken ct = default) => Get<List<AssignmentOption>>($"Organization/users/{user}/assignments", ct);
    public async Task EndAssignmentAsync(Guid id, long revision, CancellationToken ct = default) => await Send<object>(HttpMethod.Delete, $"Organization/assignments/{id}?endUtc={Segment(DateTime.UtcNow.ToString("O"))}&expectedRevision={revision}", null, ct);
    public Task<VacationReadiness> VacationReadinessAsync(Guid user, DateOnly start, CancellationToken ct = default) => Get<VacationReadiness>($"Workplace/vacations/readiness/{user}?date={start:yyyy-MM-dd}", ct);
    public Task<VacationPreview> PreviewVacationRangeAsync(Guid user, DateOnly start, DateOnly end, string reason, CancellationToken ct = default) => Post<VacationPreview>("Workplace/vacations/preview", new { userId = user, startDate = start, endDate = end, days = 0, reason }, ct);
    public async Task SubmitVacationRangeAsync(Guid user, DateOnly start, DateOnly end, string reason, string token, CancellationToken ct = default) => await Post<object>("Workplace/vacations", new { userId = user, startDate = start, endDate = end, days = 0, reason, previewToken = token }, ct);
    public Task<List<MeetingUnitOption>> MeetingUnitsAsync(CancellationToken ct = default) => Get<List<MeetingUnitOption>>("Workplace/meeting-units", ct);
    public Task<List<MeetingAudiencePerson>> ResolveMeetingUnitsAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default) => Post<List<MeetingAudiencePerson>>("Workplace/meeting-units/resolve", ids, ct);
    public async Task InviteMeetingAudienceAsync(string locator, IReadOnlyList<Guid> users, IReadOnlyList<Guid> units, CancellationToken ct = default)
    {
        if (locator != watchedMeeting || meetingConnection?.State != Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected) throw new InvalidOperationException("Reconnect to the meeting before inviting attendees.");
        await Microsoft.AspNetCore.SignalR.Client.HubConnectionExtensions.InvokeAsync(meetingConnection, "InviteAudience", new { userIds = users, unitIds = units }, ct);
    }
    public async Task InviteMeetingEmailsAsync(string locator, IReadOnlyList<string> emails, CancellationToken ct = default) => await Post<object>($"Meetings/{Segment(locator)}/invitations/email", new { emails }, ct);
    public Task<VacationPreview> PreviewVacationAsync(Guid user, DateOnly start, int days, string reason, CancellationToken ct = default) => Post<VacationPreview>("Workplace/vacations/preview", new { userId = user, startDate = start, days, reason }, ct);
    public async Task SubmitVacationAsync(Guid user, DateOnly start, int days, string reason, string token, CancellationToken ct = default) => await Post<object>("Workplace/vacations", new { userId = user, startDate = start, days, reason, previewToken = token }, ct);
    public Task<VacationPage> VacationItemAsync(Guid id, CancellationToken ct = default) => Get<VacationPage>($"Workplace/vacations?item={id}&page=1&size=100", ct);
    public Task<VacationPage> VacationsAsync(int page, CancellationToken ct = default) => Get<VacationPage>($"Workplace/vacations?page={page}&size=100", ct);
    public async Task DecideVacationAsync(VacationRequest request, string action, CancellationToken ct = default) => await Post<object>($"Workplace/vacations/{request.Id}/decision", new { version = request.Version, action }, ct);
    public async Task<bool> CallingEnabledAsync(CancellationToken ct = default) => (await Get<Capabilities>("SystemConfiguration/capabilities", ct)).CallingEnabled;
    private sealed record Capabilities(bool CallingEnabled);
    public Task<WorkplaceCatalog> CatalogAsync(CancellationToken ct = default) => Get<WorkplaceCatalog>("Workplace/catalog", ct);
    public Task<CallSession> CreateCallAsync(Guid peer, bool video, CancellationToken ct = default) => Post<CallSession>("Calls", new { participantIds = new[] { peer }, callType = video ? "video" : "audio" }, ct);
    public Task<CallSession> CreateGroupCallAsync(string conversationId, bool video, CancellationToken ct = default) => Post<CallSession>("Calls", new { conversationId, callType = video ? "video" : "audio" }, ct);
    public async Task InviteCallAsync(string callId, Guid user, CancellationToken ct = default) => await Send<CallSession>(HttpMethod.Put, $"Calls/{Segment(callId)}/Participants", new { participantIds = new[] { user } }, ct);
    public Task<CallSession> CallContextAsync(string callId, CancellationToken ct = default) => Get<CallSession>($"Calls/{Segment(callId)}/Context", ct);
    public Task<MediaToken> CallTokenAsync(string id, CancellationToken ct = default) => Get<MediaToken>($"Calls/{Segment(id)}/Token", ct);
    public async Task LeaveCallAsync(string id, CancellationToken ct = default) => await Post<bool>($"Calls/{Segment(id)}/Leave", null, ct);
    public async Task DeclineCallAsync(string id, CancellationToken ct = default) => await Post<bool>($"Calls/{Segment(id)}/Decline", null, ct);
    public Task<List<CalendarItem>> CalendarAsync(Guid user, DateTime from, DateTime to, CancellationToken ct = default) => Get<List<CalendarItem>>($"Workplace/calendar/{user}?from={Segment(from.ToUniversalTime().ToString("O"))}&to={Segment(to.ToUniversalTime().ToString("O"))}", ct);
    public Task<CalendarReview> ReviewAsync(CalendarWrite value, CancellationToken ct = default) => Post<CalendarReview>("Workplace/calendar/review", value, ct);
    public Task<SavedCalendar> SaveCalendarAsync(CalendarWrite value, CancellationToken ct = default) => Post<SavedCalendar>("Workplace/calendar", value, ct);
    public Task<CalendarReview> ReviewResponseAsync(CalendarItem item, CancellationToken ct = default) => Post<CalendarReview>($"Workplace/calendar/{item.Id}/response-review?version={item.Version}", null, ct);
    public async Task RespondAsync(CalendarItem item, int response, string? conflictToken = null, bool acknowledge = false, CancellationToken ct = default) => await Post<object>($"Workplace/calendar/{item.Id}/response?response={response}&version={item.Version}&acknowledgeConflicts={acknowledge.ToString().ToLowerInvariant()}{(conflictToken == null ? "" : "&conflictToken=" + Segment(conflictToken))}", null, ct);
    public async Task CancelAsync(CalendarItem item, CancellationToken ct = default) => await Post<object>($"Workplace/calendar/{item.Id}/cancel?version={item.Version}", null, ct);
    public Task<List<Announcement>> AnnouncementsAsync(CancellationToken ct = default) => Get<List<Announcement>>("Workplace/announcements", ct);
    public async Task AcknowledgeAsync(Announcement item, CancellationToken ct = default) => await Post<object>($"Workplace/announcements/{item.Id}/acknowledge?version={item.Version}", null, ct);
    public async Task PublishAsync(AnnouncementWrite value, CancellationToken ct = default) => await Post<object>("Workplace/announcements", value, ct);
    public Task<MeetingDetail> MeetingAsync(string locator, CancellationToken ct = default) => Get<MeetingDetail>($"Meetings/{Segment(locator)}", ct);
    public Task<MeetingCredentials> ConfigureMeetingAsync(Guid calendarId, CancellationToken ct = default) => Post<MeetingCredentials>("Meetings", new { calendarEntryId = calendarId, online = true, location = "", waitingRoom = true, retentionDays = 60 }, ct);
    public Task<MediaToken> MeetingTokenAsync(string locator, CancellationToken ct = default) => Post<MediaToken>($"Meetings/{Segment(locator)}/token", null, ct);
    public async Task LeaveMeetingAsync(string locator, CancellationToken ct = default) => await Post<object>($"Meetings/{Segment(locator)}/leave", null, ct);
}
