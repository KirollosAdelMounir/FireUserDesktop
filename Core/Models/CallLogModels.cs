using System;
using System.Collections.Generic;
namespace UserIdentityDesktop.Core.Models;
public sealed record CallLogItem(string Id,string CallId,string Name,Guid PeerId,string? ConversationId,string CallType,string Status,bool Outgoing,DateTime StartedAt,DateTime? EndedAt,int? DurationSeconds)
{
    public string Detail => $"{(Outgoing ? "Outgoing" : Status == "missed" ? "Missed" : "Incoming")} · {CallType}" + (DurationSeconds is {} seconds ? $" · {seconds}s" : "");
    public string When => StartedAt.ToLocalTime().ToString("g");
    public bool IsMissed => !Outgoing && Status == "missed";
}
public sealed record CallLogPage(List<CallLogItem> Items,string? NextCursor,int MissedCount,DateTime ObservedAt);
