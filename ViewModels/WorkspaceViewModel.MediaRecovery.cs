using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using UserIdentityDesktop.Core.Models;

namespace UserIdentityDesktop.ViewModels;

public partial class WorkspaceViewModel
{
    private CancellationTokenSource? mediaRecovery;
    private void OnMediaConnectionChanged(MediaConnectionState state) => Dispatcher.UIThread.Post(() =>
    {
        if (disposed || !InCall) return;
        if (state.State == "reconnecting") { ConnectingCall = true; Feedback = "Media connection interrupted. Reconnecting…"; return; }
        if (state.State == "connected") { ConnectingCall = false; Feedback = "Call reconnected."; return; }
        if (state.State != "disconnected" || mediaRecovery != null) return;
        if (!state.Retryable)
        {
            ConnectingCall = false;
            MicrophoneEnabled = CameraEnabled = ScreenEnabled = false;
            Feedback = $"Media connection ended (reason {state.Reason}). Leave this call before joining again.";
            return;
        }
        var microphone = MicrophoneEnabled; var camera = CameraEnabled;
        mediaRecovery = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        _ = RecoverMediaAsync(mediaRecovery, microphone, camera);
    });

    private async Task RecoverMediaAsync(CancellationTokenSource recovery, bool microphone, bool camera)
    {
        var token = recovery.Token;
        try
        {
            await mediaGate.WaitAsync(token);
            try
            {
                ConnectingCall = true; MediaBusy = true;
                MicrophoneEnabled = CameraEnabled = ScreenEnabled = false;
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    if (disposed || !InCall) return;
                    Feedback = $"Reconnecting call… (attempt {attempt + 1}/5)";
                    try
                    {
                        // Cleanup and room destruction run outside the native callback thread.
                        await media.DisconnectAsync();
                        var credentials = meetingLocator is {} meeting
                            ? await api.MeetingTokenAsync(meeting, token)
                            : callId is {} call ? await api.CallTokenAsync(call, token)
                            : throw new InvalidOperationException("This call is no longer available.");
                        await media.ConnectAsync(credentials, token);
                        token.ThrowIfCancellationRequested();
                        Videos.Clear(); FocusedVideo = null; pendingFrames.Clear();
                        Videos.Add(new VideoTileViewModel("You") { Name = "You", AvatarInitials = auth.CurrentUser?.Initials });
                        // A new room never resumes screen capture or control grants implicitly.
                        try
                        {
                            if (microphone) { await media.SetMicrophoneAsync(true, Devices, token); MicrophoneEnabled = true; }
                            if (camera) { await media.SetCameraAsync(true, Devices, token); CameraEnabled = true; }
                            Feedback = "Call reconnected. Share your screen again if needed.";
                        }
                        catch (Exception error) when (error is not OperationCanceledException)
                        { Feedback = "Call reconnected; a device could not restart: " + error.Message; }
                        await UpdateCallPresence();
                        return;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception error)
                    {
                        Feedback = "Unable to reconnect the call: " + error.Message;
                        if (attempt < 4) await Task.Delay(TimeSpan.FromSeconds(1 << attempt), token);
                    }
                }
                Feedback = "The media connection could not be restored. Leave and rejoin the call.";
            }
            finally { ConnectingCall = false; MediaBusy = false; mediaGate.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { if (mediaRecovery == recovery) mediaRecovery = null; recovery.Dispose(); }
    }
}
