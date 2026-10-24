using Avalonia.Controls;
using Avalonia.Headless;
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
public sealed class WorkplaceUxTests
{
    [Theory]
    [InlineData(0,"Completed years of service","After 5 completed service years",true)]
    [InlineData(1,"Minimum age (years)","At age 5 or older",true)]
    [InlineData(2,"Minimum qualifying value","When qualified by an administrator",false)]
    [InlineData(3,"Minimum qualifying value","When score is at least 5",true)]
    public void QualificationEditor_ExplainsAllowanceAndOnlyAsksForRelevantFields(int kind,string label,string phrase,bool minimum)
    {
        var rule=new VacationRuleEditor{Kind=kind,Threshold=5,AdditionalDays=3,CustomKey="score"};
        Assert.Equal(label,rule.MinimumLabel);Assert.Equal(minimum,rule.RequiresMinimum);Assert.Equal(kind==3,rule.IsCustom);Assert.Contains(phrase,rule.QualificationSummary);Assert.Contains("3 extra leave days",rule.QualificationSummary);
    }
    [AvaloniaFact]
    public async Task TimezoneDropdown_UsesServerCatalogueAndResolvesRemoteHours()
    {
        var api=Substitute.For<ICollaborationApi>();var date=new DateOnly(2027,7,15);var zone=new WorkTimeZoneOption("Europe/Berlin","Berlin",true,true,120);
        api.TimeZonesAsync(date,Arg.Any<CancellationToken>()).Returns(new List<WorkTimeZoneOption>{zone});
        api.PreviewScheduleClockAsync("Europe/Berlin",date,new TimeOnly(9,0),new TimeOnly(17,0),0,Arg.Any<CancellationToken>()).Returns(new ScheduleClockPreview(new(2027,7,15,7,0,0,DateTimeKind.Utc),new(2027,7,15,15,0,0,DateTimeKind.Utc),true,"Europe/Berlin"));
        var vm=new WorkplaceManagementViewModel(api){ScheduleFrom=new DateTimeOffset(2027,7,15,0,0,0,TimeSpan.Zero),ShiftStart=TimeSpan.FromHours(9),ShiftEnd=TimeSpan.FromHours(17),TimeZoneId="Europe/Berlin"};
        await vm.LoadTimeZones();Assert.Equal(zone,vm.SelectedTimeZone);Assert.Contains("Summer time applied",zone.ToString());Assert.Contains("UTC+02:00",zone.ToString());Assert.Contains("Your local time",vm.RemoteHours);Assert.Contains("Summer time applied",vm.RemoteHours);
        Assert.Contains(zone,vm.TimeZones); // Native management screens were removed at the owner's request; scheduling API behavior remains covered.
    }
    [AvaloniaFact]
    public async Task PolicyPublication_ForwardsSelectedDateAndCanClearItForImmediateApplication()
    {
        var api=Substitute.For<ICollaborationApi>();api.PoliciesAsync(1,Arg.Any<CancellationToken>()).Returns(new PolicyCatalog(new(),new()));api.SchedulesAsync(1,Arg.Any<CancellationToken>()).Returns(new ScheduleCatalog(new()));
        var vm=new WorkplaceManagementViewModel(api){Country=new(1,"Egypt"),ConfirmAsync=(_,_)=>Task.FromResult(true)};
        vm.PolicyPublishDate=new DateTimeOffset(2027,4,1,0,0,0,TimeSpan.Zero);await vm.SavePolicyCommand.ExecuteAsync(null);
        await api.Received(1).SavePolicyAsync(Arg.Any<VacationPolicyEditor>(),Arg.Any<IReadOnlyList<System.Text.Json.JsonElement>>(),Arg.Any<CancellationToken>(),new DateOnly(2027,4,1));
        vm.ApplyPolicyImmediatelyCommand.Execute(null);Assert.Null(vm.PolicyPublishDate);await vm.SavePolicyCommand.ExecuteAsync(null);
        await api.Received(1).SavePolicyAsync(Arg.Any<VacationPolicyEditor>(),Arg.Any<IReadOnlyList<System.Text.Json.JsonElement>>(),Arg.Any<CancellationToken>(),null);
    }
    [AvaloniaFact]
    public async Task VoiceActivity_IlluminatesParticipantAndFocusedStageAndClearsOnMute()
    {
        var auth=Substitute.For<IAuthService>();auth.CurrentUser.Returns(new UserData{Id=Guid.NewGuid()});var chat=Substitute.For<IChatService>();var media=Substitute.For<INativeMediaSession>();
        var chatVm=new ChatViewModel(auth,chat,Substitute.For<IUserService>(),Substitute.For<IImageService>(),Substitute.For<IFileService>(),Substitute.For<INotificationService>());
        var vm=new WorkspaceViewModel(chatVm,Substitute.For<ICollaborationApi>(),media,auth,chat){ReduceMotion=true};var peer=new VideoTileViewModel("peer"){Name="Colleague"};vm.Videos.Add(peer);
        var window=new CallWindow{DataContext=vm};window.Show();media.ParticipantUpdated+=Raise.Event<Action<MediaParticipantUpdate>>(new MediaParticipantUpdate("peer","speaking","1"));Dispatcher.UIThread.RunJobs();Assert.True(peer.IsSpeaking);Assert.Equal("Speaking",peer.MicrophoneLabel);
        Assert.Contains(window.GetVisualDescendants().OfType<Border>(),x=>x.Classes.Contains("speaking"));vm.FocusedVideo=peer;Dispatcher.UIThread.RunJobs();Assert.Contains(window.FindControl<ContentControl>("FocusedStage")!.GetVisualDescendants().OfType<Border>(),x=>x.Classes.Contains("speaking"));
        Save(window,"native-voice-activity.png");media.ParticipantUpdated+=Raise.Event<Action<MediaParticipantUpdate>>(new MediaParticipantUpdate("peer","microphone","0"));Dispatcher.UIThread.RunJobs();Assert.False(peer.IsSpeaking);Assert.DoesNotContain(window.GetVisualDescendants().OfType<Border>(),x=>x.Classes.Contains("speaking"));media.ParticipantLeft+=Raise.Event<Action<string>>("peer");Dispatcher.UIThread.RunJobs();media.ParticipantUpdated+=Raise.Event<Action<MediaParticipantUpdate>>(new MediaParticipantUpdate("peer","speaking","0"));Dispatcher.UIThread.RunJobs();Assert.DoesNotContain(vm.Videos,x=>x.Identity=="peer");window.CloseAfterCall();await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public void TwelveAndTwentyFourHourChoices_PreserveShiftTimesAndShowDatesAndLeaveCharge()
    {
        var clock=ClockDisplaySettings.Shared;var original=clock.TwelveHour;
        try {var shift=new VacationShift(new(2027,7,15),new DateTime(2027,7,15,13,30,0).ToUniversalTime(),new DateTime(2027,7,15,21,30,0).ToUniversalTime(),1);clock.TwelveHour=false;Assert.Contains("13:30",shift.Label);clock.TwelveHour=true;Assert.Contains("1:30 PM",shift.Label);Assert.Contains("2027",shift.Label);Assert.Contains("1 leave day",shift.Label);var picker=new TimePicker{SelectedTime=TimeSpan.FromHours(13.5),ClockIdentifier=clock.Identifier};Assert.Equal("12HourClock",picker.ClockIdentifier);Assert.Equal(TimeSpan.FromHours(13.5),picker.SelectedTime);}
        finally {clock.TwelveHour=original;}
    }
    private static void Save(Window window,string name) {if(Environment.GetEnvironmentVariable("FIREUSER_TEST_ARTIFACTS") is not {} path)return;Directory.CreateDirectory(path);using var frame=window.CaptureRenderedFrame();Assert.NotNull(frame);using var file=File.Create(Path.Combine(path,name));frame.Save(file,new Avalonia.Media.Imaging.PngBitmapEncoderOptions());}
}
