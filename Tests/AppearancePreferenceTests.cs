using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using UserIdentityDesktop.Core;
using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.ViewModels;
using UserIdentityDesktop.Views;
using Xunit;
namespace UserIdentityDesktop.Tests;
public sealed class AppearancePreferenceTests
{
    [AvaloniaFact]
    public async Task ProfileMenuUsesNativeControlsAndTargetedChangesRefreshPreferences()
    {
        var auth=Substitute.For<IAuthService>();auth.CurrentUser.Returns(new UserData{Id=Guid.NewGuid()});var chat=Substitute.For<IChatService>();var api=Substitute.For<ICollaborationApi>();
        api.AppearanceAsync(Arg.Any<CancellationToken>()).Returns(new AppearancePreferences(false,32));
        api.SaveAppearanceAsync(Arg.Any<AppearancePreferences>(),Arg.Any<CancellationToken>()).Returns(new AppearancePreferences(true,32));
        var chatVm=new ChatViewModel(auth,chat,Substitute.For<IUserService>(),Substitute.For<IImageService>(),Substitute.For<IFileService>(),Substitute.For<INotificationService>());
        var workspace=new WorkspaceViewModel(chatVm,api,Substitute.For<INativeMediaSession>(),auth,chat){ReduceMotion=true};
        var view=new ChatView{DataContext=chatVm};var window=new Window{Content=view,Width=1100,Height=760};window.Show();Dispatcher.UIThread.RunJobs();
        var button=view.GetVisualDescendants().OfType<Button>().Single(x=>Avalonia.Automation.AutomationProperties.GetName(x)=="Account menu");
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();var menu=button.ContextMenu!;
        var clock=menu.Items.OfType<MenuItem>().Single(x=>x.Header as string=="Time format");Assert.Equal(2,clock.Items.Count);
        var twelve=clock.Items.OfType<MenuItem>().Single(x=>x.Header as string=="12-hour (AM/PM)");twelve.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));Assert.True(workspace.Clock.TwelveHour);
        var appearance=menu.Items.OfType<MenuItem>().Single(x=>x.Header as string=="Appearance");
        var panel=Assert.IsType<StackPanel>(Assert.Single(appearance.Items.OfType<MenuItem>()).Header);Assert.Equal(180,panel.Width);var slider=Assert.Single(panel.Children.OfType<Slider>());Assert.Equal(0,slider.Minimum);Assert.Equal(100,slider.Maximum);
        slider.Value=32;await workspace.Appearance.FlushAsync();Assert.Equal(32,workspace.Appearance.Brightness);
        chat.AppearancePreferencesChanged+=Raise.Event<Action>();Dispatcher.UIThread.RunJobs();await api.Received().AppearanceAsync(Arg.Any<CancellationToken>());
        menu.Close();window.Close();await workspace.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task BrightnessDragDefersPersistenceAndRemoteRefreshUntilReleased()
    {
        var api=Substitute.For<ICollaborationApi>();api.AppearanceAsync(Arg.Any<CancellationToken>()).Returns(new AppearancePreferences(false,48));api.SaveAppearanceAsync(Arg.Any<AppearancePreferences>(),Arg.Any<CancellationToken>()).Returns(new AppearancePreferences(false,48));
        using var vm=new AppearanceSettingsViewModel(api);vm.BeginAdjusting();vm.Brightness=33;await vm.FlushAsync();await vm.RefreshAsync();
        await api.DidNotReceive().AppearanceAsync(Arg.Any<CancellationToken>());await api.DidNotReceive().SaveAppearanceAsync(Arg.Any<AppearancePreferences>(),Arg.Any<CancellationToken>());
        vm.Brightness=48;await vm.EndAdjustingAsync();Assert.Equal(48,vm.Brightness);await api.Received(1).SaveAppearanceAsync(new(null,48),Arg.Any<CancellationToken>());
        vm.BeginAdjusting();api.AppearanceAsync(Arg.Any<CancellationToken>()).Returns(new AppearancePreferences(true,65));await vm.RefreshAsync();Assert.Equal(48,vm.Brightness);
        await vm.EndAdjustingAsync();Assert.Equal(65,vm.Brightness);Assert.True(vm.Clock.TwelveHour);
    }
    [AvaloniaFact]
    public async Task SavedAppearanceLoadsAndStaleReadCannotRevertLocalBrightness()
    {
        var api=Substitute.For<ICollaborationApi>();var response=new TaskCompletionSource<AppearancePreferences>();
        api.AppearanceAsync(Arg.Any<CancellationToken>()).Returns(response.Task);
        using var vm=new AppearanceSettingsViewModel(api);var read=vm.RefreshAsync();vm.Brightness=38;
        response.SetResult(new(true,99));await read;Assert.Equal(38,vm.Brightness);
        api.AppearanceAsync(Arg.Any<CancellationToken>()).Returns(new AppearancePreferences(true,38));
        api.SaveAppearanceAsync(Arg.Any<AppearancePreferences>(),Arg.Any<CancellationToken>()).Returns(new AppearancePreferences(null,38));
        await vm.FlushAsync();Assert.True(vm.Clock.TwelveHour);Assert.Equal(38,vm.Brightness);
        await api.Received(1).SaveAppearanceAsync(new(null,38),Arg.Any<CancellationToken>());
    }
    [AvaloniaFact]
    public async Task NewClockAndBrightnessChoicesQueueBehindAnInFlightSave()
    {
        var api=Substitute.For<ICollaborationApi>();var saved=new TaskCompletionSource<AppearancePreferences>();
        api.SaveAppearanceAsync(Arg.Any<AppearancePreferences>(),Arg.Any<CancellationToken>()).Returns(saved.Task,Task.FromResult(new AppearancePreferences(true,61)));
        api.AppearanceAsync(Arg.Any<CancellationToken>()).Returns(new AppearancePreferences(true,61));
        using var vm=new AppearanceSettingsViewModel(api);vm.Brightness=35;var saving=vm.FlushAsync();
        vm.Brightness=61;vm.Clock.TwelveHour=true;await vm.RefreshAsync();
        await api.DidNotReceive().AppearanceAsync(Arg.Any<CancellationToken>());
        saved.SetResult(new(false,35));await saving;
        Assert.Equal(61,vm.Brightness);Assert.True(vm.Clock.TwelveHour);Assert.Equal("",vm.Status);
        await api.Received(1).SaveAppearanceAsync(new(true,61),Arg.Any<CancellationToken>());
    }
    [AvaloniaFact]
    public async Task FailedSaveKeepsVisibleChoiceAndRetriesWithoutLosingOtherSettings()
    {
        var api=Substitute.For<ICollaborationApi>();api.SaveAppearanceAsync(Arg.Any<AppearancePreferences>(),Arg.Any<CancellationToken>()).Returns(Task.FromException<AppearancePreferences>(new HttpRequestException()));
        using var vm=new AppearanceSettingsViewModel(api);vm.Brightness=45;await vm.FlushAsync();Assert.Equal(45,vm.Brightness);Assert.Contains("could not be saved",vm.Status);
        api.SaveAppearanceAsync(Arg.Any<AppearancePreferences>(),Arg.Any<CancellationToken>()).Returns(new AppearancePreferences(true,45));api.AppearanceAsync(Arg.Any<CancellationToken>()).Returns(new AppearancePreferences(true,45));
        vm.Clock.TwelveHour=true;await vm.FlushAsync();await api.Received(1).SaveAppearanceAsync(new(true,45),Arg.Any<CancellationToken>());Assert.Equal("",vm.Status);
    }
    [AvaloniaFact]
    public void SystemTimestampsAndCalendarFollowClockWithoutChangingScheduledTime()
    {
        var clock=ClockDisplaySettings.Shared;var old=clock.TwelveHour;
        try {
            var calendar=new ScheduleCalendar{FocusDate=DateTimeOffset.Now,Items=new[]{new CalendarItem(Guid.NewGuid(),"Meeting","Design",null,DateTime.Today.AddHours(19).ToUniversalTime(),DateTime.Today.AddHours(20).ToUniversalTime(),Guid.NewGuid(),1,1)}};
            var window=new Window{Content=calendar};window.Show();clock.TwelveHour=false;Dispatcher.UIThread.RunJobs();
            var hour=calendar.FindControl<ComboBox>("HourChoice")!;hour.SelectedIndex=19;
            Assert.Contains(calendar.GetVisualDescendants().OfType<TextBlock>(),x=>x.Text?.Contains("19:00")==true);
            clock.TwelveHour=true;Dispatcher.UIThread.RunJobs();Assert.Equal(19,hour.SelectedIndex);Assert.Contains(calendar.GetVisualDescendants().OfType<TextBlock>(),x=>x.Text?.Contains("7:00 PM")==true);
            var converter=new UserIdentityDesktop.Converters.DateTimeFormatConverter();var now=DateTime.Today.AddHours(19).ToUniversalTime();Assert.Equal("7:00 PM",converter.Convert(now,typeof(string),null,System.Globalization.CultureInfo.InvariantCulture));
            clock.TwelveHour=false;Assert.Equal("19:00",converter.Convert(now,typeof(string),null,System.Globalization.CultureInfo.InvariantCulture));window.Close();
        }finally{clock.TwelveHour=old;}
    }
    [Fact]
    public void EveryBrightnessHasReadableNativeTextAndDistinctEndpointColors()
    {
        Assert.NotEqual(NativeAppearance.Palette(0)["ForgeBg"],NativeAppearance.Palette(100)["ForgeBg"]);
        for(var level=0;level<=100;level++){
            var colors=NativeAppearance.Palette(level);
            foreach(var text in new[]{"ForgeTextPrimary","ForgeTextSecondary"})foreach(var surface in new[]{"ForgeBg","ForgeBgSecondary","ForgeSurface","ForgeSurfaceCard","ForgeBubbleReceived","ForgeBubbleSent"})
                Assert.True(NativeAppearance.Contrast(colors[text],colors[surface])>=4.49,$"{level}: {text} on {surface}");
        }
    }
}
