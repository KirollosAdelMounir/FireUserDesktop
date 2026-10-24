using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
namespace UserIdentityDesktop.ViewModels;
public partial class ChatViewModel
{
    private void OnMessageUpdated(UserIdentityDesktop.Core.Models.ChatMessage message) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        ReplaceMessage(Messages, message);
        foreach (var cache in historyCache.Values) ReplaceMessage(cache.Items, message);
    });
    private void ReplaceMessage(System.Collections.Generic.IList<ChatMessageItemViewModel> items, UserIdentityDesktop.Core.Models.ChatMessage message)
    {
        var old = items.FirstOrDefault(x => x.Id == message.Id);
        if (old != null)
        {
            message.PlayedDetails = MessageDetailsViewModel.MergePlayback(message.PlayedDetails,old.Message.PlayedDetails);
            items[items.IndexOf(old)] = new ChatMessageItemViewModel(message, old.IsMine, _chatService, _fileService, _imageService);
        }
    }
    private void OnAttachmentPlayed(UserIdentityDesktop.Core.Models.PlaybackReceipt receipt) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        foreach(var item in Messages.Concat(historyCache.Values.SelectMany(x=>x.Items)).Where(x=>x.Id==receipt.MessageId))
        {
            item.Message.PlayedDetails=MessageDetailsViewModel.MergePlayback(item.Message.PlayedDetails,new[]{new UserIdentityDesktop.Core.Models.PlayedReceipt(receipt.UserId,receipt.AttachmentId,receipt.PlayedAt)});
        }
    });
    internal async Task<string?> GetPersonNameAsync(Guid id)
    {
        if(id==CurrentUser.Id)return string.IsNullOrWhiteSpace(CurrentUser.DisplayName)?"You":CurrentUser.DisplayName;
        var known=Workspace?.People.FirstOrDefault(x=>x.Id==id)?.Name ?? Recipients.FirstOrDefault(x=>!x.IsGroup&&x.ReceiverId==id)?.ReceiverName;
        if(!string.IsNullOrWhiteSpace(known))return known;
        var profile=await _userService.GetUserProfileAsync(id);return string.IsNullOrWhiteSpace(profile?.FullName)?null:profile.FullName;
    }
    internal MessageDetailsViewModel CreateMessageDetails(ChatMessageItemViewModel item) => new(item.Message,_chatService,CurrentUser.Id,async id =>
    {
        if(id==CurrentUser.Id)return "You";
        var known=Recipients.FirstOrDefault(x=>!x.IsGroup && x.ReceiverId==id)?.ReceiverName
            ?? Workspace?.People.FirstOrDefault(x=>x.Id==id)?.Name;
        if(!string.IsNullOrWhiteSpace(known))return known;
        if(id==item.Sender && !string.IsNullOrWhiteSpace(item.Message.SenderName))return item.Message.SenderName;
        var profile=await _userService.GetUserProfileAsync(id);return profile?.FullName;
    });
    private void OnMessageDeleted(string id) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        foreach (var items in historyCache.Values.Select(x => (System.Collections.Generic.IList<ChatMessageItemViewModel>)x.Items).Append(Messages))
        {
            var old = items.FirstOrDefault(x => x.Id == id); if (old == null) continue;
            old.Message.IsDeleted = true; old.Message.Attachments?.Clear(); old.Message.Reactions?.Clear();
            items[items.IndexOf(old)] = new ChatMessageItemViewModel(old.Message, old.IsMine, _chatService, _fileService, _imageService);
        }
    });
    [ObservableProperty, NotifyPropertyChangedFor(nameof(MessageActionLabel)), NotifyPropertyChangedFor(nameof(HasMessageAction))] private ChatMessageItemViewModel? _editingMessage;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(MessageActionLabel)), NotifyPropertyChangedFor(nameof(HasMessageAction))] private ChatMessageItemViewModel? _replyingMessage;
    public bool HasMessageAction => EditingMessage != null || ReplyingMessage != null;
    public string MessageActionLabel => EditingMessage != null ? "Editing message" : ReplyingMessage != null ? "Replying: " + ReplyingMessage.Text : "";
    public void BeginEdit(ChatMessageItemViewModel message) { if (VoiceComposerActive) return; if (!message.CanEdit) { SendError = "Unlock and verify this message before editing it."; return; } ReplyingMessage = null; EditingMessage = message; MessageDraft = message.Text ?? ""; }
    public void BeginReply(ChatMessageItemViewModel message) { EditingMessage = null; ReplyingMessage = message; }
    public Task OpenConversationProfileAsync() => SelectedRecipient is {} selected
        ? OpenUserProfileAsync(new UserSearchItemViewModel(new UserIdentityDesktop.Core.Models.SystemUserSearch { Id = selected.ReceiverId.ToString(), FirstName = selected.ReceiverName }, _imageService)) : Task.CompletedTask;
    [RelayCommand] private Task OpenMyProfileAsync() => OpenUserProfileAsync(new UserSearchItemViewModel(new UserIdentityDesktop.Core.Models.SystemUserSearch { Id = CurrentUser.Id.ToString(), FirstName = CurrentUser.FirstName, LastName = CurrentUser.LastName }, _imageService));
    [RelayCommand] private void CancelMessageAction() { EditingMessage = null; ReplyingMessage = null; }
    public async Task DeleteMessage(ChatMessageItemViewModel message) { try { if (message.CanModify) await _chatService.DeleteMessageAsync(message.Id); } catch (Exception ex) { SendError = ex.Message; } }
    public async Task React(ChatMessageItemViewModel message, string emoji) { try { await _chatService.ReactAsync(message.Id, emoji); } catch (Exception ex) { SendError = ex.Message; } }
}
