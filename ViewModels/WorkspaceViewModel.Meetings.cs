using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UserIdentityDesktop.Core.Models;
namespace UserIdentityDesktop.ViewModels;
public partial class WorkspaceViewModel
{
    private void OnMeetingChanged(string locator) => Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
    {
        if (disposed || meetingLocator != locator) return;
        try
        {
            var detail = await api.MeetingAsync(locator, lifetime.Token);
            if (detail.State is 2 or 3) await LeaveAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Feedback = ex.Message; }
    });
    [ObservableProperty, NotifyPropertyChangedFor(nameof(MeetingInvitationTitle))] private MeetingDetail? _pendingMeetingInvitation;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(MeetingInvitationConflicts))] private CalendarReview? _meetingInvitationReview;
    public string MeetingInvitationTitle => PendingMeetingInvitation?.Title ?? "";
    public System.Collections.Generic.IReadOnlyList<CalendarConflict> MeetingInvitationConflicts => MeetingInvitationReview is {} review ? review.Conflicts : Array.Empty<CalendarConflict>();
    [ObservableProperty] private bool _acknowledgeMeetingConflicts;
    private MeetingDetail? reviewedMeetingInvitation;
    private static CalendarItem InvitationItem(MeetingDetail detail) => new(detail.CalendarEntryId, "Meeting", detail.Title, null, detail.StartUtc, detail.EndUtc, Guid.Empty, 0, detail.CalendarVersion);
    [RelayCommand] private Task ReviewMeetingInvitationAsync() => Work(async () =>
    {
        var detail = PendingMeetingInvitation ?? throw new InvalidOperationException("Open the meeting invitation first.");
        MeetingInvitationReview = null; AcknowledgeMeetingConflicts = false;
        var review = await api.ReviewResponseAsync(InvitationItem(detail), lifetime.Token);
        if (PendingMeetingInvitation != detail) return;
        reviewedMeetingInvitation = detail; MeetingInvitationReview = review;
    });
    [RelayCommand] private Task AcceptMeetingInvitationAsync() => Work(async () =>
    {
        var detail = PendingMeetingInvitation;
        var review = MeetingInvitationReview;
        if (detail == null || detail != reviewedMeetingInvitation || review == null) throw new InvalidOperationException("Review the current invitation before accepting.");
        if (review.Conflicts.Count > 0 && !AcknowledgeMeetingConflicts) throw new InvalidOperationException("Acknowledge the scheduling conflicts before accepting.");
        await api.RespondAsync(InvitationItem(detail), 1, review.Token, AcknowledgeMeetingConflicts, lifetime.Token);
        PendingMeetingInvitation = null; MeetingInvitationReview = null;
        Feedback = "Invitation accepted. Choose Join meeting when the host has started it."; OnInvalidated();
    });
    partial void OnMeetingCodeChanged(string value) { PendingMeetingInvitation = null; MeetingInvitationReview = null; }
}
