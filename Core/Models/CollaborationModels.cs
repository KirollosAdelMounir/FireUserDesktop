using System;
using System.Collections.Generic;

namespace UserIdentityDesktop.Core.Models;

public sealed record CallSession(string CallId, string CallType, List<Guid> ParticipantIds, string? ConversationId);
public sealed record MediaToken(string ServerUrl, string Token, string? CallId = null);
public sealed record IncomingCall(string CallId, Guid CallerId, string CallerName, string CallType,
    int ParticipantCount, string? MeetingLocator = null, string? MeetingTitle = null, Guid? MeetingInvitationId = null, string? CallerAvatar = null);
public sealed record CallParticipantLeft(string CallId, Guid UserId, bool CallEnded);
public sealed record CalendarItem(Guid Id, string Kind, string Title, string? Description,
    DateTime StartUtc, DateTime EndUtc, Guid OrganizerId, int? Response, long Version,
    bool ReadOnly = false, string? MeetingLink = null, string? TaskState = null, bool Cancelled = false)
{
    public string When => $"{StartUtc.ToLocalTime():ddd, dd MMM} {WorkClock.Time(StartUtc.ToLocalTime())} – {WorkClock.Time(EndUtc.ToLocalTime())}";
}
public sealed record CalendarWrite(Guid? Id, long Version, int Kind, string Title, string Description,
    DateTime StartUtc, DateTime EndUtc, string TimeZoneId, bool IsPrivate, IReadOnlyList<Guid> Participants,
    bool Assign = false, int Priority = 0, int Urgency = 0, string? ConflictToken = null,
    bool AcknowledgeConflicts = false, bool SendEmail = true, IReadOnlyList<Guid>? ParticipantUnitIds = null);
public sealed record CalendarConflict(Guid UserId, string Kind, string Title, DateTime StartUtc, DateTime EndUtc, bool Tentative);
public sealed record CalendarReview(List<CalendarConflict> Conflicts, string Token);
public sealed record SavedCalendar(Guid Id, long Version);
public sealed record WorkplacePerson(Guid Id, string Name, Guid? OrganizationId = null, int? CountryId = null, int ReportingDistance = 0);
public sealed record WorkplaceOrganization(Guid Id, string Name);
public sealed record SharedCalendarUnit(Guid Id, string Name, Guid OrganizationId, Guid? ParentId);
public sealed record SharedCalendarScope(List<WorkplaceOrganization> Organizations, List<SharedCalendarUnit> Units);
public sealed record SharedCalendarChoice(string Kind, Guid Id, string Name);
public sealed record WorkplaceCatalog(List<WorkplacePerson> People, SharedCalendarScope? SharedCalendars = null, List<CountryOption>? Countries = null, List<OrganizationOption>? Organizations = null);
public sealed record WorkplaceNotification(Guid Id, string Title, string Body, string ResourceType, Guid ResourceId, DateTime CreatedUtc, DateTime? ReadUtc);
public sealed record NotificationPage(List<WorkplaceNotification> Items, string? BeforeTicks, Guid? BeforeId, bool HasMore, int Unread);
public sealed record DesktopNotification(string Title, string Body, string Target, DateTime CreatedUtc)
{
    public bool HasDecisionActions => Title is "Vacation approval requested" or "Calendar invitation or update";
    public string ApproveLabel => Title == "Calendar invitation or update" ? "Accept" : "Approve";
    public string RejectLabel => Title == "Calendar invitation or update" ? "Decline" : "Reject";
}
public sealed record ChatActivity(Guid UserId, Guid? ReceiverId, string? ConversationId, string Kind, bool Active, string SourceId);
public sealed record ConversationTyping(string ConversationId, Guid UserId, bool IsTyping);
public sealed record AnnouncementTarget(string Kind, Guid TargetId, bool IncludeDescendants = false);
public sealed record AnnouncementWrite(Guid? Id, long Version, string Title, string Body, string Category,
    DateTime PublishUtc, DateTime ExpiresUtc, bool Important, bool RequiresAcknowledgement,
    System.Collections.Generic.IReadOnlyList<AnnouncementTarget> Targets, bool SendEmail = true);
