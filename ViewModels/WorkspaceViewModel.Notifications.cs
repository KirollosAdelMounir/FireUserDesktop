using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using UserIdentityDesktop.Core;
using UserIdentityDesktop.Core.Models;
using Avalonia.Threading;

namespace UserIdentityDesktop.ViewModels;

public partial class WorkspaceViewModel
{
    private INotificationService? notifications;
    public ObservableCollection<DesktopNotification> RecentNotifications { get; } = new();
    public bool HasRecentNotifications => RecentNotifications.Count > 0;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _hasEarlierNotifications;
    private readonly System.Collections.Generic.HashSet<Guid> deliveredNotifications = new();
    private bool notificationsInitialized;
    private string? notificationTicks;
    private Guid? notificationBefore;
    private void InitializeNotifications(INotificationService? service)
    {
        notifications = service;
        RecentNotifications.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRecentNotifications));
        if (service != null) service.NotificationPosted += OnNotificationPosted;
        chatService.WorkplaceNotificationChanged += OnWorkplaceNotification;
        chatService.WorkplaceNotificationsRead += OnNotificationsRead;
    }
    private void OnNotificationPosted(DesktopNotification item)
    {
        if (disposed || IsMessageNotification(item.Target)) return;
        if (RecentNotifications.Any(x => x.Target == item.Target && x.CreatedUtc == item.CreatedUtc)) return;
        RecentNotifications.Insert(0, item);
        while (RecentNotifications.Count > 200) RecentNotifications.RemoveAt(RecentNotifications.Count - 1);
    }
    internal static bool IsMessageNotification(string target) => Uri.TryCreate(target,UriKind.Absolute,out var link) && link.Host is "chat" or "conversation" or "message";
    private void OnWorkplaceNotification(Guid id) => Dispatcher.UIThread.Post(async () =>
    {
        try
        {
            var item = await api.NotificationAsync(id, lifetime.Token);
            if (disposed || item.ResourceType is "Chat" or "Conversation" or "Message" || !deliveredNotifications.Add(id)) return;
            notifications?.ShowNotification(item.Title, item.Body, $"fireuserconnect://notification/{id}");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Feedback = ex.Message; }
    });
    private void OnNotificationsRead() => Dispatcher.UIThread.Post(async () =>
    {
        try { await LoadNotificationSnapshot(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Feedback = ex.Message; }
    });
    private async Task<bool> LoadNotificationSnapshot(bool more = false)
    {
        var page = await api.NotificationsAsync(more ? notificationTicks : null, more ? notificationBefore : null, lifetime.Token);
        if (disposed || page == null) return false;
        notificationTicks = page.HasMore ? page.BeforeTicks : null; notificationBefore = page.BeforeId;
        HasEarlierNotifications = page.HasMore;
        foreach (var item in page.Items)
        {
            if(item.ResourceType is "Chat" or "Conversation" or "Message")continue;
            var firstDelivery = deliveredNotifications.Add(item.Id);
            if (firstDelivery && notificationsInitialized && !more && item.ReadUtc == null)
                notifications?.ShowNotification(item.Title, item.Body, $"fireuserconnect://notification/{item.Id}");
            var target = $"fireuserconnect://notification/{item.Id}";
            if (!RecentNotifications.Any(x => x.Target == target)) RecentNotifications.Add(new(item.Title, item.Body, target, item.CreatedUtc));
        }
        var ordered = RecentNotifications.OrderByDescending(x => x.CreatedUtc).Take(200).ToArray();
        RecentNotifications.Clear(); foreach (var item in ordered) RecentNotifications.Add(item);
        notificationsInitialized = true;
        return true;
    }
    [RelayCommand] private Task MoreNotificationsAsync() => Work(async () => { if (notificationTicks != null) await LoadNotificationSnapshot(true); });
    [RelayCommand] private async Task OpenNotificationAsync(DesktopNotification? item)
    {
        if (item == null) return;
        if (item.Target.StartsWith("fireuserconnect://chat/", StringComparison.Ordinal))
        {
            if (Guid.TryParse(item.Target.Split('/')[^1], out var peer))
            {
                SelectedWorkspaceTab = 0;
                var recipient = Chat.Recipients.FirstOrDefault(x => !x.IsGroup && x.ReceiverId == peer);
                Chat.StartChatWithUser(peer, recipient?.ReceiverName ?? "Conversation", recipient?.Avatar);
            }
        }
        else await OpenNotificationTargetAsync(item.Target);
    }
    [RelayCommand] private Task ApproveNoticeAsync(DesktopNotification? item) => item == null ? Task.CompletedTask : OpenNotificationTargetAsync(item.Target + "?action=Approve");
    [RelayCommand] private Task RejectNoticeAsync(DesktopNotification? item) => item == null ? Task.CompletedTask : OpenNotificationTargetAsync(item.Target + "?action=Reject");
    public async Task OpenNotificationTargetAsync(string target)
    {
        try
        {
            if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme != "fireuserconnect") return;
            var id = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
            if (uri.Host == "conversation")
            {
                await Chat.LoadRecipientsAsync();
                // LoadRecipients dispatches reconciliation to the UI queue.
                await Dispatcher.UIThread.InvokeAsync(() => { SelectedWorkspaceTab = 0; Chat.SelectedRecipient = Chat.Recipients.FirstOrDefault(x => x.ConversationId == id); });
            }
            else if (uri.Host == "call")
            {
                SelectedWorkspaceTab = 1;
                Feedback = Incoming?.CallId == id ? "Answer or decline the incoming call." : "This call invitation may have ended. You can rejoin from the conversation.";
            }
            else if (uri.Host == "notification" && Guid.TryParse(id, out var notificationId))
            {
                NotificationDetailResourceType="";
                var item = await api.NotificationAsync(notificationId, lifetime.Token);
                await api.ReadNotificationAsync(notificationId, lifetime.Token);
                await RefreshAsync();
                SelectedVacation = null; SelectedCalendar = null;
                switch (item.ResourceType)
                {
                    case "Announcement": SelectedWorkspaceTab = 3; SelectedAnnouncement = Announcements.FirstOrDefault(x => x.Id == item.ResourceId); break;
                    case "Balance":
                    case "Vacation":
                        SelectedWorkspaceTab = 2; SelectedCalendarTab = 1;
                        var requests = await api.VacationItemAsync(item.ResourceId,lifetime.Token);
                        SelectedVacation = requests?.Requests.FirstOrDefault(x => x.Id == item.ResourceId); break;
                    case "Calendar":
                        SelectedWorkspaceTab = 2; SelectedCalendarTab = 0;
                        var startLine = item.Body.Split('\n').FirstOrDefault(x => x.StartsWith("Starts: ",StringComparison.Ordinal));
                        if (startLine != null && DateTime.TryParseExact(startLine.Trim()[8..], "yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,out var starts))
                        {
                            RangeDate = starts.ToLocalTime(); await RefreshAsync();
                        }
                        SelectedCalendar = Calendar.FirstOrDefault(x => x.Id == item.ResourceId); break;
                    default: SelectedWorkspaceTab = 5; break;
                }
                NotificationDetailResourceType=item.ResourceType;
                NotificationDetailTitle = item.Title;
                if(item.ResourceType is "Vacation" or "Balance" && SelectedVacation is {} vacation)
                {
                    vacation.RequesterName=await Chat.GetPersonNameAsync(vacation.UserId) ?? vacation.RequesterName;
                    NotificationDetailBody=$"Requested by: {vacation.RequesterName}\n"+item.Body.Replace(vacation.UserId.ToString(),vacation.RequesterName,StringComparison.OrdinalIgnoreCase);
                }
                else NotificationDetailBody = item.Body;
                if (uri.Query is "?action=Approve" or "?action=Reject")
                {
                    var approve = uri.Query == "?action=Approve";
                    if (item.ResourceType == "Vacation" && CanApproveVacation) await DecideVacationCommand.ExecuteAsync(approve ? "Approve" : "Reject");
                    else if (item.ResourceType == "Calendar" && SelectedCalendar is { Response: 0 } entry && entry.OrganizerId != auth.CurrentUser?.Id)
                    {
                        var review = approve ? await api.ReviewResponseAsync(entry,lifetime.Token) : null;
                        var details = item.Body + (review == null ? "" : "\nScheduling conflicts:\n" + string.Join("\n",review.Conflicts.Select(x=>$"{x.Title}: {WorkClock.DateTime(x.StartUtc.ToLocalTime())} – {WorkClock.DateTime(x.EndUtc.ToLocalTime())}")));
                        if (await Confirm(approve ? "Accept invitation" : "Decline invitation",details))
                        {
                            await api.RespondAsync(entry,approve ? 1 : 2,review?.Token,review?.Conflicts.Count > 0,lifetime.Token); await RefreshAsync();
                        }
                    }
                    else Feedback = "This approval is no longer available, or you do not have permission to act on it.";
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Feedback = ex.Message; }
    }
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty, CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(CanApproveNotificationVacation))] private string _notificationDetailResourceType="";
    public bool CanApproveNotificationVacation => NotificationDetailResourceType is "Vacation" or "Balance" && CanApproveVacation;
    public bool HasNotificationDetails => !string.IsNullOrWhiteSpace(NotificationDetailTitle) || !string.IsNullOrWhiteSpace(NotificationDetailBody);
    [RelayCommand] private void DismissNotificationDetails() { NotificationDetailTitle = ""; NotificationDetailBody = "";NotificationDetailResourceType=""; }
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty, CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(HasNotificationDetails))] private string _notificationDetailTitle = "";
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty, CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(HasNotificationDetails))] private string _notificationDetailBody = "";
}
