using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.Service.Media;

namespace UserIdentityDesktop.ViewModels;
public partial class ChatViewModel
{
    private readonly Dictionary<(Guid User, string Source, string Kind), (ChatActivity Activity, DateTime Expires)> activities = new();
    private readonly DispatcherTimer activityExpiry = new() { Interval = TimeSpan.FromSeconds(1) };
    private VoiceNoteRecorder? recorder, preparingRecorder;
    private IDisposable? recordingActivity;
    private readonly List<VoiceNoteRecorder> recordedNotes = new();
    private int voiceSession;
    private readonly Stopwatch voiceClock = new();
    private readonly DispatcherTimer voiceTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    [ObservableProperty, NotifyPropertyChangedFor(nameof(VoiceRecordingLabel)), NotifyPropertyChangedFor(nameof(VoiceComposerActive)), NotifyPropertyChangedFor(nameof(CanTypeMessage)), NotifyPropertyChangedFor(nameof(VoiceStatusLabel))] private bool _isRecordingVoice;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(VoiceComposerActive)), NotifyPropertyChangedFor(nameof(CanTypeMessage)), NotifyPropertyChangedFor(nameof(VoiceStatusLabel))] private bool _isPreparingVoice;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(VoiceComposerActive)), NotifyPropertyChangedFor(nameof(CanTypeMessage)), NotifyPropertyChangedFor(nameof(VoiceStatusLabel))] private bool _isOpeningMicrophone;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasVoiceNote)), NotifyPropertyChangedFor(nameof(VoiceComposerActive)), NotifyPropertyChangedFor(nameof(CanTypeMessage))] private string? _voiceNotePath;
    public bool ShowSendAction => !string.IsNullOrWhiteSpace(MessageDraft) || PendingAttachments.Count>0 || EditingMessage!=null;
    public bool ShowRecordAction => !ShowSendAction;
    public bool HasVoiceNote => VoiceNotePath != null;
    public bool VoiceComposerActive => IsRecordingVoice || IsPreparingVoice || IsOpeningMicrophone || HasVoiceNote;
    public bool CanTypeMessage => !VoiceComposerActive;
    public string VoiceStatusLabel => IsOpeningMicrophone ? "Opening microphone…" : IsPreparingVoice ? "Preparing audio…" : $"Recording {(int)voiceClock.Elapsed.TotalMinutes}:{voiceClock.Elapsed.Seconds:00}";
    internal Func<string, Task<VoiceNoteRecorder>> CreateVoiceRecorderAsync { get; set; } = microphone => Task.Run(() => new VoiceNoteRecorder(microphone));
    [ObservableProperty] private bool _isUploadingAttachments;
    private static string UploadActivityKind(System.Collections.Generic.IReadOnlyList<string> files)
    {
        if (files.Count == 1 && System.IO.Path.GetFileName(files[0]).StartsWith("voice-note-", StringComparison.Ordinal)) return "voice";
        var types = files.Select(x => System.IO.Path.GetExtension(x).ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".svg" => "photo",
            ".mp4" or ".webm" or ".mov" or ".mkv" => "video",
            ".mp3" or ".wav" or ".ogg" or ".m4a" or ".flac" => "audio",
            _ => "file"
        }).Distinct().ToArray();
        return types.Length == 1 ? types[0] : "file";
    }
    public string VoiceRecordingLabel => IsRecordingVoice ? "Stop recording" : "Record voice note";
    public string ChatActivityLabel
    {
        get
        {
            var labels = activities.Values.Where(x => SelectedRecipient is {} recipient && x.Activity.UserId != CurrentUser.Id && (recipient.IsGroup ? x.Activity.ConversationId == recipient.ConversationId : x.Activity.ConversationId == null && x.Activity.UserId == recipient.ReceiverId))
                .Select(x => (SelectedRecipient!.IsGroup ? "A participant" : SelectedRecipient.ReceiverName) + " is " + (x.Activity.Kind switch { "recording" => "recording a voice note", "voice" => "uploading a voice note", "photo" => "uploading photos", "video" => "uploading videos", "audio" => "uploading audio", _ => "uploading attachments" }) + "…").Distinct();
            var result = string.Join(" · ", labels);
            return result.Length > 0 ? result : IsOtherPartyTyping ? "Typing…" : "";
        }
    }
    public bool HasChatActivity => !string.IsNullOrWhiteSpace(ChatActivityLabel);
    public bool IsRecordingActivity => activities.Values.Any(x=>SelectedRecipient is {} r && x.Activity.UserId!=CurrentUser.Id && (r.IsGroup?x.Activity.ConversationId==r.ConversationId:x.Activity.ConversationId==null&&x.Activity.UserId==r.ReceiverId) && x.Activity.Kind=="recording");
    private void RefreshRecipientActivity(){foreach(var r in Recipients){r.ActivityLabel=string.Join(" · ",activities.Values.Where(x=>x.Activity.UserId!=CurrentUser.Id&&(r.IsGroup?x.Activity.ConversationId==r.ConversationId:x.Activity.ConversationId==null&&x.Activity.UserId==r.ReceiverId)).Select(x=>(r.IsGroup?"A participant":r.ReceiverName)+(x.Activity.Kind=="recording"?" is recording a voice note…":x.Activity.Kind=="photo"?" is sending a photo…":" is sending attachments…")).Distinct());}}
    partial void OnIsOtherPartyTypingChanged(bool value) {OnPropertyChanged(nameof(ChatActivityLabel));OnPropertyChanged(nameof(HasChatActivity));}
    private void OnChatActivity(ChatActivity activity) => Dispatcher.UIThread.Post(() =>
    {
        var key = (activity.UserId, activity.SourceId, activity.Kind);
        if (activity.Active) activities[key] = (activity, DateTime.UtcNow.AddSeconds(8)); else activities.Remove(key);
        RefreshRecipientActivity();OnPropertyChanged(nameof(ChatActivityLabel));OnPropertyChanged(nameof(HasChatActivity));OnPropertyChanged(nameof(IsRecordingActivity));
    });
    private void OnConversationTyping(ConversationTyping typing)
    {
        if (typing.UserId != CurrentUser.Id && typing.ConversationId == SelectedRecipient?.ConversationId)
            OnTypingReceived(typing.IsTyping, SelectedRecipient.ReceiverId.ToString(), CurrentUser.Id.ToString());
    }
    private Task SendTyping(RecipientItemViewModel recipient, bool value) => recipient.IsGroup ? _chatService.NotifyConversationTypingAsync(recipient.ConversationId!, value) : _chatService.NotifyTypingAsync(recipient.ReceiverId, value);
    private bool CanToggleVoiceRecording() => SelectedRecipient != null && !IsSendingMessage && !IsOpeningMicrophone && !IsPreparingVoice && !HasVoiceNote && (IsRecordingVoice || PendingAttachments.Count == 0 && EditingMessage == null);
    private void InitializeVoiceRecording()
    {
        PropertyChanged += (_, e) =>
        {
            if(e.PropertyName is nameof(MessageDraft) or nameof(EditingMessage)){OnPropertyChanged(nameof(ShowSendAction));OnPropertyChanged(nameof(ShowRecordAction));}
            if (e.PropertyName is nameof(SelectedRecipient) or nameof(IsSendingMessage) or nameof(IsOpeningMicrophone) or nameof(IsPreparingVoice) or nameof(IsRecordingVoice) or nameof(HasVoiceNote) or nameof(EditingMessage)) ToggleVoiceRecordingCommand.NotifyCanExecuteChanged();
        };
        PendingAttachments.CollectionChanged += (_, _) => {ToggleVoiceRecordingCommand.NotifyCanExecuteChanged();OnPropertyChanged(nameof(ShowSendAction));OnPropertyChanged(nameof(ShowRecordAction));};
    }
    [RelayCommand(CanExecute = nameof(CanToggleVoiceRecording))] private async Task ToggleVoiceRecordingAsync()
    {
        if (IsSendingMessage || IsOpeningMicrophone || IsPreparingVoice || HasVoiceNote) return;
        var session = voiceSession;
        try
        {
            if (recorder != null)
            {
                var note = recorder; recorder = null; preparingRecorder = note; IsRecordingVoice = false; IsPreparingVoice = true;
                voiceTimer.Stop(); recordingActivity?.Dispose(); recordingActivity = null;
                try
                {
                    var path = await note.StopAsync();
                    if (session != voiceSession) { await note.DisposeAsync(); return; }
                    recordedNotes.Add(note); VoiceNotePath = path;
                }
                catch { await note.DisposeAsync(); throw; }
                finally { if (preparingRecorder == note) preparingRecorder = null; if (session == voiceSession) IsPreparingVoice = false; }
            }
            else if (SelectedRecipient is {} recipient && PendingAttachments.Count == 0 && EditingMessage == null)
            {
                IsOpeningMicrophone = true;
                var note = await CreateVoiceRecorderAsync(Workspace?.MicrophoneDevice ?? "");
                if (session != voiceSession || SelectedRecipient != recipient) { await note.DisposeAsync(); return; }
                recorder = note; IsRecordingVoice = true; IsOpeningMicrophone = false;
                voiceClock.Restart(); voiceTimer.Tick -= VoiceTick; voiceTimer.Tick += VoiceTick; voiceTimer.Start();
                if (_isCurrentlyTypingNotified) { _isCurrentlyTypingNotified = false; await SendTyping(recipient, false); }
                if (session != voiceSession) return;
                recordingActivity = _chatService.BeginChatActivity(recipient.IsGroup ? null : recipient.ReceiverId, recipient.ConversationId, "recording");
            }
        }
        catch (Exception ex) { if (session == voiceSession) { SendError = ex.Message; IsOpeningMicrophone = false; IsPreparingVoice = false; } }
    }
    private void VoiceTick(object? sender, EventArgs e) => OnPropertyChanged(nameof(VoiceStatusLabel));
    [RelayCommand] private Task DiscardVoiceNoteAsync() => IsSendingMessage ? Task.CompletedTask : CancelVoiceRecording();
    private async Task CancelVoiceRecording()
    {
        voiceSession++; voiceTimer.Stop(); voiceClock.Reset();
        var note = recorder; recorder = null; IsRecordingVoice = false; IsPreparingVoice = IsOpeningMicrophone = false;
        VoiceNotePath = null;
        recordingActivity?.Dispose(); recordingActivity = null;
        if (note != null) await note.DisposeAsync();
        var preparing = preparingRecorder; preparingRecorder = null;
        if (preparing != null) await preparing.DisposeAsync();
        await CleanupDetachedVoiceNotes();
    }
    private async Task CleanupDetachedVoiceNotes()
    {
        if (IsSendingMessage) return;
        foreach (var note in recordedNotes.Where(x => x.Path != VoiceNotePath && !PendingAttachments.Any(item => item.FilePath == x.Path)).ToArray())
        {
            try { await note.DisposeAsync(); recordedNotes.Remove(note); }
            catch (Exception ex) { SendError = ex.Message; }
        }
    }
}
