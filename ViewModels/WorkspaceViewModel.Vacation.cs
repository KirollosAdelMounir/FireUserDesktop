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
        // If reporting access disappeared, fetch the fallback calendar once.
        if (selectedId != (SelectedCalendarPerson?.Id ?? auth.CurrentUser!.Id)) OnInvalidated();
    }
    [ObservableProperty] private DateTimeOffset? _vacationStart = DateTimeOffset.Now.Date.AddDays(1);
    [ObservableProperty] private string _vacationDays = "1";
    [ObservableProperty] private string _vacationReason = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(VacationShifts)), NotifyPropertyChangedFor(nameof(VacationBalances))] private VacationPreview? _vacationReview;
    public System.Collections.Generic.IReadOnlyList<VacationShift> VacationShifts => VacationReview is {} review ? review.Shifts : Array.Empty<VacationShift>();
    public System.Collections.Generic.IReadOnlyList<VacationBalance> VacationBalances => VacationReview is {} review ? review.Balances : Array.Empty<VacationBalance>();
    [ObservableProperty] private VacationRequest? _selectedVacation;
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
        VacationReview = null; Feedback = "Vacation request submitted."; await LoadVacations(); await RefreshAsync();
    });
    [RelayCommand] private Task RefreshVacationsAsync() => Work(async () => { vacationPage = 1; await LoadVacations(); });
    [RelayCommand] private Task MoreVacationsAsync() => Work(async () => { if (VacationsHaveMore) { vacationPage++; await LoadVacations(); } });
    private async Task LoadVacations()
    {
        var page = await api.VacationsAsync(vacationPage, lifetime.Token);
        if (vacationPage == 1) Vacations.Clear(); foreach (var item in page.Requests) Vacations.Add(item); VacationsHaveMore = page.HasMore;
    }
    [RelayCommand] private Task DecideVacationAsync(string? action) => Work(async () =>
    {
        if (SelectedVacation == null || action == null) throw new InvalidOperationException("Select a vacation request.");
        await api.DecideVacationAsync(SelectedVacation, action, lifetime.Token); vacationPage = 1; await LoadVacations(); await RefreshAsync();
    });
}
