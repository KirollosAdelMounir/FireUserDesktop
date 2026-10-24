using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UserIdentityDesktop.Core;
using UserIdentityDesktop.Core.Models;

namespace UserIdentityDesktop.ViewModels;

public partial class AttendeePickerViewModel : ObservableObject
{
    private readonly ICollaborationApi api;
    private readonly Dictionary<Guid, HashSet<Guid>> unitMembers = new();
    public ObservableCollection<PersonSelection> People { get; }
    public ObservableCollection<MeetingUnitOption> Units { get; } = new();
    public ObservableCollection<MeetingUnitOption> SelectedUnits { get; } = new();
    public ObservableCollection<string> Emails { get; } = new();
    public ObservableCollection<PersonSelection> Results { get; } = new();
    public ObservableCollection<PersonSelection> SelectedPeople { get; } = new();
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private MeetingUnitOption? _unit;
    public bool AllowsMeetingGuests { get; set; } = true;
    public Guid[] UnitIds => SelectedUnits.Select(x => x.Id).ToArray();
    public Guid[] UserIds => People.Where(x => x.Selected).Select(x => x.Id).Distinct().ToArray();
    public AttendeePickerViewModel(ICollaborationApi api, ObservableCollection<PersonSelection> people)
    {
        this.api = api; People = people;
        People.CollectionChanged += (_, _) => Reconcile();
        Reconcile();
    }
    public async Task LoadAsync(CancellationToken ct = default)
    {
        Error = "";
        if (!AllowsMeetingGuests) return;
        try { var units = await api.MeetingUnitsAsync(ct); Units.Clear(); foreach (var item in units ?? new()) Units.Add(item); }
        catch (Exception ex) { Error = ex.Message; }
    }
    partial void OnSearchChanged(string value) => Reconcile();
    public void Reconcile()
    {
        foreach (var person in People) { person.PropertyChanged -= PersonChanged; person.PropertyChanged += PersonChanged; }
        var selected = People.Where(x => x.Selected).OrderBy(x => x.Name).ToArray();
        var results = People.Where(x => !x.Selected && x.Name.Contains(Search.Trim(), StringComparison.CurrentCultureIgnoreCase)).OrderBy(x => x.Name).ToArray();
        Sync(SelectedPeople, selected); Sync(Results, results);
        OnPropertyChanged(nameof(UserIds));
    }
    private static void Sync(ObservableCollection<PersonSelection> target, PersonSelection[] values)
    {
        for (int i = target.Count - 1; i >= 0; i--) if (!values.Contains(target[i])) target.RemoveAt(i);
        for (int i = 0; i < values.Length; i++) { var index = target.IndexOf(values[i]); if (index < 0) target.Insert(i, values[i]); else if (index != i) target.Move(index, i); }
    }
    private void PersonChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PersonSelection.Selected)) return;
        if (sender is PersonSelection { Selected: false } person)
            foreach (var unit in SelectedUnits.Where(x => unitMembers.TryGetValue(x.Id, out var members) && members.Contains(person.Id)).ToArray()) SelectedUnits.Remove(unit);
        Reconcile();
    }
    [RelayCommand] private void Toggle(PersonSelection? person) { if (person != null) person.Selected = !person.Selected; }
    [RelayCommand] private void SelectAll() { foreach (var person in Results.ToArray()) person.Selected = true; }
    [RelayCommand] private void Clear() { foreach (var person in SelectedPeople.ToArray()) person.Selected = false; SelectedUnits.Clear(); }
    [RelayCommand] private async Task AddUnitAsync()
    {
        if (Unit == null || Busy || !AllowsMeetingGuests) return;
        var selected = Unit; Busy = true; Error = "";
        try
        {
            var members = await api.ResolveMeetingUnitsAsync(new[] { selected.Id });
            if (members == null) throw new InvalidOperationException("The business unit could not be resolved.");
            unitMembers[selected.Id] = members.Select(x => x.Id).ToHashSet();
            foreach (var member in members)
            {
                var person = People.FirstOrDefault(x => x.Id == member.Id);
                if (person == null) { person = new PersonSelection(new(member.Id, member.Name)); People.Add(person); }
                person.Selected = true;
            }
            if (!SelectedUnits.Any(x => x.Id == selected.Id)) SelectedUnits.Add(selected);
            Unit = null;
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { Busy = false; }
    }
    [RelayCommand] private void RemoveUnit(MeetingUnitOption? unit)
    {
        if (unit == null) return;
        SelectedUnits.Remove(unit);
        var retained = SelectedUnits.SelectMany(x => unitMembers[x.Id]).ToHashSet();
        if (unitMembers.TryGetValue(unit.Id, out var members)) foreach (var person in People.Where(x => members.Contains(x.Id) && !retained.Contains(x.Id))) person.Selected = false;
    }
    public static bool ValidEmail(string value) => value.Length <= 254 && !value.Any(char.IsWhiteSpace) && System.Text.RegularExpressions.Regex.IsMatch(value, @"^[^\s@]+@[^\s@]+\.[^\s@]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) && MailAddress.TryCreate(value, out var parsed) && parsed.Address == value;
    [RelayCommand] private void AddEmail()
    {
        Error = ""; var email = Email.Trim();
        if (!AllowsMeetingGuests || !ValidEmail(email)) { Error = "Enter a valid email address, such as name@example.com."; return; }
        if (Emails.Count >= 20) { Error = "Send at most 20 email invitations at a time."; return; }
        if (!Emails.Contains(email, StringComparer.OrdinalIgnoreCase)) Emails.Add(email);
        Email = "";
    }
    [RelayCommand] private void RemoveEmail(string? email) { if (email != null) Emails.Remove(email); }
}
