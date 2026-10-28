using System;
using System.Linq;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using UserIdentityDesktop.Data;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using UserIdentityDesktop.Core.Models;
namespace UserIdentityDesktop.Service;
public partial class ChatService
{
    public async Task<ChatMessage?> GetMessageAsync(string messageId, System.Threading.CancellationToken cancellationToken = default)
    {
        using var client=_authService.CreateAuthenticatedClient();
        using var response=await client.GetAsync($"{_configService.ServerUrl}/api/Chat/Message/{Uri.EscapeDataString(messageId)}",cancellationToken);
        var message=await ApiPayload.ReadAsync<ChatMessage>(response,cancellationToken);
        return message == null ? null : await _encryption.DecryptAsync(message);
    }

    private async Task<string> Protect(Guid receiver, string? conversation, string? text, EncryptedFile[]? files = null,System.Threading.CancellationToken cancellationToken = default)
    {
        Guid[]? audience = null;
        if (conversation != null)
        {
            var recipients = await GetRecipientsAsync().WaitAsync(cancellationToken);
            audience = recipients.FirstOrDefault(x => x.ConversationId == conversation)?.ParticipantIds.ToArray() ?? throw new InvalidOperationException("Conversation is unavailable.");
            if (!audience.Contains(_authService.CurrentUser!.Id)) throw new InvalidOperationException("You are no longer a conversation member.");
        }
        return await _encryption.EncryptAsync(receiver, text, files, conversation, audience,cancellationToken);
    }
    public async Task<bool> SendConversationAsync(string conversationId, string text, string? replyTo = null)
    {
        var sender=_authService.CurrentUser?.Id ?? throw new InvalidOperationException("Sign in first.");
        var encrypted = await Protect(Guid.Empty, conversationId, text);
        if(_authService.CurrentUser?.Id!=sender)throw new OperationCanceledException("The signed-in account changed.");
        await QueueHubAction("SendMessageToConversation", new ChatDto { Sender = sender, Receiver = Guid.Empty, ConversationId = conversationId, Text = encrypted, NotificationPreviews=await _encryption.NotificationPreviewsAsync(encrypted,text), ReplyToMessageId = replyTo });
        return true;
    }
    public async Task<List<ChatMessage>> GetConversationHistoryAsync(string id, int page = 0)
    {
        using var client = _authService.CreateAuthenticatedClient();
        using var response = await client.GetAsync($"{_configService.ServerUrl}/api/Chat/ConversationMessages?conversationId={Uri.EscapeDataString(id)}&page={page}");
        var history = await ApiPayload.ReadAsync<List<ChatMessage>>(response);
        foreach (var message in history) await _encryption.DecryptAsync(message);
        return history;
    }
    private sealed record AttachmentRow(Guid Id, string MessageId);
    private sealed record AttachmentPage(List<AttachmentRow> Items);
    public async Task<List<ChatAttachment>> GetAttachmentsAsync(Guid receiverId, string? conversationId, int page)
    {
        using var client = _authService.CreateAuthenticatedClient();
        var scope = conversationId == null ? "receiverId=" + receiverId : "conversationId=" + Uri.EscapeDataString(conversationId);
        using var response = await client.GetAsync($"{_configService.ServerUrl}/api/Chat/Attachments?{scope}&page={page}");
        var result = await ApiPayload.ReadAsync<AttachmentPage>(response);
        var messages = new Dictionary<string, ChatMessage>();
        var files = new List<ChatAttachment>();
        foreach (var row in result.Items)
        {
            if (!messages.TryGetValue(row.MessageId, out var message))
            {
                using var detail = await client.GetAsync($"{_configService.ServerUrl}/api/Chat/Message/{Uri.EscapeDataString(row.MessageId)}");
                message = await _encryption.DecryptAsync(await ApiPayload.ReadAsync<ChatMessage>(detail)); messages[row.MessageId] = message;
            }
            var attachment = message.Attachments?.FirstOrDefault(x => x.Id == row.Id);
            if (attachment != null) files.Add(attachment);
        }
        return files;
    }
    private sealed record CreatedConversation(string Id);
    public async Task<string> AddMembersAsync(RecipientDto recipient, List<Guid> ids)
    {
        using var client = _authService.CreateAuthenticatedClient();
        if (recipient.ConversationId != null)
        {
            using var response = await client.PutAsJsonAsync($"{_configService.ServerUrl}/api/Chat/Conversations/{Uri.EscapeDataString(recipient.ConversationId)}/Members", ids);
            await ApiPayload.ReadAsync<bool>(response); return recipient.ConversationId;
        }
        ids.Add(recipient.ReceiverId); ids.Add(_authService.CurrentUser!.Id);
        using var created = await client.PostAsJsonAsync($"{_configService.ServerUrl}/api/Chat/Conversations", new { name = recipient.ReceiverName, participantIds = ids.Distinct().ToArray() });
        return (await ApiPayload.ReadAsync<CreatedConversation>(created)).Id;
    }
    public async Task<bool> SendReplyAsync(Guid receiverId, string text, string replyTo)
    {
        var encrypted = await _encryption.EncryptAsync(receiverId, text);
        await QueueHubAction("SendMessageToUser", new ChatDto { Sender = _authService.CurrentUser!.Id, Receiver = receiverId, Text = encrypted, NotificationPreviews=await _encryption.NotificationPreviewsAsync(encrypted,text), ReplyToMessageId = replyTo });
        return true;
    }
    public async Task EditMessageAsync(ChatMessage message, string text)
    {

        var files = message.Attachments?.Select(x => x.Encryption ?? throw new InvalidOperationException("Unlock attachments before editing.")).ToArray();
        var encrypted = await Protect(message.Receiver, message.ConversationId, text, files);
        await QueueHubAction("EditMessage", new { messageId = message.Id, text = encrypted });
    }
    public Task MarkConversationSeenAsync(string conversationId) => QueueHubAction("MarkConversationSeen",conversationId);
    public Task DeleteMessageAsync(string id) => QueueHubAction("DeleteMessage",id);
    public Task ReactAsync(string id, string emoji) => QueueHubAction("SetMessageReaction",new{messageId=id,emoji});
}
