using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UserIdentityDesktop.Core;
using UserIdentityDesktop.Core.Models;
namespace UserIdentityDesktop.ViewModels;
public partial class WorkplaceManagementViewModel(ICollaborationApi api) : ObservableObject
{
    public ClockDisplaySettings Clock => ClockDisplaySettings.Shared;
    public ObservableCollection<WorkTimeZoneOption> TimeZones { get; } = new();
    [ObservableProperty] private WorkTimeZoneOption? _selectedTimeZone;
    [ObservableProperty] private DateTimeOffset? _policyPublishDate;
    [ObservableProperty] private string _remoteHours = "Choose a timezone and date to preview remote work hours.";
    private int clockGeneration, zoneGeneration;
    partial void OnSelectedTimeZoneChanged(WorkTimeZoneOption? value) { if(value != null) TimeZoneId=value.Id; }
    partial void OnTimeZoneIdChanged(string value) { if(SelectedTimeZone?.Id != value) SelectedTimeZone=TimeZones.FirstOrDefault(x=>x.Id==value); _=PreviewClock(); }
    partial void OnShiftStartChanged(TimeSpan? value) => _=PreviewClock();
    partial void OnShiftEndChanged(TimeSpan? value) => _=PreviewClock();
    partial void OnEndDayOffsetChanged(int value) => _=PreviewClock();
    partial void OnScheduleFromChanged(DateTimeOffset? value) => _=LoadTimeZones();
    [RelayCommand] private void ApplyPolicyImmediately() => PolicyPublishDate=null;
    public async Task LoadTimeZones()
    {
        var generation=++zoneGeneration;
        try {
            var zones=await api.TimeZonesAsync(DateOnly.FromDateTime((ScheduleFrom??DateTimeOffset.Now).Date));
            if(generation!=zoneGeneration)return;
            TimeZones.Clear();foreach(var zone in zones??new())TimeZones.Add(zone);
            SelectedTimeZone=TimeZones.FirstOrDefault(x=>x.Id==TimeZoneId);await PreviewClock();
        } catch(Exception ex) {if(generation==zoneGeneration)RemoteHours=ex.Message;}
    }
    public Task RefreshClockPreviewAsync() => PreviewClock();
    private async Task PreviewClock()
    {
        var generation=++clockGeneration;
        if(ShiftStart==null||ShiftEnd==null||ScheduleFrom==null)return;
        try {
            var result=await api.PreviewScheduleClockAsync(TimeZoneId,DateOnly.FromDateTime(ScheduleFrom.Value.Date),TimeOnly.FromTimeSpan(ShiftStart.Value),TimeOnly.FromTimeSpan(ShiftEnd.Value),EndDayOffset);
            if(generation!=clockGeneration||result==null)return;
            if(TimeZoneId!=result.TimeZoneId)TimeZoneId=result.TimeZoneId;
            var zone=TimeZoneInfo.FindSystemTimeZoneById(result.TimeZoneId);
            RemoteHours=$"{(result.IsDaylightSavingTime ? "Summer time applied" : "Standard time applied")} on {ScheduleFrom:d}\nWork zone ({result.TimeZoneId}): {Clock.Time(TimeZoneInfo.ConvertTimeFromUtc(result.StartUtc,zone))} – {Clock.Time(TimeZoneInfo.ConvertTimeFromUtc(result.EndUtc,zone))}\nYour local time ({TimeZoneInfo.Local.Id}): {result.StartUtc.ToLocalTime():d} {Clock.Time(result.StartUtc.ToLocalTime())} – {result.EndUtc.ToLocalTime():d} {Clock.Time(result.EndUtc.ToLocalTime())}";
        } catch(Exception ex) {if(generation==clockGeneration)RemoteHours=ex.Message;}
    }
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }
    public ObservableCollection<CountryOption> Countries { get; } = new();
    public ObservableCollection<OrganizationOption> Organizations { get; } = new();
    public ObservableCollection<WorkplacePerson> People { get; } = new();
    public ObservableCollection<VacationPolicyEditor> Policies { get; } = new();
    public ObservableCollection<VacationRuleEditor> PolicyRules { get; } = new();
    public ObservableCollection<WorkScheduleEditor> Schedules { get; } = new();
    public ObservableCollection<WorkScheduleEditor> UnitSchedules { get; } = new();
    public ObservableCollection<UnitOption> Units { get; } = new();
    public ObservableCollection<AssignmentOption> Assignments { get; } = new();
    private List<JsonElement> rules = new();
    private List<PendingPolicyChange> pendingPolicies = new();
    private long revision;
    private int countryGeneration, organizationGeneration, personGeneration;
    private bool countryLoading, organizationLoading;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _feedback = "";
    [ObservableProperty] private CountryOption? _country;
    [ObservableProperty] private OrganizationOption? _organization;
    [ObservableProperty] private WorkplacePerson? _person;
    [ObservableProperty] private AssignmentOption? _assignment;
    [ObservableProperty] private VacationPolicyEditor? _policy;
    [ObservableProperty] private WorkScheduleEditor? _schedule;
    [ObservableProperty] private WorkScheduleEditor? _unitSchedule;
    [ObservableProperty] private UnitOption? _unit;
    [ObservableProperty] private UnitOption? _parentUnit;
    [ObservableProperty] private string _policyName = "Annual leave";
    [ObservableProperty] private decimal _baseDays = 21;
    [ObservableProperty] private decimal _carryover;
    [ObservableProperty] private DateTimeOffset? _policyFrom = DateTimeOffset.Now.Date;
    public string RenewalDate => PolicyFrom is {} date ? new DateTime(date.Year + 1, 1, 1).ToString("d MMMM yyyy") : "Choose an effective date";
    partial void OnPolicyFromChanged(DateTimeOffset? value) => OnPropertyChanged(nameof(RenewalDate));
    [ObservableProperty] private string _unitName = "";
    [ObservableProperty] private string _unitCode = "";
    [ObservableProperty] private string _unitDescription = "";
    [ObservableProperty] private int _unitType;
    public string[] UnitTypes { get; } = ["Root", "Division", "Department", "Section", "Office", "Team"];
    public string[] ShiftPresets { get; } = ["Day shift", "Midday shift", "Night shift"];
    [ObservableProperty] private int _shiftPreset;
    [ObservableProperty] private bool _scheduleIsDefault;
    [ObservableProperty] private string _scheduleName = "Day shift";
    [ObservableProperty] private string _timeZoneId = "Africa/Cairo";
    [ObservableProperty] private TimeSpan? _shiftStart = new(8,0,0);
    [ObservableProperty] private TimeSpan? _shiftEnd = new(16,0,0);
    [ObservableProperty] private int _endDayOffset;
    [ObservableProperty] private int _weekendMask = 96;
    public bool SundayOff { get => (WeekendMask & 1) != 0; set => WeekendMask = value ? WeekendMask | 1 : WeekendMask & ~1; }
    public bool MondayOff { get => (WeekendMask & 2) != 0; set => WeekendMask = value ? WeekendMask | 2 : WeekendMask & ~2; }
    public bool TuesdayOff { get => (WeekendMask & 4) != 0; set => WeekendMask = value ? WeekendMask | 4 : WeekendMask & ~4; }
    public bool WednesdayOff { get => (WeekendMask & 8) != 0; set => WeekendMask = value ? WeekendMask | 8 : WeekendMask & ~8; }
    public bool ThursdayOff { get => (WeekendMask & 16) != 0; set => WeekendMask = value ? WeekendMask | 16 : WeekendMask & ~16; }
    public bool FridayOff { get => (WeekendMask & 32) != 0; set => WeekendMask = value ? WeekendMask | 32 : WeekendMask & ~32; }
    public bool SaturdayOff { get => (WeekendMask & 64) != 0; set => WeekendMask = value ? WeekendMask | 64 : WeekendMask & ~64; }
    partial void OnWeekendMaskChanged(int value) { foreach (var day in new[] { nameof(SundayOff), nameof(MondayOff), nameof(TuesdayOff), nameof(WednesdayOff), nameof(ThursdayOff), nameof(FridayOff), nameof(SaturdayOff) }) OnPropertyChanged(day); }

    [ObservableProperty] private DateTimeOffset? _scheduleFrom = DateTimeOffset.Now.Date;
    public bool HasCountry => Country != null;
    public bool HasOrganization => Organization != null;
    public bool HasAssignment => Assignment != null;
    partial void OnCountryChanged(CountryOption? value) { OnPropertyChanged(nameof(HasCountry)); _ = LoadCountry(++countryGeneration); }
    partial void OnOrganizationChanged(OrganizationOption? value) { OnPropertyChanged(nameof(HasOrganization)); _ = LoadOrganization(++organizationGeneration); }
    partial void OnPersonChanged(WorkplacePerson? value) => _ = LoadAssignments(++personGeneration);
    partial void OnAssignmentChanged(AssignmentOption? value) => OnPropertyChanged(nameof(HasAssignment));
    partial void OnPolicyChanged(VacationPolicyEditor? value)
    {
        var pending=pendingPolicies.FirstOrDefault(x=>x.PolicyId==value?.Id);
        PolicyPublishDate=pending?.Change.PublishDate is {} publication ? new DateTimeOffset(publication.ToDateTime(TimeOnly.MinValue)) : null;
        PolicyRules.Clear();
        if (value != null) foreach (var rule in pending?.Change.Rules ?? rules.Where(x => x.TryGetProperty("policyId",out var id) && id.GetGuid() == value.Id).ToList())
            PolicyRules.Add(rule.Deserialize<VacationRuleEditor>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
        value=pending?.Change.Policy??value;
        PolicyName = value?.Name ?? "Annual leave"; BaseDays = value?.BaseDays ?? 21; Carryover = value?.CarryoverLimit ?? 0;
        PolicyFrom = new DateTimeOffset((value?.EffectiveFrom ?? DateOnly.FromDateTime(DateTime.Today)).ToDateTime(TimeOnly.MinValue));
    }
    partial void OnUnitChanged(UnitOption? value)
    {
        UnitName = value?.Name ?? ""; UnitCode = value?.Code ?? ""; UnitDescription = value?.Description ?? ""; UnitType = value?.Type ?? 0;
        ParentUnit = Units.FirstOrDefault(x => x.Id == value?.ParentId);
        UnitSchedule = Schedules.FirstOrDefault(x => x.UnitId == value?.Id && value != null && (x.EffectiveTo == null || x.EffectiveTo > DateOnly.FromDateTime(DateTime.Today)));
    }
    partial void OnShiftPresetChanged(int value)
    {
        ScheduleName = ShiftPresets[Math.Clamp(value,0,2)];
        ShiftStart = TimeSpan.FromHours(value == 1 ? 14 : value == 2 ? 22 : 8); ShiftEnd = TimeSpan.FromHours(value == 1 ? 22 : value == 2 ? 6 : 16); EndDayOffset = value == 2 ? 1 : 0;
    }
    partial void OnScheduleChanged(WorkScheduleEditor? value)
    {
        if (value == null) return;
        ScheduleIsDefault = value.IsDefault; ScheduleName = value.Name; TimeZoneId = value.TimeZoneId; ShiftStart = value.StartTime.ToTimeSpan(); ShiftEnd = value.EndTime.ToTimeSpan(); EndDayOffset = value.EndDayOffset; WeekendMask = value.WeekendMask;
        ScheduleFrom = new DateTimeOffset(value.EffectiveFrom.ToDateTime(TimeOnly.MinValue));
    }
    public async Task InitializeAsync()
    {
        await LoadTimeZones();
        await Run(async () => { var catalog = await api.CatalogAsync(); foreach (var x in catalog.Countries ?? new()) Countries.Add(x); foreach (var x in catalog.Organizations ?? new()) Organizations.Add(x); foreach (var x in catalog.People) People.Add(x); revision = (await api.OrganizationRevisionAsync()).Revision; });
    }
    private async Task LoadCountry(int generation)
    {
        var country = Country; countryLoading = true; Policy = null; OnPolicyChanged(null); Schedule = null; Policies.Clear(); Schedules.Clear(); UnitSchedules.Clear();
        if (country == null) { countryLoading = false; return; }
        try
        {
            var catalog = await api.PoliciesAsync(country.Id); var schedules = await api.SchedulesAsync(country.Id);
            if (generation != countryGeneration) return;
            rules = catalog.Rules; pendingPolicies=catalog.PendingChanges??new(); foreach (var x in catalog.Policies) Policies.Add(x); foreach (var x in schedules.Schedules) Schedules.Add(x);
            RefreshUnitSchedules(); if (Unit != null) OnUnitChanged(Unit);
        }
        catch (Exception ex) { Feedback = ex.Message; }
        finally { if (generation == countryGeneration) countryLoading = false; }
    }
    private void RefreshUnitSchedules()
    {
        UnitSchedules.Clear(); foreach (var item in Schedules.Where(x => x.OrganizationId == Organization?.Id && x.CountryId == Organization?.CountryId && x.EffectiveFrom <= DateOnly.FromDateTime(DateTime.Today) && (x.EffectiveTo == null || x.EffectiveTo > DateOnly.FromDateTime(DateTime.Today)))) UnitSchedules.Add(item);
    }
    private async Task LoadOrganization(int generation)
    {
        var org = Organization; organizationLoading = true; Unit = null; Units.Clear(); if (org == null) { organizationLoading = false; return; }
        Country = Countries.FirstOrDefault(x => x.Id == org.CountryId);
        try
        {
            var all = new List<UnitOption>(); int page = 1; DirectoryPage<UnitOption> result;
            do { result = await api.UnitsAsync(org.Id, page++); all.AddRange(result.Items); } while (result.HasMore && generation == organizationGeneration);
            if (generation != organizationGeneration) return;
            revision = result.Revision; foreach (var x in all) Units.Add(x); RefreshUnitSchedules();
        }
        catch (Exception ex) { Feedback = ex.Message; }
        finally { if (generation == organizationGeneration) organizationLoading = false; }
    }
    private async Task LoadAssignments(int generation)
    {
        var person = Person; Assignment = null; Assignments.Clear(); if (person == null) return;
        try { var values = await api.AssignmentsAsync(person.Id); if (generation != personGeneration) return; foreach (var x in values.Where(x => !x.IsCancelled && x.ValidFromUtc <= DateTime.UtcNow && (x.ValidToUtc == null || x.ValidToUtc > DateTime.UtcNow))) Assignments.Add(x); }
        catch (Exception ex) { Feedback = ex.Message; }
    }
    private async Task Run(Func<Task> action)
    {
        if (Busy) return; if (countryLoading || organizationLoading) { Feedback = "Wait for the selected country and organization to finish loading."; return; } Busy = true; Feedback = "";
        try { await action(); } catch (Exception ex) { Feedback = ex.Message; } finally { Busy = false; }
    }
    [RelayCommand] private void AddPolicyRule() => PolicyRules.Add(new VacationRuleEditor());
    [RelayCommand] private void RemovePolicyRule(VacationRuleEditor? rule) { if (rule != null) PolicyRules.Remove(rule); }
    [RelayCommand] private void NewPolicy() { Policy = null; OnPolicyChanged(null); }
    [RelayCommand] private void NewUnit() { Unit = null; OnUnitChanged(null); }
    [RelayCommand] private void UseDefaultSchedule() => UnitSchedule = null;
    [RelayCommand] private void NewSchedule() { Schedule = null; ScheduleIsDefault = false; ShiftPreset = 0; OnShiftPresetChanged(0); ScheduleFrom = DateTimeOffset.Now.Date; }
    [RelayCommand] private Task SavePolicyAsync() => Run(async () =>
    {
        if (Country == null || PolicyFrom == null || string.IsNullOrWhiteSpace(PolicyName) || BaseDays < 0 || Carryover < 0) throw new InvalidOperationException("Choose a country, name, effective date and non-negative balances.");
        var source=pendingPolicies.FirstOrDefault(x=>x.PolicyId==Policy?.Id)?.Change.Policy??Policy;
        var policy = source == null ? new VacationPolicyEditor() : JsonSerializer.Deserialize<VacationPolicyEditor>(JsonSerializer.Serialize(source))!;
        policy.Version=Policy?.Version??policy.Version;
        policy.CountryId = Country.Id; policy.Name = PolicyName.Trim(); policy.BaseDays = BaseDays; policy.CarryoverLimit = Carryover; policy.EffectiveFrom = DateOnly.FromDateTime(PolicyFrom.Value.Date); policy.EffectiveTo = new DateOnly(policy.EffectiveFrom.Year + 1,1,1);
        if (PolicyRules.Any(x => string.IsNullOrWhiteSpace(x.Name) || x.Kind is < 0 or > 3 || x.Threshold < 0 || x.AdditionalDays < 0)) throw new InvalidOperationException("Each qualification rule needs a name, a valid kind and non-negative values.");
        foreach (var rule in PolicyRules) rule.PolicyId = policy.Id;
        var retainedRules = PolicyRules.Select(x => JsonSerializer.SerializeToElement(x,new JsonSerializerOptions(JsonSerializerDefaults.Web))).ToArray();
        if (ConfirmAsync == null || !await ConfirmAsync("Save vacation policy", $"{policy.Name}\n{policy.BaseDays} annual days\n{policy.CarryoverLimit} carryover limit\nEffective {policy.EffectiveFrom} until {policy.EffectiveTo}\nPublish: {(PolicyPublishDate.HasValue ? PolicyPublishDate.Value.ToString("d MMM yyyy") : "Immediately")}\nTaken leave and reservations are retained. Entitlement differences apply on publication. Balances renew on 1 January. Qualification rules:\n" + string.Join("\n",PolicyRules.Select(x=>$"{x.Name}: {x.QualificationSummary}")) + "\nRules with qualification history must be retained; set additional days to zero to retire them.")) return;
        await api.SavePolicyAsync(policy, retainedRules, publishDate:PolicyPublishDate.HasValue?DateOnly.FromDateTime(PolicyPublishDate.Value.Date):null); await LoadCountry(++countryGeneration); Feedback = "Vacation policy saved. Entitlement updates apply on publication; taken and reserved leave retained.";
    });
    [RelayCommand] private Task SaveScheduleAsync() => Run(async () =>
    {
        if (EndDayOffset is < 0 or > 1 || WeekendMask is < 0 or > 126) throw new InvalidOperationException("Choose at least one working day and a valid shift end day.");
        if (Country == null || Organization == null || Organization.CountryId != Country.Id || ScheduleFrom == null || ShiftStart == null || ShiftEnd == null || string.IsNullOrWhiteSpace(ScheduleName)) throw new InvalidOperationException("Choose an organization with a country, a name and shift times.");
        var value = Schedule == null ? new WorkScheduleEditor() : JsonSerializer.Deserialize<WorkScheduleEditor>(JsonSerializer.Serialize(Schedule))!;
        value.IsDefault = ScheduleIsDefault; value.CountryId = Country.Id; value.OrganizationId = Organization.Id; value.Name = ScheduleName.Trim(); value.TimeZoneId = TimeZoneId; value.StartTime = TimeOnly.FromTimeSpan(ShiftStart.Value); value.EndTime = TimeOnly.FromTimeSpan(ShiftEnd.Value); value.EndDayOffset = EndDayOffset; value.WeekendMask = WeekendMask; value.EffectiveFrom = DateOnly.FromDateTime(ScheduleFrom.Value.Date);
        if (ConfirmAsync == null || !await ConfirmAsync("Save working schedule", $"{value.Name}\n{Clock.Time(new DateOnly(2000,1,1).ToDateTime(value.StartTime))} – {Clock.Time(new DateOnly(2000,1,1).ToDateTime(value.EndTime))} (+{value.EndDayOffset} day)\n{value.TimeZoneId}\nEffective {value.EffectiveFrom}")) return;
        await api.SaveScheduleAsync(value); await LoadCountry(++countryGeneration); Feedback = "Working schedule saved.";
    });
    [RelayCommand] private Task SaveUnitAsync() => Run(async () =>
    {
        if (Organization == null || string.IsNullOrWhiteSpace(UnitName) || string.IsNullOrWhiteSpace(UnitCode)) throw new InvalidOperationException("Select an organization and enter a unit name and code.");
        if (ParentUnit?.Id == Unit?.Id && Unit != null) throw new InvalidOperationException("A unit cannot be its own parent.");
        if (UnitSchedule != null && !UnitSchedules.Contains(UnitSchedule)) throw new InvalidOperationException("Choose a current schedule belonging to this organization.");
        var value = new UnitOption(Unit?.Id ?? Guid.NewGuid(), Organization.Id, ParentUnit?.Id, UnitType, UnitCode.Trim(), UnitName.Trim(), UnitDescription, true);
        if (ConfirmAsync == null || !await ConfirmAsync("Save business unit", $"{Organization.Name}\n{value.Name} ({value.Code})\nParent: {ParentUnit?.Name ?? "None"}\nWorking schedule: {UnitSchedule?.Name ?? "Default schedule"}")) return;
        await api.SaveUnitAsync(value, UnitSchedule?.Id, revision, Unit == null); await LoadOrganization(++organizationGeneration); Feedback = "Business unit saved.";
    });
    [RelayCommand] private Task UnassignAsync() => Run(async () =>
    {
        var assignment = Assignment ?? throw new InvalidOperationException("Select an employee's current assignment."); var person = Person;
        if (ConfirmAsync == null || !await ConfirmAsync("Unassign position", $"{person?.Name}\n{assignment.PositionTitle}\nThe current assignment ends immediately. Country-based leave requires a position assignment.")) return;
        var current = await api.OrganizationRevisionAsync(); await api.EndAssignmentAsync(assignment.Id, current.Revision); await LoadAssignments(++personGeneration); Feedback = "Position unassigned.";
    });
}
