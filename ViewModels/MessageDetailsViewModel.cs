using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using UserIdentityDesktop.Core;
using UserIdentityDesktop.Core.Models;

namespace UserIdentityDesktop.ViewModels;
public sealed record MessageReceiptRow(string Name, string Time);
public sealed record MessagePlaybackSection(string Title, IReadOnlyList<MessageReceiptRow> Rows)
{
    public bool IsEmpty => Rows.Count==0;
}
/// <summary>Server receipt times only. Open details follow SignalR and local clock preferences, never poll.</summary>
public sealed class MessageDetailsViewModel : ObservableObject, IDisposable
{
    private ChatMessage message;
    private readonly IChatService chat;
    private readonly Guid currentUser;
    private readonly Func<Guid,Task<string?>> resolveName;
    private readonly Dictionary<Guid,string> names=new();
    private readonly HashSet<Guid> requested=new();
    private readonly SemaphoreSlim nameLoads=new(4);
    private bool disposed;
    private readonly CancellationTokenSource lifetime=new();
    private int loadVersion;
    private string? refreshError;
    public string? RefreshError {get=>refreshError;private set=>SetProperty(ref refreshError,value);}
    public Task RefreshTask {get;private set;}=Task.CompletedTask;
    public ObservableCollection<MessageReceiptRow> SeenRows {get;}=new();
    public ObservableCollection<MessagePlaybackSection> Playback {get;}=new();
    public string SentAt => Format(message.TimeSent);
    public string Sender => Name(message.Sender);
    public string Body => message.IsDeleted==true ? "Message deleted" : !string.IsNullOrWhiteSpace(message.Text) ? message.Text : message.Attachments?.Count>0 ? $"{message.Attachments.Count} attachment{(message.Attachments.Count==1?"":"s")}" : "Message";
    public bool ShowReceipts => !message.SuppressReadReceipts;
    public bool NoReaders => SeenRows.Count==0;
    public Task NamesLoaded {get;private set;}=Task.CompletedTask;
    public MessageDetailsViewModel(ChatMessage message,IChatService chat,Guid currentUser,Func<Guid,Task<string?>> resolveName)
    {
        this.message=message;this.chat=chat;this.currentUser=currentUser;this.resolveName=resolveName;
        chat.MessageUpdated+=Updated;chat.AttachmentPlayed+=Played;chat.ReadReceiptReceived+=Seen;
        ClockDisplaySettings.Shared.PropertyChanged+=ClockChanged;Refresh();RefreshTask=LoadLatestAsync();
    }
    private async Task LoadLatestAsync()
    {
        if(disposed)return;var version=++loadVersion;
        try
        {
            var latest=await chat.GetMessageAsync(message.Id,lifetime.Token);
            if(disposed||version!=loadVersion||latest==null||latest.Id!=message.Id)return;
            latest.PlayedDetails=MergePlayback(latest.PlayedDetails,message.PlayedDetails);message=latest;RefreshError=null;Refresh();
        }
        catch(OperationCanceledException)when(lifetime.IsCancellationRequested) { }
        catch(Exception) {if(!disposed&&version==loadVersion)RefreshError="Unable to refresh details. Showing available receipts.";}
    }
    private string Name(Guid user) => user==currentUser ? "You" : names.GetValueOrDefault(user) ?? (user==message.Sender && !string.IsNullOrWhiteSpace(message.SenderName) ? message.SenderName : $"Participant · {user:D}");
    private static string Format(DateTime value)
    {
        if(value.Kind==DateTimeKind.Unspecified)value=DateTime.SpecifyKind(value,DateTimeKind.Utc);
        var local=value.ToLocalTime();return local.ToString("dd MMM yyyy",CultureInfo.CurrentCulture)+" · "+local.ToString(ClockDisplaySettings.Shared.TwelveHour?"h:mm:ss tt":"HH:mm:ss",CultureInfo.CurrentCulture);
    }
    private void Refresh()
    {
        if(disposed)return;
        SeenRows.Clear();Playback.Clear();var ids=new HashSet<Guid>{message.Sender};
        if(ShowReceipts)
        {
            var seen=(message.SeenDetails??new()).GroupBy(x=>x.UserId).Select(g=>g.OrderBy(x=>x.SeenAt).First()).Where(x=>x.UserId!=message.Sender).ToArray();
            if(seen.Length>0) foreach(var receipt in seen.OrderBy(x=>x.SeenAt)){ids.Add(receipt.UserId);SeenRows.Add(new(Name(receipt.UserId),Format(receipt.SeenAt)));}
            else
            {
                var readers=(message.SeenBy??new()).Where(x=>x!=message.Sender).Distinct().ToList();
                if(readers.Count==0 && message.Seen && message.ConversationId==null)readers.Add(message.Sender==currentUser?message.Receiver:currentUser);
                foreach(var reader in readers.Where(x=>x!=Guid.Empty&&x!=message.Sender)){ids.Add(reader);SeenRows.Add(new(Name(reader),message.TimeSeen is {} at?Format(at):"Seen · time unavailable"));}
            }
            foreach(var audio in (message.Attachments??new()).Where(x=>x.IsAudio))
            {
                var rows=MergePlayback(message.PlayedDetails,null).Where(x=>x.AttachmentId==audio.Id && x.UserId!=message.Sender).OrderBy(x=>x.PlayedAt).Select(x=>{ids.Add(x.UserId);return new MessageReceiptRow(Name(x.UserId),Format(x.PlayedAt));}).ToArray();
                var label=audio.FileName.StartsWith("voice-note-",StringComparison.OrdinalIgnoreCase)?"Voice note":audio.FileName;
                Playback.Add(new("Played by"+(message.Attachments!.Count>1?" · "+label:""),rows));
            }
        }
        OnPropertyChanged(nameof(SentAt));OnPropertyChanged(nameof(Sender));OnPropertyChanged(nameof(Body));OnPropertyChanged(nameof(ShowReceipts));OnPropertyChanged(nameof(NoReaders));
        var missing=ids.Where(x=>x!=Guid.Empty&&x!=currentUser&&!requested.Contains(x)).ToArray();
        if(missing.Length>0) {foreach(var id in missing)requested.Add(id);NamesLoaded=Task.WhenAll(missing.Select(LoadName));}
    }
    private async Task LoadName(Guid id)
    {
        await nameLoads.WaitAsync();
        try { if(disposed)return;var name=await resolveName(id).WaitAsync(lifetime.Token);if(!disposed&&!string.IsNullOrWhiteSpace(name)){names[id]=name;Refresh();} }
        catch(Exception) { /* Preserve a distinct participant identifier if profile lookup is unavailable. */ }
        finally {nameLoads.Release();}
    }
    internal static List<PlayedReceipt> MergePlayback(IEnumerable<PlayedReceipt>? first,IEnumerable<PlayedReceipt>? second) => (first??Array.Empty<PlayedReceipt>()).Concat(second??Array.Empty<PlayedReceipt>()).GroupBy(x=>(x.UserId,x.AttachmentId)).Select(g=>g.OrderBy(x=>x.PlayedAt).First()).ToList();
    private void Updated(ChatMessage update)=>Dispatcher.UIThread.Post(()=>{if(disposed||update.Id!=message.Id)return;loadVersion++;RefreshError=null;update.PlayedDetails=MergePlayback(update.PlayedDetails,message.PlayedDetails);message=update;Refresh();});
    private void Played(PlaybackReceipt receipt)=>Dispatcher.UIThread.Post(()=>{if(disposed||receipt.MessageId!=message.Id)return;message.PlayedDetails=MergePlayback(message.PlayedDetails,new[]{new PlayedReceipt(receipt.UserId,receipt.AttachmentId,receipt.PlayedAt)});Refresh();});
    private void Seen(Guid reader,Guid sender,DateTime through)=>Dispatcher.UIThread.Post(()=>
    {
        if(disposed||message.ConversationId!=null||message.Sender!=sender||message.Receiver!=reader||message.TimeSent>through)return;
        message.Seen=true;message.SeenBy=(message.SeenBy??new()).Append(reader).Distinct().ToList();Refresh();RefreshTask=LoadLatestAsync();
    });
    private void ClockChanged(object? sender,PropertyChangedEventArgs args)=>Dispatcher.UIThread.Post(()=>{if(!disposed)Refresh();});
    public void Dispose(){if(disposed)return;disposed=true;lifetime.Cancel();lifetime.Dispose();chat.MessageUpdated-=Updated;chat.AttachmentPlayed-=Played;chat.ReadReceiptReceived-=Seen;ClockDisplaySettings.Shared.PropertyChanged-=ClockChanged;}
}
