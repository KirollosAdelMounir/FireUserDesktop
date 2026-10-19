using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UserIdentityDesktop.Core.Models;

namespace UserIdentityDesktop.ViewModels;

public partial class WorkspaceViewModel
{
    public ObservableCollection<MediaDeviceOption> Microphones { get; } = new();
    public ObservableCollection<MediaDeviceOption> Speakers { get; } = new();
    public ObservableCollection<MediaDeviceOption> Cameras { get; } = new();
    [ObservableProperty] private MediaDeviceOption? selectedMicrophone;
    [ObservableProperty] private MediaDeviceOption? selectedSpeaker;
    [ObservableProperty] private MediaDeviceOption? selectedCamera;
    [ObservableProperty] private string deviceStatus = "Refresh to discover connected devices.";
    [ObservableProperty] private bool checkingDevices;
    [ObservableProperty] private double microphonePeak;
    [ObservableProperty] private VideoPicture? cameraPreview;
    partial void OnSelectedMicrophoneChanged(MediaDeviceOption? value) { MicrophoneDevice = value?.Id ?? ""; }
    partial void OnSelectedCameraChanged(MediaDeviceOption? value) { CameraDevice = value?.Id ?? ""; CameraPreview = null; }
    partial void OnSelectedSpeakerChanged(MediaDeviceOption? value) { _ = SelectSpeakerAsync(value?.Id ?? ""); }
    private async Task SelectSpeakerAsync(string device)
    {
        try { await media.SetSpeakerAsync(device); }
        catch (Exception ex) { DeviceStatus = ex.Message; }
    }
    private async Task DeviceAction(Func<Task> action)
    {
        if (CheckingDevices) return;
        CheckingDevices = true;
        try { await action(); }
        catch (OperationCanceledException) { if (!lifetime.IsCancellationRequested) DeviceStatus = "Device check timed out. Check the device and permissions."; }
        catch (Exception ex) { DeviceStatus = ex.Message; }
        finally { CheckingDevices = false; }
    }
    [RelayCommand] private Task RefreshDevicesAsync() => DeviceAction(async () =>
    {
        DeviceStatus = "Looking for connected devices…";
        var catalog = await media.GetDevicesAsync(lifetime.Token);
        var mic = SelectedMicrophone?.Id; var speaker = SelectedSpeaker?.Id; var camera = SelectedCamera?.Id;
        Microphones.Clear(); foreach (var item in catalog.Microphones) Microphones.Add(item);
        Speakers.Clear(); foreach (var item in catalog.Speakers) Speakers.Add(item);
        Cameras.Clear(); foreach (var item in catalog.Cameras) Cameras.Add(item);
        SelectedMicrophone = Microphones.FirstOrDefault(x => x.Id == mic) ?? Microphones.FirstOrDefault();
        SelectedSpeaker = Speakers.FirstOrDefault(x => x.Id == speaker) ?? Speakers.FirstOrDefault();
        SelectedCamera = Cameras.FirstOrDefault(x => x.Id == camera) ?? Cameras.FirstOrDefault();
        DeviceStatus = catalog.Warning ?? $"Devices refreshed · {Cameras.Count} camera(s). Checks stay on this device and are never sent to a call.";
    });
    [RelayCommand] private Task CheckMicrophoneAsync() => DeviceAction(async () =>
    {
        if (MicrophoneEnabled) { DeviceStatus = "Turn off your call microphone before checking it."; return; }
        DeviceStatus = "Say something… listening for one second.";
        MicrophonePeak = await media.CheckMicrophoneAsync(SelectedMicrophone?.Id ?? "", lifetime.Token);
        DeviceStatus = MicrophonePeak > .5 ? "Microphone check complete. Sound was detected." : "Microphone opened, but no sound was detected. Check mute and input volume.";
    });
    [RelayCommand] private Task CheckCameraAsync() => DeviceAction(async () =>
    {
        if (CameraEnabled) { DeviceStatus = "Turn off your call camera before checking it."; return; }
        if (SelectedCamera == null) { DeviceStatus = "No camera selected. Refresh devices first."; return; }
        DeviceStatus = "Opening camera preview…";
        CameraPreview = await media.CheckCameraAsync(SelectedCamera.Id, lifetime.Token);
        DeviceStatus = "Camera check complete. Preview captured locally; camera is now stopped.";
    });
    [RelayCommand] private Task CheckSpeakerAsync() => DeviceAction(async () =>
    {
        DeviceStatus = "Playing a short test tone…";
        await media.CheckSpeakerAsync(SelectedSpeaker?.Id ?? "", lifetime.Token);
        DeviceStatus = "Speaker check complete. You should have heard a short tone.";
    });
}