public sealed record Announcement(Guid Id, string Title, string Body, string Category, DateTime PublishUtc,
    DateTime ExpiresUtc, bool Important, bool RequiresAcknowledgement, long Version, bool Acknowledged)
{
    public bool CanAcknowledge => RequiresAcknowledgement && !Acknowledged;
}
public sealed record MeetingDetail(Guid Id, string Locator, string Title, bool Online, string Location, int State, bool IsHost, bool InvitationPending = false, Guid CalendarEntryId = default, long CalendarVersion = 0, DateTime StartUtc = default, DateTime EndUtc = default);
public sealed record MeetingCredentials(Guid Id, string Locator, string Password, string MeetingLink);
public enum ScreenShareKind { Screen, Window }
public enum ScreenCaptureBackend { Windows, X11, Portal, MacOS }
public sealed record ScreenShareSource(string Id, string Name, ScreenShareKind Kind, ScreenCaptureBackend Backend, int X = 0, int Y = 0, int Width = 0, int Height = 0);
public sealed record MediaDevices(string Microphone, string Camera, string Screen, ScreenShareSource? ShareSource = null);
public sealed record MediaDeviceOption(string Id, string Name);
public sealed record MediaDeviceCatalog(MediaDeviceOption[] Microphones, MediaDeviceOption[] Speakers, MediaDeviceOption[] Cameras, string? Warning = null);
public sealed record MediaParticipantUpdate(string Identity, string Kind, string Value);
public sealed record MediaParticipant(string Identity, string Name);
public sealed record VideoPicture(string Identity, int Width, int Height, byte[] Rgba);
public sealed record EncryptedFile(string Id, string Name, string MimeType, long Size, int ChunkSize, string Key, string NoncePrefix)
{
    [System.Text.Json.Serialization.JsonPropertyName("waveform")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public double[]? Waveform { get; init; }
}

public sealed record VacationShift(DateOnly AnchorDate, DateTime StartUtc, DateTime EndUtc, decimal Days)
{ public string Label => $"{AnchorDate:ddd, d MMM yyyy} · {WorkClock.Time(StartUtc.ToLocalTime())} – {WorkClock.Time(EndUtc.ToLocalTime())} · {Days:0.##} leave {(Days==1 ? "day" : "days")}"; }
public sealed record VacationBalance(int Year, decimal Available, decimal Pending, decimal Approved, decimal Entitlement = 0, decimal Adjustments = 0, decimal Taken = 0)
{ public string Label => $"{Year}: {Available:0.##} days available · entitlement {Entitlement:0.##} · adjustments {Adjustments:0.##} · {Pending:0.##} pending · {Approved:0.##} approved · {Taken:0.##} taken"; }
public sealed record VacationPreview(System.Collections.Generic.List<VacationShift> Shifts, string PreviewToken, System.Collections.Generic.List<VacationBalance> Balances);
public sealed record VacationAllocation(Guid Id, Guid RequestId, DateOnly AnchorDate, DateTime StartUtc, DateTime EndUtc, decimal Days, bool Released);
public sealed record VacationRequest(Guid Id, Guid UserId, int State, string Reason, decimal RequestedDays, long Version)
{ [System.Text.Json.Serialization.JsonIgnore] public string DatesLabel {get;set;} = "";
  public DateTime CreatedUtc {get;init;}
  [System.Text.Json.Serialization.JsonIgnore] public string RequesterName {get;set;} = "Employee";
  public string StateLabel => State switch { 0 => "Pending", 1 => "Approved", 2 => "Rejected", 3 => "Withdrawn", 4 => "Cancellation requested", 5 => "Cancelled", _ => "Unknown" }; }
public sealed record VacationPage(System.Collections.Generic.List<VacationRequest> Requests, bool HasMore, int Page, System.Collections.Generic.List<VacationAllocation>? Allocations = null);

public sealed record VacationReadiness(bool Ready, string? Instructions);
public sealed record MeetingUnitOption(Guid Id, string Name, int MemberCount) { public override string ToString() => $"{Name} · {MemberCount} people (including child units)"; }
public sealed record MeetingAudiencePerson(Guid Id, string Name);

public sealed record MediaConnectionState(string State, string? Reason = null, bool Retryable = false);
