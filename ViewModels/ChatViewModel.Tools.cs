using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UserIdentityDesktop.Core.Models;
namespace UserIdentityDesktop.ViewModels;
public partial class ChatViewModel
{
    [ObservableProperty] private bool _showConversationDetails;
    [ObservableProperty] private int _conversationDetailsTab;
    public void OpenConversationDetails(int section)
    {
        ConversationDetailsTab = section; ShowConversationDetails = true;
        if (section == 0) LoadAttachmentsCommand.Execute(null);
        if (section == 2) LoadSecurityCommand.Execute(null);
    }
    [RelayCommand] private void CloseConversationDetails() => ShowConversationDetails = false;
    public ObservableCollection<ChatAttachmentItemViewModel> AllAttachments { get; } = new();
    [ObservableProperty] private string _newMemberId = "";
    [ObservableProperty] private PersonSelection? _selectedNewMember;
    partial void OnSelectedNewMemberChanged(PersonSelection? value) => NewMemberId = value?.Id.ToString() ?? "";
    private int attachmentPage = 1;
    [ObservableProperty] private string _securityPin = "";
    [ObservableProperty] private string _securityConfirmation = "";
    [ObservableProperty] private string _securityPassword = "";
    [ObservableProperty] private string _securityOtp = "";
    [ObservableProperty] private string _securityStatus = "";
    [RelayCommand] private async Task LoadSecurityAsync()
    {
        try { var status = await _chatService.ChatSecurityAsync(); SecurityStatus = status.PinEnabled ? (status.DeviceTrusted ? "PIN enabled · this device is unlocked" : "PIN enabled · unlock this device") : "PIN disabled · chat encryption remains enabled"; }
        catch (Exception ex) { SecurityStatus = ex.Message; }
    }
    [RelayCommand] private async Task ChangeSecurityAsync(string? action)
    {
        if (action == null) return;
        try
        {
            await _chatService.ChangeChatPinAsync(action, SecurityPin, SecurityConfirmation, SecurityPassword, SecurityOtp);
            SecurityStatus = action == "Recovery" ? "Recovery code sent to your registered email." : "Chat security updated.";
        }
        catch (Exception ex) { SecurityStatus = ex.Message; }
        finally { SecurityPin = SecurityConfirmation = SecurityPassword = SecurityOtp = ""; }
    }
    [RelayCommand] private async Task LoadAttachmentsAsync()
    {
        attachmentPage = 1; AllAttachments.Clear(); await LoadAttachmentPage();
    }
    [RelayCommand] private async Task MoreAttachmentsAsync() { attachmentPage++; await LoadAttachmentPage(); }
    private async Task LoadAttachmentPage()
    {
        var selected = SelectedRecipient; if (selected == null) return;
        try
        {
            var items = await _chatService.GetAttachmentsAsync(selected.ReceiverId, selected.ConversationId, attachmentPage);
            if (SelectedRecipient != selected) return;
            foreach (var item in items) AllAttachments.Add(new ChatAttachmentItemViewModel(item, _chatService, _fileService, _imageService));
        }
        catch (Exception ex) { SendError = ex.Message; }
    }
    [RelayCommand] private async Task AddMemberAsync()
    {
        if (SelectedRecipient == null) return;
        try
        {
            if (!Guid.TryParse(NewMemberId.Trim(), out var id)) throw new InvalidOperationException("Select a colleague from the list.");
            var selected = SelectedRecipient;
            var conversation = await _chatService.AddMembersAsync(new RecipientDto { ReceiverId = selected.ReceiverId, ReceiverName = selected.ReceiverName, ConversationId = selected.ConversationId }, new() { id });
            var recipients = await _chatService.GetRecipientsAsync(); OnRecipientsUpdated(recipients);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => SelectedRecipient = Recipients.FirstOrDefault(x => x.ConversationId == conversation));
            NewMemberId = "";
        }
        catch (Exception ex) { SendError = ex.Message; }
    }
}
