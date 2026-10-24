using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using UserIdentityDesktop.Core;
using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.Data;
using UserIdentityDesktop.Service;
using UserIdentityDesktop.ViewModels;
using UserIdentityDesktop.Views;
using Xunit;
namespace UserIdentityDesktop.Tests;
public sealed class WorkplaceUpdateTests
{
    private static (WorkspaceViewModel Vm, ICollaborationApi Api, IChatService Chat, INativeMediaSession Media) Create()
    {
        var auth = Substitute.For<IAuthService>(); auth.CurrentUser.Returns(new UserData { Id = Guid.NewGuid() });
        var api = Substitute.For<ICollaborationApi>(); var chat = Substitute.For<IChatService>(); var media = Substitute.For<INativeMediaSession>();
        var chatVm = new ChatViewModel(auth,chat,Substitute.For<IUserService>(),Substitute.For<IImageService>(),Substitute.For<IFileService>(),Substitute.For<INotificationService>());
        api.CatalogAsync(Arg.Any<CancellationToken>()).Returns(new WorkplaceCatalog(new()));
        api.AnnouncementsAsync(Arg.Any<CancellationToken>()).Returns(new List<Announcement>());
        api.CalendarAsync(Arg.Any<Guid>(),Arg.Any<DateTime>(),Arg.Any<DateTime>(),Arg.Any<CancellationToken>()).Returns(new List<CalendarItem>());
        return (new WorkspaceViewModel(chatVm,api,media,auth,chat) { ReduceMotion = true, ConfirmActionAsync = (_,_) => Task.FromResult(true) },api,chat,media);
    }
    [AvaloniaFact]
    public void Picker_TransfersSelectedPeopleAndFiltersUnselectedResults()
    {
        var people = new ObservableCollection<PersonSelection> { new(new(Guid.NewGuid(),"Alice")),new(new(Guid.NewGuid(),"Bob")) };
        var picker = new AttendeePickerViewModel(Substitute.For<ICollaborationApi>(), people);
        picker.ToggleCommand.Execute(people[0]); Assert.Equal(people[0],Assert.Single(picker.SelectedPeople)); Assert.Equal(people[1],Assert.Single(picker.Results));
        picker.Search = "Alice"; Assert.Empty(picker.Results); picker.ToggleCommand.Execute(people[0]); Assert.Empty(picker.SelectedPeople); Assert.Equal(people[0],Assert.Single(picker.Results));
        picker.SelectAllCommand.Execute(null); Assert.Equal(people[0],Assert.Single(picker.SelectedPeople)); Assert.False(people[1].Selected);
        picker.ClearCommand.Execute(null); Assert.Empty(picker.SelectedPeople);
    }
    [AvaloniaFact]
    public async Task Picker_UnitExpansionIncludesResolvedDescendantsAndUnselectionClearsUnitGrant()
    {
        var api = Substitute.For<ICollaborationApi>(); var unit = new MeetingUnitOption(Guid.NewGuid(),"Department",2);
        var parent = new MeetingAudiencePerson(Guid.NewGuid(),"Parent unit member"); var child = new MeetingAudiencePerson(Guid.NewGuid(),"Child unit member");
        api.MeetingUnitsAsync(Arg.Any<CancellationToken>()).Returns(new List<MeetingUnitOption> { unit });
        api.ResolveMeetingUnitsAsync(Arg.Any<IReadOnlyList<Guid>>(),Arg.Any<CancellationToken>()).Returns(new List<MeetingAudiencePerson> { parent,child });
        var picker = new AttendeePickerViewModel(api,new()); await picker.LoadAsync(); picker.Unit = unit; await picker.AddUnitCommand.ExecuteAsync(null);
        Assert.Equal(new[] { parent.Id,child.Id }.Order(),picker.UserIds.Order()); Assert.Equal(unit.Id,Assert.Single(picker.UnitIds)); Assert.Null(picker.Unit);
        picker.ToggleCommand.Execute(picker.SelectedPeople.First(x=>x.Id==child.Id)); Assert.Empty(picker.UnitIds); Assert.Equal(parent.Id,Assert.Single(picker.UserIds));
        api.ResolveMeetingUnitsAsync(Arg.Any<IReadOnlyList<Guid>>(),Arg.Any<CancellationToken>()).Returns(Task.FromException<List<MeetingAudiencePerson>>(new UnauthorizedAccessException("Reporting authority changed")));
        picker.Unit = unit; await picker.AddUnitCommand.ExecuteAsync(null); Assert.Contains("Reporting authority",picker.Error); Assert.Equal(parent.Id,Assert.Single(picker.UserIds));
    }
    [Theory]
    [InlineData("person@example.com",true)] [InlineData("person+guest@example.co.uk",true)] [InlineData("Name <person@example.com>",false)] [InlineData("person@host",false)] [InlineData("bad@@example.com",false)] [InlineData("person@example.com\r\nBcc:other@example.com",false)]
    public void Picker_EmailValidationRejectsMalformedAndHeaderInputs(string email,bool expected) => Assert.Equal(expected,AttendeePickerViewModel.ValidEmail(email));
    [AvaloniaFact]
    public void Picker_EmailsDeduplicateAndCannotBeAddedToOrdinaryCalls()
    {
        var picker = new AttendeePickerViewModel(Substitute.For<ICollaborationApi>(),new()); picker.Email="guest@example.com"; picker.AddEmailCommand.Execute(null); picker.Email="GUEST@example.com"; picker.AddEmailCommand.Execute(null); Assert.Single(picker.Emails);
        picker.AllowsMeetingGuests=false; picker.Email="another@example.com"; picker.AddEmailCommand.Execute(null); Assert.Single(picker.Emails); Assert.NotEmpty(picker.Error);
    }
    [AvaloniaFact]
    public async Task VacationRange_UsesServerDaysAndRefusesMissingPrerequisites()
    {
        var (vm,api,_,_) = Create();
        api.VacationReadinessAsync(Arg.Any<Guid>(),Arg.Any<DateOnly>(),Arg.Any<CancellationToken>()).Returns(new VacationReadiness(false,"Assign a position first."));
        await vm.PreviewVacationCommand.ExecuteAsync(null); Assert.False(vm.VacationReady); Assert.Contains("position",vm.VacationInstructions);
        await api.DidNotReceive().PreviewVacationRangeAsync(Arg.Any<Guid>(),Arg.Any<DateOnly>(),Arg.Any<DateOnly>(),Arg.Any<string>(),Arg.Any<CancellationToken>());
        api.VacationReadinessAsync(Arg.Any<Guid>(),Arg.Any<DateOnly>(),Arg.Any<CancellationToken>()).Returns(new VacationReadiness(true,null));
        var start = new DateOnly(2027,1,3); var end = start.AddDays(4); vm.VacationStart = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue)); vm.VacationEnd = new DateTimeOffset(end.ToDateTime(TimeOnly.MinValue));
        api.PreviewVacationRangeAsync(Arg.Any<Guid>(),Arg.Any<DateOnly>(),Arg.Any<DateOnly>(),Arg.Any<string>(),Arg.Any<CancellationToken>()).Returns(new VacationPreview(new() { new(start,DateTime.UtcNow,DateTime.UtcNow.AddHours(8),1),new(end,DateTime.UtcNow,DateTime.UtcNow.AddHours(8),1) },"review",new() { new(2027,19,0,0) }));
        await vm.PreviewVacationCommand.ExecuteAsync(null); Assert.Equal("2",vm.VacationDays); Assert.Equal(2027,vm.VacationBalanceYear);
        await api.Received().PreviewVacationRangeAsync(vm.Chat.CurrentUser.Id,start,end,"",Arg.Any<CancellationToken>());
        vm.ConfirmActionAsync=(_,_)=>Task.FromResult(false); await vm.SubmitVacationCommand.ExecuteAsync(null);
        await api.DidNotReceive().SubmitVacationRangeAsync(Arg.Any<Guid>(),Arg.Any<DateOnly>(),Arg.Any<DateOnly>(),Arg.Any<string>(),Arg.Any<string>(),Arg.Any<CancellationToken>()); await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task VacationRange_IgnoresStalePreviewAfterDateChange()
    {
        var (vm,api,_,_) = Create(); api.VacationReadinessAsync(Arg.Any<Guid>(),Arg.Any<DateOnly>(),Arg.Any<CancellationToken>()).Returns(new VacationReadiness(true,null));
        var pending = new TaskCompletionSource<VacationPreview>();
        api.PreviewVacationRangeAsync(Arg.Any<Guid>(),Arg.Any<DateOnly>(),Arg.Any<DateOnly>(),Arg.Any<string>(),Arg.Any<CancellationToken>()).Returns(pending.Task);
        var task = vm.PreviewVacationCommand.ExecuteAsync(null); vm.VacationEnd=vm.VacationStart!.Value.AddDays(-1); pending.SetResult(new(new(),"stale",new())); await task;
        Assert.Null(vm.VacationReview); Assert.Equal("0",vm.VacationDays); await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task MeetingPresence_FollowsActualJoinShareAndNativeStopAndRestoresUserStatus()
    {
        var (vm,api,chat,media) = Create(); chat.SetStatusAsync(Arg.Any<UserStatus>()).Returns(true); vm.Chat.CurrentUserStatus=UserStatus.Busy;
        vm.MeetingCode="meeting"; api.MeetingAsync("meeting",Arg.Any<CancellationToken>()).Returns(new MeetingDetail(Guid.NewGuid(),"meeting","Planning",true,"",1,true)); api.MeetingTokenAsync("meeting",Arg.Any<CancellationToken>()).Returns(new MediaToken("wss://test.invalid","token"));
        await vm.JoinMeetingCommand.ExecuteAsync(null); Assert.Equal(UserStatus.InMeeting,vm.Chat.CurrentUserStatus);
        vm.ScreenEnabled=true; Assert.Equal(UserStatus.Presenting,vm.Chat.CurrentUserStatus);
        media.CaptureStopped += Raise.Event<Action<string>>("screen"); Dispatcher.UIThread.RunJobs(); Assert.Equal(UserStatus.InMeeting,vm.Chat.CurrentUserStatus);
        await vm.LeaveCommand.ExecuteAsync(null); Assert.Equal(UserStatus.Busy,vm.Chat.CurrentUserStatus); await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task CallerMessage_ExpandsChatAndQuietStatusesSuppressRingingPolicy()
    {
        var (vm,api,_,_) = Create(); var peer=Guid.NewGuid(); vm.Chat.SelectedRecipient=new RecipientItemViewModel { ReceiverId=peer,ReceiverName="Caller" }; vm.CallingEnabled=true;
        api.CreateCallAsync(peer,false,Arg.Any<CancellationToken>()).Returns(new CallSession("call","audio",new(){peer},null)); api.CallTokenAsync("call",Arg.Any<CancellationToken>()).Returns(new MediaToken("wss://test.invalid","token"));
        await vm.StartVoiceCommand.ExecuteAsync(null);
        Assert.True(vm.HandleCallMessage(new ChatMessage { Sender=peer,Receiver=vm.Chat.CurrentUser.Id,Text="Hello" })); Assert.True(vm.ShowCallChat);
        vm.ShowCallChat=false; Assert.False(vm.HandleCallMessage(new ChatMessage { Sender=Guid.NewGuid(),Text="Unrelated" })); Assert.False(vm.ShowCallChat);
        vm.Chat.CurrentUserStatus=UserStatus.DoNotDisturb; Assert.True(vm.IsQuietIncoming); vm.Chat.CurrentUserStatus=UserStatus.Online; Assert.False(vm.IsQuietIncoming); await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task PolicyEdit_PreservesIdentityRevisionAndRulesAndRequiresReview()
    {
        var api=Substitute.For<ICollaborationApi>(); var id=Guid.NewGuid(); var policy=new VacationPolicyEditor { Id=id,CountryId=1,Version=7,BaseDays=21 };
        var rule=JsonSerializer.SerializeToElement(new { id=Guid.NewGuid(),policyId=id,additionalDays=3 });
        api.PoliciesAsync(1,Arg.Any<CancellationToken>()).Returns(new PolicyCatalog(new(){policy},new(){rule})); api.SchedulesAsync(1,Arg.Any<CancellationToken>()).Returns(new ScheduleCatalog(new()));
        var vm=new WorkplaceManagementViewModel(api) { ConfirmAsync=(_,_)=>Task.FromResult(false) }; vm.Country=new(1,"Egypt"); vm.Policy=policy; vm.BaseDays=25; vm.PolicyFrom=new DateTimeOffset(2026,11,1,0,0,0,TimeSpan.Zero);
        await vm.SavePolicyCommand.ExecuteAsync(null); await api.DidNotReceive().SavePolicyAsync(Arg.Any<VacationPolicyEditor>(),Arg.Any<IReadOnlyList<JsonElement>>(),Arg.Any<CancellationToken>());
        vm.ConfirmAsync=(_,details)=> { Assert.Contains("Taken leave and reservations are retained",details); return Task.FromResult(true); }; await vm.SavePolicyCommand.ExecuteAsync(null);
        await api.Received(1).SavePolicyAsync(Arg.Is<VacationPolicyEditor>(x=>x.Id==id&&x.Version==7&&x.BaseDays==25&&x.EffectiveTo==new DateOnly(2027,1,1)),Arg.Is<IReadOnlyList<JsonElement>>(x=>x.Count==1&&x[0].GetProperty("policyId").GetGuid()==id),Arg.Any<CancellationToken>());
        Assert.Equal(21,policy.BaseDays);
    }
    [AvaloniaFact]
    public async Task NativeForms_RenderReadonlyVacationDaysAndManagementPrerequisites()
    {
        var (vm,_,_,_)=Create(); var view=new WorkspaceView { DataContext=vm, ShowReviewAsync=(_,_,_)=>Task.FromResult(true) }; var owner=new Window { Content=view,Width=1000,Height=800 }; owner.Show(); vm.ShowVacationEditor=true; Dispatcher.UIThread.RunJobs();
        var window=Assert.Single(view.EditorWindows); Assert.True(window.GetVisualDescendants().OfType<TextBox>().Single(x=>Avalonia.Automation.AutomationProperties.GetName(x)=="Vacation working days").IsReadOnly);
        await window.CloseAnimatedAsync(); vm.Attendees.People.Add(new(new(Guid.NewGuid(),"Alice Example"))); vm.Attendees.People.Add(new(new(Guid.NewGuid(),"Bob Example")) { Selected=true });
        var picker=new AttendeePickerWindow(vm.Attendees,true); picker.Show(owner); Dispatcher.UIThread.RunJobs();
        if (Environment.GetEnvironmentVariable("FIREUSER_TEST_ARTIFACTS") is {} artifacts) { Directory.CreateDirectory(artifacts); using var frame = picker.CaptureRenderedFrame(); Assert.NotNull(frame); using var file = File.Create(Path.Combine(artifacts,"workplace-attendee-picker.png")); frame.Save(file,new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
        Assert.Contains(picker.GetVisualDescendants().OfType<TextBlock>(),x=>x.Text=="Selected people"); picker.Close();
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(),x=>x.Content?.ToString()=="Workplace settings"); owner.Close(); await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task VacationNotification_OpensTargetedRequestAndReviewsBeforeDecision()
    {
        var (vm,api,_,_) = Create(); var employee=Guid.NewGuid();var request=new VacationRequest(Guid.NewGuid(),employee,0,"Family leave",3,7);var notice=Guid.NewGuid();
        api.CatalogAsync(Arg.Any<CancellationToken>()).Returns(new WorkplaceCatalog(new(){new(employee,"Direct report",ReportingDistance:1)}));
        api.NotificationAsync(notice,Arg.Any<CancellationToken>()).Returns(new WorkplaceNotification(notice,"Vacation approval requested","Detailed leave request","Vacation",request.Id,DateTime.UtcNow,null));
        api.VacationItemAsync(request.Id,Arg.Any<CancellationToken>()).Returns(new VacationPage(new(){request},false,1));
        var reviews=0;vm.ConfirmActionAsync=(title,body)=>{reviews++;Assert.Contains("Family leave",body);return Task.FromResult(true);};
        await vm.OpenNotificationTargetAsync($"fireuserconnect://notification/{notice}?action=Approve");
        Assert.Equal(1,reviews);await api.Received(1).DecideVacationAsync(request,"Approve",Arg.Any<CancellationToken>());
        api.ClearReceivedCalls();api.VacationItemAsync(request.Id,Arg.Any<CancellationToken>()).Returns(new VacationPage(new(){request with{State=1}},false,1));
        await vm.OpenNotificationTargetAsync($"fireuserconnect://notification/{notice}?action=Reject");
        await api.DidNotReceive().DecideVacationAsync(Arg.Any<VacationRequest>(),Arg.Any<string>(),Arg.Any<CancellationToken>());Assert.Contains("no longer available",vm.Feedback);await vm.DisposeAsync();
    }
    [AvaloniaFact]
    public void ApprovalToast_HasSeparateActionsAndDispatchesOnlyOnce()
    {
        var owner=new Window();owner.Show();var actions=new List<string>();var opened=0;var presenter=new DesktopToastPresenter(owner,()=>true);
        presenter.Show("Vacation approval requested","Full request details",()=>opened++,decide:actions.Add);Dispatcher.UIThread.RunJobs();
        var toast=Assert.Single(presenter.ActiveWindows);var approve=toast.GetVisualDescendants().OfType<Button>().Single(x=>Equals(x.Content,"Approve"));
        approve.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));approve.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("Approve",Assert.Single(actions));Assert.Equal(0,opened);owner.Close();
    }
    [AvaloniaFact]
    public async Task UnassignPosition_RequiresReviewAndUsesFreshOrganizationRevision()
    {
        var api=Substitute.For<ICollaborationApi>();var user=Guid.NewGuid();var assignment=new AssignmentOption(Guid.NewGuid(),user,Guid.NewGuid(),"Engineer",DateTime.UtcNow.AddDays(-3),null,false);
        api.AssignmentsAsync(user,Arg.Any<CancellationToken>()).Returns(new List<AssignmentOption>{assignment});api.OrganizationRevisionAsync(Arg.Any<CancellationToken>()).Returns(new OrganizationRevision(12));
        var vm=new WorkplaceManagementViewModel(api){ConfirmAsync=(_,_)=>Task.FromResult(false)};vm.Person=new(user,"Employee");vm.Assignment=assignment;
        await vm.UnassignCommand.ExecuteAsync(null);await api.DidNotReceive().EndAssignmentAsync(Arg.Any<Guid>(),Arg.Any<long>(),Arg.Any<CancellationToken>());
        vm.ConfirmAsync=(_,details)=>{Assert.Contains("Engineer",details);return Task.FromResult(true);};await vm.UnassignCommand.ExecuteAsync(null);await api.Received(1).EndAssignmentAsync(assignment.Id,12,Arg.Any<CancellationToken>());
    }
    [AvaloniaFact]
    public async Task CalendarReview_CancellationPreventsSaveAndEmailRetryKeepsEntryIdentity()
    {
        var (vm,api,_,_)=Create();vm.EventTitle="Team meeting";vm.Attendees.Emails.Add("guest@example.com");var id=Guid.NewGuid();
        api.ReviewAsync(Arg.Any<CalendarWrite>(),Arg.Any<CancellationToken>()).Returns(new CalendarReview(new(),"review"));api.SaveCalendarAsync(Arg.Any<CalendarWrite>(),Arg.Any<CancellationToken>()).Returns(new SavedCalendar(id,2));
        vm.ConfirmActionAsync=(_,_)=>Task.FromResult(false);await vm.ReviewEventCommand.ExecuteAsync(null);await vm.SaveEventCommand.ExecuteAsync(null);await api.DidNotReceive().SaveCalendarAsync(Arg.Any<CalendarWrite>(),Arg.Any<CancellationToken>());
        vm.ConfirmActionAsync=(_,_)=>Task.FromResult(true);api.ConfigureMeetingAsync(id,Arg.Any<CancellationToken>()).Returns(new MeetingCredentials(id,"code","password","https://identity.invalid/meeting/code"));
        var attempts=0;api.InviteMeetingEmailsAsync("code",Arg.Any<IReadOnlyList<string>>(),Arg.Any<CancellationToken>()).Returns(_=>++attempts==1?Task.FromException(new HttpRequestException("SMTP unavailable")):Task.CompletedTask);
        await vm.ReviewEventCommand.ExecuteAsync(null);await vm.SaveEventCommand.ExecuteAsync(null);Assert.Contains("incomplete",vm.Feedback);
        await vm.ReviewEventCommand.ExecuteAsync(null);await vm.SaveEventCommand.ExecuteAsync(null);
        await api.Received(1).SaveCalendarAsync(Arg.Is<CalendarWrite>(x=>x.Id==null),Arg.Any<CancellationToken>());await api.Received(1).SaveCalendarAsync(Arg.Is<CalendarWrite>(x=>x.Id==id&&x.Version==2),Arg.Any<CancellationToken>());await api.Received(1).ConfigureMeetingAsync(id,Arg.Any<CancellationToken>());Assert.Equal(2,attempts);await vm.DisposeAsync();
    }

    [Theory]
    [InlineData(0,8,16,0)] [InlineData(1,14,22,0)] [InlineData(2,22,6,1)]
    public void WorkingSchedulePresets_KeepDayMiddayAndOvernightBoundaries(int preset,int start,int end,int offset)
    {
        var vm=new WorkplaceManagementViewModel(Substitute.For<ICollaborationApi>());vm.ShiftPreset=preset;
        Assert.Equal(TimeSpan.FromHours(start),vm.ShiftStart);Assert.Equal(TimeSpan.FromHours(end),vm.ShiftEnd);Assert.Equal(offset,vm.EndDayOffset);
    }
    [AvaloniaFact]
    public async Task Management_ReviewsOrganizationScheduleAndAllowsUnitToUseDefault()
    {
        var api=Substitute.For<ICollaborationApi>();var org=Guid.NewGuid();var template=new WorkScheduleEditor{Id=Guid.NewGuid(),OrganizationId=org,CountryId=1,Name="Night template",EffectiveFrom=DateOnly.FromDateTime(DateTime.Today).AddDays(-1)};
        api.PoliciesAsync(1,Arg.Any<CancellationToken>()).Returns(new PolicyCatalog(new(),new()));api.SchedulesAsync(1,Arg.Any<CancellationToken>()).Returns(new ScheduleCatalog(new(){template}));api.UnitsAsync(org,Arg.Any<int>(),Arg.Any<CancellationToken>()).Returns(new DirectoryPage<UnitOption>(new(),1,200,false,9));
        var vm=new WorkplaceManagementViewModel(api){ConfirmAsync=(_,_)=>Task.FromResult(true)};vm.Countries.Add(new(1,"Egypt"));vm.Organization=new(org,"Office",1);vm.NewScheduleCommand.Execute(null);vm.ShiftPreset=2;
        await vm.SaveScheduleCommand.ExecuteAsync(null);await api.Received(1).SaveScheduleAsync(Arg.Is<WorkScheduleEditor>(x=>x.OrganizationId==org&&x.StartTime==new TimeOnly(22,0)&&x.EndTime==new TimeOnly(6,0)&&x.EndDayOffset==1&&!x.IsDefault),Arg.Any<CancellationToken>());
        vm.UnitName="Engineering";vm.UnitCode="ENG";vm.UnitType=2;vm.UnitSchedule=vm.UnitSchedules.Single();await vm.SaveUnitCommand.ExecuteAsync(null);
        await api.Received(1).SaveUnitAsync(Arg.Is<UnitOption>(x=>x.OrganizationId==org&&x.Code=="ENG"&&x.Type==2),template.Id,9,true,Arg.Any<CancellationToken>());
        vm.UnitSchedule=null;await vm.SaveUnitCommand.ExecuteAsync(null);await api.Received(1).SaveUnitAsync(Arg.Any<UnitOption>(),null,9,true,Arg.Any<CancellationToken>());
        vm.UnitSchedule=new WorkScheduleEditor{OrganizationId=Guid.NewGuid(),CountryId=1};await vm.SaveUnitCommand.ExecuteAsync(null);Assert.Contains("belonging to this organization",vm.Feedback);
        await api.Received(2).SaveUnitAsync(Arg.Any<UnitOption>(),Arg.Any<Guid?>(),Arg.Any<long>(),Arg.Any<bool>(),Arg.Any<CancellationToken>());
    }

    [AvaloniaTheory]
    [InlineData(true)] [InlineData(false)]
    public async Task IncomingMeetingJoinIsSeparateFromItsInvitationResponse(bool accept)
    {
        var (vm,api,chat,media)=Create();var invitation=Guid.NewGuid();var call=new IncomingCall("meeting-id",Guid.NewGuid(),"Host","video",1,"code","Planning",invitation);
        api.MeetingAsync("code",Arg.Any<CancellationToken>()).Returns(new MeetingDetail(Guid.NewGuid(),"code","Planning",true,"",1,false));api.MeetingTokenAsync("code",Arg.Any<CancellationToken>()).Returns(new MediaToken("wss://test.invalid","token"));
        chat.IncomingCallReceived+=Raise.Event<Action<IncomingCall>>(call);Dispatcher.UIThread.RunJobs();Assert.Equal(call,vm.Incoming);
        if(accept) await vm.AcceptCommand.ExecuteAsync(null); else await vm.DeclineCommand.ExecuteAsync(null);
        if(accept)await chat.DidNotReceive().RespondToMeetingInvitationAsync("code",invitation,true);else await chat.Received(1).RespondToMeetingInvitationAsync("code",invitation,false);await api.DidNotReceive().DeclineCallAsync(Arg.Any<string>(),Arg.Any<CancellationToken>());Assert.Null(vm.Incoming);
        if(accept) { Assert.True(vm.InCall);await api.Received(1).MeetingTokenAsync("code",Arg.Any<CancellationToken>()); }
        else { Assert.False(vm.InCall);await media.DidNotReceive().ConnectAsync(Arg.Any<MediaToken>(),Arg.Any<CancellationToken>()); }
        await vm.DisposeAsync();
    }

}
