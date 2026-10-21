using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UserIdentityDesktop.Core.Models;

public enum UserStatus
{
    Offline = 0,
    Online = 1,
    Away = 2,
    Busy = 3,
    DoNotDisturb = 4,
    InCall = 5,
    InMeeting = 6,
    Presenting = 7,
    SharingScreen = 8
}

public class UserPresence
{
    [JsonPropertyName("userId")]
    public Guid UserId { get; set; }

    [JsonPropertyName("status")]
    public UserStatus Status { get; set; }

    [JsonPropertyName("lastSeen")]
    public DateTime? LastSeen { get; set; }

    [JsonPropertyName("lastActivity")]
    public DateTime? LastActivity { get; set; }
}

public class ChatAttachment
{
    [JsonIgnore]
    public EncryptedFile? Encryption { get; set; }
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = string.Empty;

    [JsonPropertyName("fileSize")]
    public long FileSize { get; set; }

    [JsonPropertyName("fileExtension")]
    public string FileExtension { get; set; } = string.Empty;

    [JsonPropertyName("fileLocation")]
    public string FileLocation { get; set; } = string.Empty;

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsImage =>
        (!string.IsNullOrEmpty(MimeType) && MimeType.StartsWith("image", StringComparison.OrdinalIgnoreCase)) ||
        FileExtension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
        FileExtension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
        FileExtension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
        FileExtension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
        FileExtension.Equals(".webp", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsVideo =>
        !string.IsNullOrEmpty(MimeType) && MimeType.StartsWith("video", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsAudio =>
        !string.IsNullOrEmpty(MimeType) && MimeType.StartsWith("audio", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsDocument => !IsImage && !IsVideo && !IsAudio;

    [JsonIgnore]
    public string FormattedFileSize
    {
        get
        {
            if (FileSize < 1024) return $"{FileSize} B";
            if (FileSize < 1024 * 1024) return $"{FileSize / 1024.0:F1} KB";
            return $"{FileSize / (1024.0 * 1024.0):F1} MB";
        }
    }
}

public sealed record ChatSecurityStatus(bool PinEnabled, bool DeviceTrusted);
public sealed record ChatReaction(Guid UserId, string Emoji);
public sealed record SeenReceipt(Guid UserId, DateTime SeenAt);
public sealed record CallSummary(string CallId, string CallType, string Status, string CallerName, bool IsActive, int? DurationSeconds);
public class ChatMessage
{
    [JsonIgnore] public bool IsLocked { get; set; }
    public string? MessageId { get; set; }
    public bool? IsDeleted { get; set; }
    public bool? IsEdited { get; set; }
    public string? ReplyToMessageId { get; set; }
    public string? ReplyToSenderName { get; set; }
    public List<ChatReaction>? Reactions { get; set; }
    public List<SeenReceipt>? SeenDetails { get; set; }
    public CallSummary? CallSummary { get; set; }
    private readonly string fallbackId = Guid.NewGuid().ToString("N");
    public string? ConversationId { get; set; }
    [JsonPropertyName("id")]
    public JsonElement IdRaw { get; set; }

    [JsonIgnore]
    public string Id
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(MessageId)) return MessageId;
            if (IdRaw.ValueKind == JsonValueKind.String)
                return IdRaw.GetString() ?? string.Empty;
            if (IdRaw.ValueKind == JsonValueKind.Object)
            {
                if (IdRaw.TryGetProperty("timestamp", out var ts))
                    return ts.ToString();
                if (IdRaw.TryGetProperty("$oid", out var oid))
                    return oid.GetString() ?? string.Empty;
            }
            return fallbackId;
        }
    }

    [JsonPropertyName("sender")]
    public Guid Sender { get; set; }

    [JsonPropertyName("receiver")]
    public Guid Receiver { get; set; }

    [JsonPropertyName("senderName")]
    public string? SenderName { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("attachments")]
    public List<ChatAttachment>? Attachments { get; set; } = new();

    [JsonPropertyName("timeSent")]
    public DateTime TimeSent { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("seen")]
    public bool Seen { get; set; }

    [JsonPropertyName("timeSeen")]
    public DateTime? TimeSeen { get; set; }
}

public class RecipientDto
{
    public List<Guid> ParticipantIds { get; set; } = new();
    public Guid? OwnerId { get; set; }
    public bool IsGroup { get; set; }
    public string? ConversationId { get; set; }
    [JsonPropertyName("receiverId")]
    public Guid ReceiverId { get; set; }

    [JsonPropertyName("senderId")]
    public Guid SenderId { get; set; }

    [JsonPropertyName("receiverName")]
    public string ReceiverName { get; set; } = string.Empty;

    [JsonPropertyName("lastMessage")]
    public string? LastMessage { get; set; }

    [JsonPropertyName("lastSent")]
    public DateTime LastSent { get; set; }

