using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
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
    [ObservableProperty] private string _vacationDays = "1";
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
    public bool CanConfirmVacationCancellation => SelectedVacation?.State == 4 && ReviewsSelectedVacation;
    partial void OnSelectedVacationChanged(VacationRequest? value) => NotifyVacationActions();
    private void NotifyVacationActions()
    {
        OnPropertyChanged(nameof(CanApproveVacation)); OnPropertyChanged(nameof(CanWithdrawVacation));
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
    partial void OnVacationStartChanged(DateTimeOffset? value) => VacationReview = null;
    partial void OnVacationDaysChanged(string value) => VacationReview = null;
    partial void OnVacationReasonChanged(string value) => VacationReview = null;
    private (DateOnly Start, int Days) VacationDraft()
    {
        if (VacationStart == null || !int.TryParse(VacationDays, out var days) || days < 1) throw new InvalidOperationException("Choose a start date and a positive number of working days.");
        return (DateOnly.FromDateTime(VacationStart.Value.Date), days);
    }
    [RelayCommand] private Task PreviewVacationAsync() => Work(async () =>
    {
        VacationReview = null; var draft = VacationDraft();
        VacationReview = await api.PreviewVacationAsync(auth.CurrentUser!.Id, draft.Start, draft.Days, VacationReason, lifetime.Token);
    });
    [RelayCommand] private Task SubmitVacationAsync() => Work(async () =>
    {
        var review = VacationReview ?? throw new InvalidOperationException("Preview your working shifts and balance first."); var draft = VacationDraft();
        await api.SubmitVacationAsync(auth.CurrentUser!.Id, draft.Start, draft.Days, VacationReason, review.PreviewToken, lifetime.Token);
        VacationReview = null; Feedback = "Vacation request submitted. Pending manager approval."; vacationPage = 1; if (!IsVacationTab) await LoadVacations(); await RefreshAsync();
        SubmissionCompleted?.Invoke(WorkspaceFormKind.Vacation, "Vacation request submitted. Pending manager approval.");
    });
    [RelayCommand] private Task RefreshVacationsAsync() => Work(async () => { vacationPage = 1; await LoadVacations(); });
    [RelayCommand] private Task MoreVacationsAsync() => Work(async () => { if (VacationsHaveMore) { vacationPage++; await LoadVacations(); } });
    private async Task LoadVacations()
    {
        var page = await api.VacationsAsync(vacationPage, lifetime.Token);
        if (page == null) return;
        var selectedId = SelectedVacation?.Id;
        if (vacationPage == 1) Vacations.Clear(); foreach (var item in page.Requests) Vacations.Add(item); VacationsHaveMore = page.HasMore;
        SelectedVacation = Vacations.FirstOrDefault(item => item.Id == selectedId);
    }
    [RelayCommand(CanExecute = nameof(CanDecideVacation))] private Task DecideVacationAsync(string? action) => Work(async () =>
    {
        if (SelectedVacation == null || action == null || !CanDecideVacation(action)) throw new InvalidOperationException("This action is not available for the selected request.");
        await api.DecideVacationAsync(SelectedVacation, action, lifetime.Token); vacationPage = 1; if (!IsVacationTab) await LoadVacations(); await RefreshAsync();
    });
}
