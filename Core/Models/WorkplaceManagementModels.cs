using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace UserIdentityDesktop.Core.Models;
public sealed record CountryOption(int Id, string CountryName) { public override string ToString() => CountryName; }
public sealed record OrganizationOption(Guid Id, string Name, int? CountryId) { public override string ToString() => Name; }
public sealed record DirectoryPage<T>(List<T> Items, int Page, int PageSize, bool HasMore, long Revision);
public sealed record UnitOption(Guid Id, Guid OrganizationId, Guid? ParentId, int Type, string Code, string Name, string? Description, bool IsActive) { public override string ToString() => Name; }
public sealed record AssignmentOption(Guid Id, Guid UserId, Guid PositionId, string PositionTitle, DateTime ValidFromUtc, DateTime? ValidToUtc, bool IsCancelled) { public override string ToString() => PositionTitle; }
public sealed record OrganizationRevision([property: JsonPropertyName("version")] long Revision);
public sealed class VacationPolicyEditor
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int CountryId { get; set; }
    public string Name { get; set; } = "Annual leave";
    public DateOnly EffectiveFrom { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public DateOnly? EffectiveTo { get; set; }
    public decimal BaseDays { get; set; } = 21;
    public decimal CarryoverLimit { get; set; }
    public int OvernightDayWeight { get; set; } = 1;
    public bool ImmediateQualification { get; set; }
    public DateTime? PublishUtc { get; set; }
    public long Version { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
    public override string ToString() => Name + " · " + EffectiveFrom.ToString("yyyy-MM-dd");
}
public sealed record PolicyPendingWrite(VacationPolicyEditor Policy, List<JsonElement> Rules, DateOnly? PublishDate);
public sealed record PendingPolicyChange(Guid PolicyId, DateTime PublishUtc, PolicyPendingWrite Change);
public sealed record PolicyCatalog(List<VacationPolicyEditor> Policies, List<JsonElement> Rules, List<PendingPolicyChange>? PendingChanges=null);
public sealed class WorkScheduleEditor
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int CountryId { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid? UnitId { get; set; }
    public string Name { get; set; } = "Day shift";
    public string TimeZoneId { get; set; } = "Africa/Cairo";
    public DateOnly EffectiveFrom { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public DateOnly? EffectiveTo { get; set; }
    public int WeekendMask { get; set; } = 96;
    public TimeOnly StartTime { get; set; } = new(8,0);
    public TimeOnly EndTime { get; set; } = new(16,0);
    public int EndDayOffset { get; set; }
    public bool IsDefault { get; set; } = true;
    public bool SendEmail { get; set; } = true;
    public int AnnouncementDays { get; set; } = 7;
    public long Version { get; set; }
    public override string ToString() => Name;
}
public sealed record ScheduleCatalog(List<WorkScheduleEditor> Schedules);
public sealed class VacationRuleEditor : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Changed() { foreach(var name in new[]{"Kind","Threshold","AdditionalDays","CustomKey","MinimumLabel","RequiresMinimum","IsCustom","QualificationSummary"}) PropertyChanged?.Invoke(this,new(name)); }
    public bool RequiresMinimum => Kind != 2;
    public bool IsCustom => Kind == 3;
    public string MinimumLabel => Kind == 0 ? "Completed years of service" : Kind == 1 ? "Minimum age (years)" : "Minimum qualifying value";
    public string QualificationSummary => (Kind == 0 ? $"After {Threshold} completed service years" : Kind == 1 ? $"At age {Threshold} or older" : Kind == 2 ? "When qualified by an administrator" : $"When {CustomKey ?? "the employee fact"} is at least {Threshold}") + $": {AdditionalDays} extra leave days per year";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PolicyId { get; set; }
    public string Name { get; set; } = "Additional leave";
    private int kind; public int Kind { get => kind; set { if(kind==value)return;kind=value;Changed(); } }
    private decimal threshold; public decimal Threshold { get => threshold; set { if(threshold==value)return;threshold=value;Changed(); } }
    private decimal additionalDays; public decimal AdditionalDays { get => additionalDays; set { if(additionalDays==value)return;additionalDays=value;Changed(); } }
    public string? ExclusionGroup { get; set; }
    private string? customKey; public string? CustomKey { get => customKey; set { if(customKey==value)return;customKey=value;Changed(); } }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
