using System;
using System.Collections.Generic;

namespace UserIdentityDesktop.Core.Models;

public sealed record CallSession(string CallId, string CallType, List<Guid> ParticipantIds, string? ConversationId);
public sealed record MediaToken(string ServerUrl, string Token, string? CallId = null);
public sealed record IncomingCall(string CallId, Guid CallerId, string CallerName, string CallType,
    int ParticipantCount, string? MeetingLocator = null, string? MeetingTitle = null);
public sealed record CallParticipantLeft(string CallId, Guid UserId, bool CallEnded);
public sealed record CalendarItem(Guid Id, string Kind, string Title, string? Description,
    DateTime StartUtc, DateTime EndUtc, Guid OrganizerId, int? Response, long Version,
    bool ReadOnly = false, string? MeetingLink = null, string? TaskState = null, bool Cancelled = false)
{
    public string When => $"{StartUtc.ToLocalTime():ddd, dd MMM HH:mm} – {EndUtc.ToLocalTime():HH:mm}";
}
public sealed record CalendarWrite(Guid? Id, long Version, int Kind, string Title, string Description,
    DateTime StartUtc, DateTime EndUtc, string TimeZoneId, bool IsPrivate, IReadOnlyList<Guid> Participants,
    bool Assign = false, int Priority = 0, int Urgency = 0, string? ConflictToken = null,
    bool AcknowledgeConflicts = false, bool SendEmail = true);
public sealed record CalendarConflict(Guid UserId, string Kind, string Title, DateTime StartUtc, DateTime EndUtc, bool Tentative);
public sealed record CalendarReview(List<CalendarConflict> Conflicts, string Token);
public sealed record SavedCalendar(Guid Id, long Version);
public sealed record WorkplacePerson(Guid Id, string Name);
public sealed record WorkplaceCatalog(List<WorkplacePerson> People);
public sealed record AnnouncementTarget(string Kind, Guid TargetId, bool IncludeDescendants = false);
public sealed record AnnouncementWrite(Guid? Id, long Version, string Title, string Body, string Category,
    DateTime PublishUtc, DateTime ExpiresUtc, bool Important, bool RequiresAcknowledgement,
    System.Collections.Generic.IReadOnlyList<AnnouncementTarget> Targets, bool SendEmail = true);
public sealed record Announcement(Guid Id, string Title, string Body, string Category, DateTime PublishUtc,
    DateTime ExpiresUtc, bool Important, bool RequiresAcknowledgement, long Version, bool Acknowledged);
public sealed record MeetingDetail(Guid Id, string Locator, string Title, bool Online, string Location, int State, bool IsHost);
public sealed record MeetingCredentials(Guid Id, string Locator, string Password, string MeetingLink);
public sealed record MediaDevices(string Microphone, string Camera, string Screen);
public sealed record MediaDeviceOption(string Id, string Name);
public sealed record MediaDeviceCatalog(MediaDeviceOption[] Microphones, MediaDeviceOption[] Speakers, MediaDeviceOption[] Cameras, string? Warning = null);
public sealed record VideoPicture(string Identity, int Width, int Height, byte[] Rgba);
public sealed record EncryptedFile(string Id, string Name, string MimeType, long Size, int ChunkSize, string Key, string NoncePrefix);
