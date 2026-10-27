using System;
using System.Linq;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UserIdentityDesktop.Core.Models;
namespace UserIdentityDesktop.ViewModels;
public partial class WorkspaceViewModel
{
    public ObservableCollection<CallLogItem> CallLogs { get; } = new();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasMissedCalls))] private int _missedCalls;
    public bool HasMissedCalls => MissedCalls > 0;
    [ObservableProperty] private string _callLogsError = "";
    [ObservableProperty] private bool _loadingCallLogs;
    [ObservableProperty] private bool _hasOlderCalls;
    private string? callLogsCursor;
    private DateTime callLogsObserved;
    private readonly SemaphoreSlim callLogsGate = new(1,1);
    partial void OnSelectedWorkspaceTabChanged(int value) { if(value==6) _=RefreshCallLogsAsync(); }
    private void OnCallLogMessage(ChatMessage message) { if(message.CallSummary != null) InvalidateCallLogs(); }
    private void InvalidateCallLogs() => Dispatcher.UIThread.Post(()=>{if(!disposed)_=RefreshCallLogsAsync();});
    public async Task RefreshCallLogsAsync()
    {
        await callLogsGate.WaitAsync();
        try {
            if(disposed)return;
            LoadingCallLogs=true;CallLogsError="";
            var page=await api.CallLogsAsync(ct:lifetime.Token);
            CallLogs.Clear();foreach(var item in page.Items)CallLogs.Add(item);
            callLogsCursor=page.NextCursor;HasOlderCalls=callLogsCursor!=null;
            callLogsObserved=page.ObservedAt;MissedCalls=page.MissedCount;
            if(SelectedWorkspaceTab==6 && MissedCalls>0) { await api.MarkCallLogsSeenAsync(callLogsObserved,lifetime.Token);MissedCalls=0; }
        } catch(OperationCanceledException) when(lifetime.IsCancellationRequested) { }
        catch(Exception ex) { if(!disposed)CallLogsError=ex.Message; }
        finally { LoadingCallLogs=false;callLogsGate.Release(); }
    }
    [RelayCommand] private Task RetryCallLogsAsync()=>RefreshCallLogsAsync();
    [RelayCommand] private async Task OlderCallsAsync()
    {
        await callLogsGate.WaitAsync();
        try {
            if(callLogsCursor==null || disposed)return;
            LoadingCallLogs=true;CallLogsError="";
            var page=await api.CallLogsAsync(callLogsCursor,lifetime.Token);
            foreach(var item in page.Items)if(!CallLogs.Any(x=>x.Id==item.Id))CallLogs.Add(item);
            callLogsCursor=page.NextCursor;HasOlderCalls=callLogsCursor!=null;
        } catch(Exception ex) { if(!disposed)CallLogsError=ex.Message; }
        finally { LoadingCallLogs=false;callLogsGate.Release(); }
    }
    [RelayCommand] private Task RedialAsync(CallLogItem? item)=>RunMedia(async()=>{
        if(item==null)return;
        if(InCall)throw new InvalidOperationException("Leave the current call first.");
        if(!CallingEnabled)throw new InvalidOperationException("Calling is not configured on the server.");
        var call=item.ConversationId is {} group ? await api.CreateGroupCallAsync(group,item.CallType=="video",lifetime.Token) : await api.CreateCallAsync(item.PeerId,item.CallType=="video",lifetime.Token);
        callId=call.CallId;activeCallConversation=call.ConversationId;activeCallUsers.UnionWith(call.ParticipantIds);
        callSounds.Play("Ringback");CallTitle=item.Name;ConnectingCall=true;
        try {await JoinMedia(await api.CallTokenAsync(callId,lifetime.Token),item.Name);}
        catch {callSounds.Play(null);await api.LeaveCallAsync(callId);callId=null;throw;}
    });
}
