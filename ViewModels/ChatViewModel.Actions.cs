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
        var old = Messages.FirstOrDefault(x => x.Id == message.Id);
        if (old != null) Messages[Messages.IndexOf(old)] = new ChatMessageItemViewModel(message, old.IsMine, _chatService, _fileService, _imageService);
    });
    private void OnMessageDeleted(string id) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        var old = Messages.FirstOrDefault(x => x.Id == id); if (old == null) return;
        old.Message.IsDeleted = true; old.Message.Attachments?.Clear(); old.Message.Reactions?.Clear();
        Messages[Messages.IndexOf(old)] = new ChatMessageItemViewModel(old.Message, old.IsMine, _chatService, _fileService, _imageService);
    });
    [ObservableProperty, NotifyPropertyChangedFor(nameof(MessageActionLabel)), NotifyPropertyChangedFor(nameof(HasMessageAction))] private ChatMessageItemViewModel? _editingMessage;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(MessageActionLabel)), NotifyPropertyChangedFor(nameof(HasMessageAction))] private ChatMessageItemViewModel? _replyingMessage;
    public bool HasMessageAction => EditingMessage != null || ReplyingMessage != null;
    public string MessageActionLabel => EditingMessage != null ? "Editing message" : ReplyingMessage != null ? "Replying: " + ReplyingMessage.Text : "";
    public void BeginEdit(ChatMessageItemViewModel message) { if (!message.CanEdit) { SendError = "Unlock and verify this message before editing it."; return; } ReplyingMessage = null; EditingMessage = message; MessageDraft = message.Text ?? ""; }
    public void BeginReply(ChatMessageItemViewModel message) { EditingMessage = null; ReplyingMessage = message; }
    public Task OpenConversationProfileAsync() => SelectedRecipient is {} selected
        ? OpenUserProfileAsync(new UserSearchItemViewModel(new UserIdentityDesktop.Core.Models.SystemUserSearch { Id = selected.ReceiverId.ToString(), FirstName = selected.ReceiverName }, _imageService)) : Task.CompletedTask;
    [RelayCommand] private Task OpenMyProfileAsync() => OpenUserProfileAsync(new UserSearchItemViewModel(new UserIdentityDesktop.Core.Models.SystemUserSearch { Id = CurrentUser.Id.ToString(), FirstName = CurrentUser.FirstName, LastName = CurrentUser.LastName }, _imageService));
    [RelayCommand] private void CancelMessageAction() { EditingMessage = null; ReplyingMessage = null; }
    public async Task DeleteMessage(ChatMessageItemViewModel message) { try { if (message.CanModify) await _chatService.DeleteMessageAsync(message.Id); } catch (Exception ex) { SendError = ex.Message; } }
    public async Task React(ChatMessageItemViewModel message, string emoji) { try { await _chatService.ReactAsync(message.Id, emoji); } catch (Exception ex) { SendError = ex.Message; } }
}
