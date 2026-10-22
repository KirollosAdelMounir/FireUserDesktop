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
        if (disposed) return;
        if (RecentNotifications.Any(x => x.Target == item.Target && x.CreatedUtc == item.CreatedUtc)) return;
        RecentNotifications.Insert(0, item);
        while (RecentNotifications.Count > 200) RecentNotifications.RemoveAt(RecentNotifications.Count - 1);
    }
    private void OnWorkplaceNotification(Guid id) => Dispatcher.UIThread.Post(async () =>
    {
        try
        {
            var item = await api.NotificationAsync(id, lifetime.Token);
            if (disposed || !deliveredNotifications.Add(id)) return;
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
                var item = await api.NotificationAsync(notificationId, lifetime.Token);
                await api.ReadNotificationAsync(notificationId, lifetime.Token);
                await RefreshAsync();
                switch (item.ResourceType)
                {
                    case "Announcement": SelectedWorkspaceTab = 3; SelectedAnnouncement = Announcements.FirstOrDefault(x => x.Id == item.ResourceId); break;
                    case "Balance":
                    case "Vacation": SelectedWorkspaceTab = 2; SelectedCalendarTab = 1; await LoadVacations(); SelectedVacation = Vacations.FirstOrDefault(x => x.Id == item.ResourceId); break;
                    case "Calendar": SelectedWorkspaceTab = 2; SelectedCalendarTab = 0; SelectedCalendar = Calendar.FirstOrDefault(x => x.Id == item.ResourceId); if (SelectedCalendar != null) RangeDate = SelectedCalendar.StartUtc.ToLocalTime(); break;
                    default: SelectedWorkspaceTab = 5; break;
                }
                NotificationDetailTitle = item.Title; NotificationDetailBody = item.Body;
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Feedback = ex.Message; }
    }
    public bool HasNotificationDetails => !string.IsNullOrWhiteSpace(NotificationDetailTitle) || !string.IsNullOrWhiteSpace(NotificationDetailBody);
    [RelayCommand] private void DismissNotificationDetails() { NotificationDetailTitle = ""; NotificationDetailBody = ""; }
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty, CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(HasNotificationDetails))] private string _notificationDetailTitle = "";
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty, CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(HasNotificationDetails))] private string _notificationDetailBody = "";
}