    [JsonPropertyName("isSeen")]
    public bool IsSeen { get; set; }

    [JsonIgnore]
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
}

public class SystemUserSearch
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("firstName")]
    public string FirstName { get; set; } = string.Empty;

    [JsonPropertyName("lastName")]
    public string LastName { get; set; } = string.Empty;

    [JsonPropertyName("compressedBase64Profile")]
    public string? CompressedBase64Profile { get; set; }

    [JsonIgnore]
    public string FullName => $"{FirstName} {LastName}".Trim();

    [JsonIgnore]
    public string Initials
    {
        get
        {
            var first = !string.IsNullOrWhiteSpace(FirstName) ? FirstName[0].ToString().ToUpper() : "";
            var last = !string.IsNullOrWhiteSpace(LastName) ? LastName[0].ToString().ToUpper() : "";
            var initials = $"{first}{last}";
            return string.IsNullOrEmpty(initials) ? "?" : initials;
        }
    }
}

public class ChatDto
{
    public string? ConversationId { get; set; }
    public string? ReplyToMessageId { get; set; }
    [JsonPropertyName("sender")]
    public Guid Sender { get; set; }

    [JsonPropertyName("receiver")]
    public Guid Receiver { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("attachments")]
    public object? Attachments { get; set; }
}

public sealed record OrganizationName(string Name, Guid Id = default, string? Code = null, Guid OrganizationId = default, int? Type = null, string? CountryName = null, string? CityName = null);
public sealed record PositionName(string Title, string? UnitName, string? OrganizationName, Guid Id = default, Guid UnitId = default, string? Code = null, string? LevelName = null, string? Grade = null);
public sealed record ReportingManager(string DisplayName, string PositionTitle, Guid UserId = default);
public sealed record EmployeeOrganization(OrganizationName? Organization, PositionName? Position, List<OrganizationName> UnitPath, List<PositionName> ReportingPath, List<ReportingManager>? ReportingManagers, bool ReportingManagersHasMore = false)
{
    public string Summary => (Organization?.Name ?? "No organization assignment") + "\n" + (Position?.Title ?? "") +
        "\n" + string.Join(" › ", UnitPath.ConvertAll(x => x.Name)) + "\nReporting line: " + string.Join(" → ", ReportingPath.ConvertAll(x => x.Title)) +
        "\nManagers: " + string.Join(", ", (ReportingManagers ?? new()).ConvertAll(x => x.DisplayName + " · " + x.PositionTitle));
}
public class ViewUserProfileDto
{
    [JsonIgnore] public EmployeeOrganization? Organization { get; set; }
    [JsonPropertyName("firstName")]
    public string FirstName { get; set; } = string.Empty;

    [JsonPropertyName("lastName")]
    public string LastName { get; set; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("gender")]
    public bool Gender { get; set; }

    [JsonPropertyName("phoneNumber")]
    public PhoneNumberDetailDto? PhoneNumber { get; set; }

    [JsonPropertyName("address")]
    public AddressDetailDto? Address { get; set; }

    [JsonPropertyName("profilePicture")]
    public string? ProfilePicture { get; set; }

    [JsonPropertyName("dateOfBirth")]
    public DateTime? DateOfBirth { get; set; }

    [JsonIgnore]
    public string FullName => $"{FirstName} {LastName}".Trim();

    [JsonIgnore]
    public string FormattedPhone =>
        PhoneNumber != null
            ? $"{PhoneNumber.Country?.CountryCode} {PhoneNumber.PhoneNum}".Trim()
            : "Not specified";

    [JsonIgnore]
    public string FormattedAddress
    {
        get
        {
            if (Address == null) return "Not specified";
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Address.FullAddress)) parts.Add(Address.FullAddress);
            if (Address.City != null && !string.IsNullOrWhiteSpace(Address.City.CityName)) parts.Add(Address.City.CityName);
            return parts.Count > 0 ? string.Join(", ", parts) : "Not specified";
        }
    }
}

public class PhoneNumberDetailDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("phoneNum")]
    public string PhoneNum { get; set; } = string.Empty;

    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; set; }

    [JsonPropertyName("country")]
    public CountryDetailDto? Country { get; set; }
}

public class CountryDetailDto
{
    [JsonPropertyName("countryCode")]
    public string CountryCode { get; set; } = string.Empty;

    [JsonPropertyName("countryName")]
    public string CountryName { get; set; } = string.Empty;
}

public class AddressDetailDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("fullAddress")]
    public string FullAddress { get; set; } = string.Empty;

    [JsonPropertyName("postalCode")]
    public string PostalCode { get; set; } = string.Empty;

    [JsonPropertyName("city")]
    public CityDetailDto? City { get; set; }
}

public class CityDetailDto
{
    [JsonPropertyName("cityName")]
    public string CityName { get; set; } = string.Empty;
}
