using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using UserIdentityDesktop.Core;
using UserIdentityDesktop.Core.Models;
namespace UserIdentityDesktop.ViewModels;
public partial class AppearanceSettingsViewModel : ObservableObject, IDisposable
{
    private readonly ICollaborationApi api;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Avalonia.Threading.DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private AppearancePreferences pending = new();
    private bool applying, saving, disposed, editing;
    private long revision;
    public ClockDisplaySettings Clock => ClockDisplaySettings.Shared;
    [ObservableProperty] private double brightness = 100;
    [ObservableProperty] private string status = "";
    public AppearanceSettingsViewModel(ICollaborationApi api)
    {
        this.api=api;
        brightness=Avalonia.Application.Current?.RequestedThemeVariant==ThemeVariant.Dark?0:100;
        applying=true;Clock.TwelveHour=false;applying=false;
        Clock.PropertyChanged += ClockChanged;
        timer.Tick += OnTimer;
    }
    partial void OnBrightnessChanged(double value)
    {
        if(!double.IsFinite(value))return;
        var level=(int)Math.Clamp(Math.Round(value),0,100);
        NativeAppearance.Apply(level);
        if(!applying)Queue(pending with { Brightness=level });
    }
    private void ClockChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName==nameof(Clock.TwelveHour)&&!applying)Queue(pending with { TwelveHour=Clock.TwelveHour });
    }
    private void Queue(AppearancePreferences value)
    {
        if(disposed)return;
        revision++;pending=value;Status="Saving preferences…";timer.Stop();timer.Start();
    }
    public void BeginAdjusting(){editing=true;revision++;}
    public Task EndAdjustingAsync(){var wasEditing=editing;editing=false;return pending!=new AppearancePreferences()?FlushAsync():wasEditing?RefreshAsync():Task.CompletedTask;}
    private async void OnTimer(object? sender,EventArgs e) {timer.Stop();await FlushAsync();}
    public async Task RefreshAsync()
    {
        if(disposed||saving||editing||pending!=new AppearancePreferences())return;
        var version=++revision;
        try {
            var value=await api.AppearanceAsync(lifetime.Token);
            if(disposed||version!=revision||saving||editing||pending!=new AppearancePreferences()||value==null)return;
            applying=true;
            try {if(value.TwelveHour is {} clock)Clock.TwelveHour=clock;if(value.Brightness is {} level)Brightness=level;}
            finally {applying=false;}
        }catch(OperationCanceledException) when(lifetime.IsCancellationRequested){}
        catch {if(!disposed&&version==revision)Status="Preferences could not be loaded.";}
    }
    public async Task FlushAsync()
    {
        if(disposed||saving||editing||pending==new AppearancePreferences())return;
        var value=pending;pending=new();saving=true;
        try {await api.SaveAppearanceAsync(value,lifetime.Token);if(!disposed)Status="";}
        catch(OperationCanceledException) when(lifetime.IsCancellationRequested){return;}
        catch {if(!disposed){pending=new(pending.TwelveHour??value.TwelveHour,pending.Brightness??value.Brightness);Status="Preferences could not be saved. Change the setting to retry.";}return;}
        finally {saving=false;}
        if(pending!=new AppearancePreferences())await FlushAsync();else await RefreshAsync();
    }
    public void Dispose(){disposed=true;revision++;timer.Stop();timer.Tick-=OnTimer;Clock.PropertyChanged-=ClockChanged;lifetime.Cancel();lifetime.Dispose();}
}
