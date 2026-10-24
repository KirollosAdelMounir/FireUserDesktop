using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UserIdentityDesktop.Core.Models;
namespace UserIdentityDesktop.ViewModels;
public partial class WorkspaceViewModel
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsVacationTab)), NotifyPropertyChangedFor(nameof(IsCalendarTab)), NotifyPropertyChangedFor(nameof(IsOrganizationCalendarTab))] private int _selectedCalendarTab;
    public bool IsVacationTab => SelectedCalendarTab == 1;
    public bool IsCalendarTab => SelectedCalendarTab != 1;
    public bool IsOrganizationCalendarTab => SelectedCalendarTab == 2;
    partial void OnSelectedCalendarTabChanged(int value) { ShowCalendarEditor = false; if (value == 1) RefreshVacationsCommand.Execute(null); else OnInvalidated(); }
    public ObservableCollection<SharedCalendarChoice> SharedCalendars { get; } = new();
    [ObservableProperty] private SharedCalendarChoice? _selectedSharedCalendar;
    partial void OnSelectedSharedCalendarChanged(SharedCalendarChoice? value) { if (IsOrganizationCalendarTab) OnInvalidated(); }
    private void ApplySharedCalendars(SharedCalendarScope? scope)
    {
        var choices = (scope?.Organizations ?? new()).Select(x => new SharedCalendarChoice("Organization", x.Id, x.Name))
            .Concat((scope?.Units ?? new()).Select(x => new SharedCalendarChoice("Unit", x.Id, x.Name))).ToArray();
        if (SharedCalendars.SequenceEqual(choices)) return;
        var old = SelectedSharedCalendar;
        SharedCalendars.Clear(); foreach (var item in choices) SharedCalendars.Add(item);
        SelectedSharedCalendar = SharedCalendars.FirstOrDefault(x => x.Kind == old?.Kind && x.Id == old.Id) ?? SharedCalendars.FirstOrDefault();
    }
    private Task<System.Collections.Generic.List<CalendarItem>> LoadCalendar(DateTime from, DateTime to)
    {
        if (IsOrganizationCalendarTab)
            return SelectedSharedCalendar is {} shared ? api.SharedCalendarAsync(shared.Kind, shared.Id, from, to, lifetime.Token) : Task.FromResult(new System.Collections.Generic.List<CalendarItem>());
        var requested = SelectedCalendarPerson;
        var user = requested != null && CalendarPeople.Any(x => x.Id == requested.Id) ? requested.Id : auth.CurrentUser!.Id;
        return api.CalendarAsync(user, from.ToUniversalTime(), to.ToUniversalTime(), lifetime.Token);
    }
    public ObservableCollection<WorkplacePerson> CalendarPeople { get; } = new();
    private bool applyingCalendarPeople;
    [ObservableProperty] private WorkplacePerson? _selectedCalendarPerson;
    partial void OnSelectedCalendarPersonChanged(WorkplacePerson? oldValue, WorkplacePerson? newValue)
    {
        if (!applyingCalendarPeople && (oldValue?.Id ?? auth.CurrentUser?.Id) != (newValue?.Id ?? auth.CurrentUser?.Id)) OnInvalidated();
    }
    private void ApplyCalendarPeople(System.Collections.Generic.List<WorkplacePerson> people)
    {
        var selectedId = SelectedCalendarPerson?.Id ?? auth.CurrentUser!.Id;
        var available = people.Where(x => x.Id == auth.CurrentUser!.Id || x.ReportingDistance > 0).DistinctBy(x => x.Id).ToArray();
        applyingCalendarPeople = true;
        try
        {
            // Reconcile instead of resetting the bound collection. A reset clears the
            // ComboBox selection; that intermediate null must not invalidate the snapshot.
            for (var i = CalendarPeople.Count - 1; i >= 0; i--)
                if (!available.Any(x => x.Id == CalendarPeople[i].Id)) CalendarPeople.RemoveAt(i);
            for (var i = 0; i < available.Length; i++)
            {
                var existing = CalendarPeople.FirstOrDefault(x => x.Id == available[i].Id);
                if (existing == null) CalendarPeople.Insert(i, available[i]);
                else
                {
                    var index = CalendarPeople.IndexOf(existing);
                    if (index != i) CalendarPeople.Move(index, i);
                    if (existing != available[i]) CalendarPeople[i] = available[i];
                }
            }
            SelectedCalendarPerson = CalendarPeople.FirstOrDefault(x => x.Id == selectedId)
                ?? CalendarPeople.FirstOrDefault(x => x.Id == auth.CurrentUser!.Id);
        }
        finally { applyingCalendarPeople = false; }
        NotifyVacationActions();
        // If reporting access disappeared, fetch the fallback calendar once.
        if (selectedId != (SelectedCalendarPerson?.Id ?? auth.CurrentUser!.Id)) OnInvalidated();
    }
    [ObservableProperty] private DateTimeOffset? _vacationStart = DateTimeOffset.Now.Date.AddDays(1);
    [ObservableProperty] private string _vacationDays = "0";
    [ObservableProperty] private DateTimeOffset? _vacationEnd = DateTimeOffset.Now.Date.AddDays(1);
    [ObservableProperty] private bool _vacationReady;
    [ObservableProperty] private string _vacationInstructions = "Choose dates to check your work schedule and leave policy.";
    public int VacationBalanceYear => VacationStart?.Year ?? DateTime.Now.Year;
    private long vacationDraftVersion;
    private string? reviewedVacationDraft;
    partial void OnVacationEndChanged(DateTimeOffset? value) => InvalidateVacation();
    private void InvalidateVacation()
    {
        VacationReview = null; VacationDays = "0"; reviewedVacationDraft = null;
        OnPropertyChanged(nameof(VacationBalanceYear));
        _ = CalculateVacation(++vacationDraftVersion);
    }
    private async Task CalculateVacation(long version)
    {
        try
        {
            if (VacationStart == null || VacationEnd == null || VacationEnd.Value.Date < VacationStart.Value.Date) { VacationReady = false; VacationInstructions = "Choose an end date on or after the start date."; return; }
            var draft = RangeDraft(); var key = RangeDraftKey();
            var readiness = await api.VacationReadinessAsync(auth.CurrentUser!.Id, draft.Start, lifetime.Token);
            if (version != vacationDraftVersion || disposed) return;
            VacationReady = readiness?.Ready == true; VacationInstructions = readiness?.Instructions ?? "";
            if (!VacationReady) return;
            var review = await api.PreviewVacationRangeAsync(auth.CurrentUser!.Id, draft.Start, draft.End, VacationReason, lifetime.Token);
            if (version != vacationDraftVersion || key != RangeDraftKey() || disposed) return;
            VacationReview = review; reviewedVacationDraft = key;
            VacationDays = (review?.Shifts.Sum(x => x.Days) ?? 0).ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (version == vacationDraftVersion) { VacationReview = null; VacationReady = false; VacationInstructions = ex.Message; } }
    }
    private (DateOnly Start, DateOnly End) RangeDraft()
    {
        if (VacationStart == null || VacationEnd == null || VacationEnd.Value.Date < VacationStart.Value.Date) throw new InvalidOperationException("Choose valid start and end dates.");
        return (DateOnly.FromDateTime(VacationStart.Value.Date), DateOnly.FromDateTime(VacationEnd.Value.Date));
    }
    private string RangeDraftKey() => $"{VacationStart:yyyy-MM-dd}|{VacationEnd:yyyy-MM-dd}|{VacationReason}";

    [ObservableProperty] private string _vacationReason = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(VacationShifts)), NotifyPropertyChangedFor(nameof(VacationBalances)), NotifyPropertyChangedFor(nameof(VacationFormCommand))] private VacationPreview? _vacationReview;
    public System.Windows.Input.ICommand VacationFormCommand => VacationReview == null ? PreviewVacationCommand : SubmitVacationCommand;
    public System.Collections.Generic.IReadOnlyList<VacationShift> VacationShifts => VacationReview is {} review ? review.Shifts : Array.Empty<VacationShift>();
    public System.Collections.Generic.IReadOnlyList<VacationBalance> VacationBalances => VacationReview is {} review ? review.Balances : Array.Empty<VacationBalance>();
    [ObservableProperty] private VacationRequest? _selectedVacation;
    [ObservableProperty] private bool _showVacationEditor;
    private bool ReviewsSelectedVacation => SelectedVacation is {} request && request.UserId != auth.CurrentUser?.Id && CalendarPeople.Any(person => person.Id == request.UserId && person.ReportingDistance == 1);
    public bool CanApproveVacation => SelectedVacation?.State == 0 && ReviewsSelectedVacation;
    public bool CanWithdrawVacation => SelectedVacation is { State: 0 } request && request.UserId == auth.CurrentUser?.Id;
    public bool CanRequestVacationCancellation => SelectedVacation is { State: 1 } request && request.UserId == auth.CurrentUser?.Id;
    public bool CanConfirmVacationCancellation => false;
    partial void OnSelectedVacationChanged(VacationRequest? value) => NotifyVacationActions();
    private void NotifyVacationActions()
    {
        OnPropertyChanged(nameof(CanApproveVacation));OnPropertyChanged(nameof(CanApproveNotificationVacation)); OnPropertyChanged(nameof(CanWithdrawVacation));
        OnPropertyChanged(nameof(CanRequestVacationCancellation)); OnPropertyChanged(nameof(CanConfirmVacationCancellation));
        DecideVacationCommand.NotifyCanExecuteChanged();
    }
    private bool CanDecideVacation(string? action) => action switch
    {
        "Approve" or "Reject" => CanApproveVacation,
        "Withdraw" => CanWithdrawVacation,
        "RequestCancellation" => CanRequestVacationCancellation,
        "ConfirmCancellation" => CanConfirmVacationCancellation,
        _ => false
    };
    [ObservableProperty] private bool _vacationsHaveMore;
    private int vacationPage = 1;
    public ObservableCollection<VacationRequest> Vacations { get; } = new();
    partial void OnVacationStartChanged(DateTimeOffset? value) => InvalidateVacation();

    partial void OnVacationReasonChanged(string value) => InvalidateVacation();
    [RelayCommand] private Task PreviewVacationAsync() => Work(async () =>
    {
        await CalculateVacation(++vacationDraftVersion);
        if (VacationReview is {} review) await Confirm("Review vacation", VacationDetails(review));
    });
    private string VacationDetails(VacationPreview review) => $"{VacationStart:d} – {VacationEnd:d}\n{VacationDays} working days\n{VacationReason}\n" + string.Join("\n", review.Shifts.Select(x => x.Label)) + "\n" + string.Join("\n", review.Balances.Select(x => x.Label));
    [RelayCommand] private Task SubmitVacationAsync() => Work(async () =>
    {
        var review = VacationReview ?? throw new InvalidOperationException("Preview your working shifts and balance first."); var draft = RangeDraft();
        if (!VacationReady || reviewedVacationDraft != RangeDraftKey()) throw new InvalidOperationException("Review the current dates and reason before submitting.");
        if (!await Confirm("Submit vacation request", VacationDetails(review))) return;
        await api.SubmitVacationRangeAsync(auth.CurrentUser!.Id, draft.Start, draft.End, VacationReason, review.PreviewToken, lifetime.Token);
        VacationReview = null; reviewedVacationDraft = null; Feedback = "Vacation request submitted. The reporting policy determines approval."; vacationPage = 1; if (!IsVacationTab) await LoadVacations(); await RefreshAsync();
        SubmissionCompleted?.Invoke(WorkspaceFormKind.Vacation, Feedback);
    });
    [RelayCommand] private Task RefreshVacationsAsync() => Work(async () => { vacationPage = 1; await LoadVacations(); });
    [RelayCommand] private Task MoreVacationsAsync() => Work(async () => { if (VacationsHaveMore) { vacationPage++; await LoadVacations(); } });
    private async Task LoadVacations()
    {
        var page = await api.VacationsAsync(vacationPage, lifetime.Token);
        if (page == null) return;
        var selectedId = SelectedVacation?.Id;
        if (vacationPage == 1) Vacations.Clear(); foreach (var item in page.Requests) { item.RequesterName=await Chat.GetPersonNameAsync(item.UserId) ?? "Employee name unavailable";Vacations.Add(item); } VacationsHaveMore = page.HasMore;
        SelectedVacation = Vacations.FirstOrDefault(item => item.Id == selectedId);
    }
    [RelayCommand(CanExecute = nameof(CanDecideVacation))] private Task DecideVacationAsync(string? action) => Work(async () =>
    {
        if (SelectedVacation == null || action == null || !CanDecideVacation(action)) throw new InvalidOperationException("This action is not available for the selected request.");
        var request = SelectedVacation;
        var requester=await Chat.GetPersonNameAsync(request.UserId) ?? request.RequesterName;
        if (!await Confirm(action == "RequestCancellation" ? "Cancel vacation" : action + " vacation", $"Employee: {requester}\n{request.RequestedDays} working days\n{request.Reason}\nStatus: {request.StateLabel}" + (action == "RequestCancellation" ? "\nCancellation takes effect immediately and notifies your reporting line." : ""))) return;
        await api.DecideVacationAsync(request, action, lifetime.Token); vacationPage = 1; if (!IsVacationTab) await LoadVacations(); await RefreshAsync();
    });
}
