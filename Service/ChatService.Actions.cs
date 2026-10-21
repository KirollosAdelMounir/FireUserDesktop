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
    private async Task<string> Protect(Guid receiver, string? conversation, string? text, EncryptedFile[]? files = null)
    {
        Guid[]? audience = null;
        if (conversation != null)
        {
            var recipients = await GetRecipientsAsync();
            audience = recipients.FirstOrDefault(x => x.ConversationId == conversation)?.ParticipantIds.ToArray() ?? throw new InvalidOperationException("Conversation is unavailable.");
            if (!audience.Contains(_authService.CurrentUser!.Id)) throw new InvalidOperationException("You are no longer a conversation member.");
        }
        return await _encryption.EncryptAsync(receiver, text, files, conversation, audience);
    }
    public async Task<bool> SendConversationAsync(string conversationId, string text, string? replyTo = null)
    {
        if (_hubConnection?.State != HubConnectionState.Connected) return false;
        var encrypted = await Protect(Guid.Empty, conversationId, text);
        await _hubConnection.InvokeAsync("SendMessageToConversation", new ChatDto { Sender = _authService.CurrentUser!.Id, Receiver = Guid.Empty, ConversationId = conversationId, Text = encrypted, ReplyToMessageId = replyTo });
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
        if (_hubConnection?.State != HubConnectionState.Connected) return false;
        var encrypted = await _encryption.EncryptAsync(receiverId, text);
        await _hubConnection.InvokeAsync("SendMessageToUser", new ChatDto { Sender = _authService.CurrentUser!.Id, Receiver = receiverId, Text = encrypted, ReplyToMessageId = replyTo });
        return true;
    }
    public async Task EditMessageAsync(ChatMessage message, string text)
    {
        if (_hubConnection?.State != HubConnectionState.Connected) throw new InvalidOperationException("Reconnect before editing.");

        var files = message.Attachments?.Select(x => x.Encryption ?? throw new InvalidOperationException("Unlock attachments before editing.")).ToArray();
        var encrypted = await Protect(message.Receiver, message.ConversationId, text, files);
        await _hubConnection.InvokeAsync("EditMessage", new { messageId = message.Id, text = encrypted });
    }
    public Task MarkConversationSeenAsync(string conversationId) => _hubConnection?.InvokeAsync("MarkConversationSeen", conversationId) ?? Task.CompletedTask;
    public Task DeleteMessageAsync(string id) => _hubConnection?.InvokeAsync("DeleteMessage", id) ?? Task.FromException(new InvalidOperationException("Reconnect first."));
    public Task ReactAsync(string id, string emoji) => _hubConnection?.InvokeAsync("SetMessageReaction", new { messageId = id, emoji }) ?? Task.FromException(new InvalidOperationException("Reconnect first."));
}
