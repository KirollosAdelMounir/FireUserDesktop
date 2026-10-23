using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using UserIdentityDesktop.Core;
using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.ViewModels;
using UserIdentityDesktop.Views;
using Xunit;
namespace UserIdentityDesktop.Tests;
public sealed class DesktopWorkspaceRefinementTests
{
    private static (WorkspaceViewModel Workspace, ChatViewModel Chat,IChatService Transport,ICollaborationApi Api,IAuthService Auth,IUserService Users,INotificationService Notices) Create()
    {
        var auth=Substitute.For<IAuthService>();auth.CurrentUser.Returns(new UserData{Id=Guid.NewGuid(),FirstName="Alex",LastName="User",Role="Admin"});var transport=Substitute.For<IChatService>();var users=Substitute.For<IUserService>();var notices=Substitute.For<INotificationService>();var api=Substitute.For<ICollaborationApi>();
        var chat=new ChatViewModel(auth,transport,users,Substitute.For<IImageService>(),Substitute.For<IFileService>(),notices);var workspace=new WorkspaceViewModel(chat,api,Substitute.For<INativeMediaSession>(),auth,transport,notices){ReduceMotion=true};return(workspace,chat,transport,api,auth,users,notices);
    }
    [AvaloniaFact]
    public async Task LateOnlineSnapshotCannotOverwriteBusyAndSelectedHeaderReceivesRealStatus()
    {
        var f=Create();var id=Guid.NewGuid();var recipient=new RecipientItemViewModel{ReceiverId=id,ReceiverName="Colleague"};var pending=new TaskCompletionSource<UserPresence?>(TaskCreationOptions.RunContinuationsAsynchronously);f.Transport.GetUserPresenceAsync(id).Returns(pending.Task);f.Chat.Recipients.Add(recipient);f.Chat.SelectedRecipient=recipient;var loading=f.Chat.LoadRecipientPresenceAsync(recipient);
        f.Transport.UserStatusChanged+=Raise.Event<Action<Guid,UserStatus>>(id,UserStatus.Busy);Dispatcher.UIThread.RunJobs();pending.SetResult(new UserPresence{UserId=id,Status=UserStatus.Online});await loading;Dispatcher.UIThread.RunJobs();Assert.Equal(UserStatus.Busy,recipient.Status);Assert.Equal("Busy",f.Chat.SelectedRecipientStatusLabel);
        f.Transport.UserStatusChanged+=Raise.Event<Action<Guid,UserStatus>>(id,UserStatus.DoNotDisturb);Dispatcher.UIThread.RunJobs();Assert.Equal("Do Not Disturb",f.Chat.SelectedRecipientStatusLabel);
        f.Chat.UnregisterEvents();await f.Workspace.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task HeaderSearchOpensChatsDirectlyUsingThemedResultsAndClosesAfterSelection()
    {
        var f=Create();var view=new PeopleSearchBox{DataContext=f.Chat};var window=new Window{Content=view,Width=600,Height=400};window.Show();var id=Guid.NewGuid();
        try
        {
            f.Workspace.SelectedWorkspaceTab=2;f.Chat.IdentitySearchText="Maya";f.Users.SearchResultReceived+=Raise.Event<Action<string,List<SystemUserSearch>>>("Maya",new List<SystemUserSearch>{new(){Id=id.ToString(),FirstName="Maya",LastName="Hassan"}});Dispatcher.UIThread.RunJobs();Assert.True(f.Chat.ShowSearchDropdown);var item=Assert.Single(f.Chat.SearchResults);Assert.Equal("Maya Hassan",item.FullName);
            f.Chat.StartChatFromSearchCommand.Execute(item);Dispatcher.UIThread.RunJobs();Assert.Equal(id,f.Chat.SelectedRecipient!.ReceiverId);Assert.Equal(0,f.Workspace.SelectedWorkspaceTab);Assert.False(f.Chat.ShowSearchDropdown);Assert.Empty(f.Chat.IdentitySearchText);
        }
        finally{window.Close();f.Chat.UnregisterEvents();await f.Workspace.DisposeAsync();}
    }
    [AvaloniaFact]
    public async Task MainHeaderCentersPeopleSearchAndRemovesTheThemeShortcut()
    {
        var f=Create();var previous=Avalonia.Application.Current!.RequestedThemeVariant;
        var shell=new MainViewModel(Substitute.For<IConfigurationService>(),f.Auth,f.Transport,f.Users,Substitute.For<IImageService>(),Substitute.For<IFileService>(),f.Notices,f.Api,Substitute.For<INativeMediaSession>()){CurrentView=f.Workspace};
        var window=new MainWindow{DataContext=shell,AllowClose=true,Width=1280,Height=820};window.Show();Dispatcher.UIThread.RunJobs();
        try
        {
            var search=Assert.Single(window.GetVisualDescendants().OfType<PeopleSearchBox>());Assert.Same(f.Chat,search.DataContext);var point=search.TranslatePoint(new Point(0,0),window)!.Value;Assert.InRange(point.X+search.Bounds.Width/2,window.ClientSize.Width/2-2,window.ClientSize.Width/2+2);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(),x=>AutomationProperties.GetName(x)=="Switch light or dark theme");
            if(Environment.GetEnvironmentVariable("FIREUSER_TEST_ARTIFACTS") is {} dir){Directory.CreateDirectory(dir);using var frame=window.CaptureRenderedFrame();Assert.NotNull(frame);using var file=File.Create(Path.Combine(dir,"desktop-header-search.png"));frame.Save(file,new PngBitmapEncoderOptions());}
        }
        finally{window.Close();Avalonia.Application.Current.RequestedThemeVariant=previous;f.Chat.UnregisterEvents();await f.Workspace.DisposeAsync();}
    }
    [AvaloniaTheory]
    [InlineData(UserStatus.Away,"Away")][InlineData(UserStatus.Busy,"Busy")][InlineData(UserStatus.DoNotDisturb,"Do Not Disturb")][InlineData(UserStatus.InMeeting,"In a meeting")][InlineData(UserStatus.Presenting,"Presenting")][InlineData(UserStatus.Offline,"Offline")]
    public async Task ConversationProfileShowsTheActualPresenceSign(UserStatus state,string label)
    {
        var f=Create();var id=Guid.NewGuid();f.Transport.GetUserPresenceAsync(id).Returns(new UserPresence{UserId=id,Status=state});var person=new RecipientItemViewModel{ReceiverId=id,ReceiverName="Maya Hassan"};f.Chat.Recipients.Add(person);f.Chat.SelectedRecipient=person;await f.Chat.LoadRecipientPresenceAsync(person);Dispatcher.UIThread.RunJobs();
        var view=new ChatView{DataContext=f.Chat};var window=new Window{Content=view,Width=1100,Height=700};window.Show();Dispatcher.UIThread.RunJobs();
        try{var header=view.FindControl<Button>("ConversationProfileButton")!;Assert.Equal(state,Assert.Single(header.GetVisualDescendants().OfType<PresenceIndicator>()).Status);Assert.Contains(header.GetVisualDescendants().OfType<TextBlock>(),x=>x.Text==label);}
        finally{window.Close();f.Chat.UnregisterEvents();await f.Workspace.DisposeAsync();}
    }
    [AvaloniaTheory][InlineData(false)][InlineData(true)]
    public async Task ProfileFooterUsesCompressedAvatarAndStatusMenuKeepsSignsWithCompactAppearance(bool dark)
    {
        var f=Create();using var image=new WriteableBitmap(new PixelSize(20,20),new Vector(96,96),Avalonia.Platform.PixelFormat.Rgba8888,Avalonia.Platform.AlphaFormat.Opaque);f.Chat.MyAvatar=image;
        var view=new ChatView{DataContext=f.Chat};var window=new Window{Content=view,Width=1150,Height=760,RequestedThemeVariant=dark?ThemeVariant.Dark:ThemeVariant.Light};window.Show();Dispatcher.UIThread.RunJobs();
        try
        {
            var avatar=view.GetVisualDescendants().OfType<Image>().Single(x=>AutomationProperties.GetName(x)=="My profile picture");Assert.Same(image,avatar.Source);f.Chat.MyAvatar=null;Assert.Null(avatar.Source);Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),x=>x.Text=="AU");
            var button=view.GetVisualDescendants().OfType<Button>().Single(x=>AutomationProperties.GetName(x)=="Account menu");button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();var menu=button.ContextMenu!;Assert.DoesNotContain(menu.Items.OfType<MenuItem>(),x=>x.Header is StackPanel);
            var appearance=menu.Items.OfType<MenuItem>().Single(x=>x.Header as string=="Appearance");Assert.Equal(180,Assert.IsType<StackPanel>(Assert.Single(appearance.Items.OfType<MenuItem>()).Header).Width);
            var statuses=menu.Items.OfType<MenuItem>().Single(x=>x.Header as string=="Availability");foreach(var option in statuses.Items.OfType<MenuItem>()){var sign=Assert.IsType<PresenceIndicator>(option.Icon);Assert.Equal(option.CommandParameter,sign.Status);}
            menu.Close();
        }
        finally{window.Close();f.Chat.UnregisterEvents();await f.Workspace.DisposeAsync();}
    }
    [AvaloniaFact]
    public async Task VacationReviewUsesRequesterNameRatherThanGuid()
    {
        var f=Create();var user=Guid.NewGuid();var person=new WorkplacePerson(user,"Maya Hassan",ReportingDistance:1);f.Workspace.CalendarPeople.Add(person);f.Workspace.People.Add(new(person));f.Workspace.SelectedVacation=new VacationRequest(Guid.NewGuid(),user,0,"Rest",3,1);string? review=null;f.Workspace.ConfirmActionAsync=(_,body)=>{review=body;return Task.FromResult(false);};await f.Workspace.DecideVacationCommand.ExecuteAsync("Approve");Assert.Contains("Maya Hassan",review);Assert.DoesNotContain(user.ToString(),review);await f.Api.DidNotReceive().DecideVacationAsync(Arg.Any<VacationRequest>(),Arg.Any<string>(),Arg.Any<CancellationToken>());f.Chat.UnregisterEvents();await f.Workspace.DisposeAsync();
    }
    [AvaloniaFact]
    public async Task NotificationCenterExcludesChatAndConversationNoticesButKeepsWorkplaceUpdates()
    {
        var f=Create();foreach(var host in new[]{"chat","conversation","message"})f.Notices.NotificationPosted+=Raise.Event<Action<DesktopNotification>>(new DesktopNotification("Message","Hi",$"fireuserconnect://{host}/{Guid.NewGuid()}",DateTime.UtcNow));Assert.Empty(f.Workspace.RecentNotifications);
        f.Notices.NotificationPosted+=Raise.Event<Action<DesktopNotification>>(new DesktopNotification("Vacation approval requested","Maya requested time off",$"fireuserconnect://notification/{Guid.NewGuid()}",DateTime.UtcNow));Assert.Single(f.Workspace.RecentNotifications);Assert.True(f.Workspace.HasRecentNotifications);f.Chat.UnregisterEvents();await f.Workspace.DisposeAsync();
    }
    [AvaloniaTheory][InlineData(false)][InlineData(true)]
    public async Task RefinedWorkspaceHasNoManagementEntryAndDisplaysCalendarMeetingNotificationStructure(bool dark)
    {
        var f=Create();var view=new WorkspaceView{DataContext=f.Workspace};var window=new Window{Content=view,Width=1280,Height=850,RequestedThemeVariant=dark?ThemeVariant.Dark:ThemeVariant.Light};window.Show();Dispatcher.UIThread.RunJobs();var tabs=view.FindControl<TabControl>("WorkspaceTabs")!;
        try
        {
            foreach(var tab in new[]{1,2,5})
            {
                tabs.SelectedIndex=tab;Dispatcher.UIThread.RunJobs();Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(),b=>b.Content?.ToString()=="Workplace settings");
                if(tab==1)Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),x=>x.Text=="Upcoming meetings");
                if(tab==2)Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),x=>x.Text=="Holidays");
                if(tab==5)Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),x=>x.Text=="Approvals, invitations and workplace updates");
                if(Environment.GetEnvironmentVariable("FIREUSER_TEST_ARTIFACTS") is {} dir){Directory.CreateDirectory(dir);using var frame=window.CaptureRenderedFrame();Assert.NotNull(frame);using var file=File.Create(Path.Combine(dir,$"workspace-refined-{tab}-{(dark?"dark":"light")}.png"));frame.Save(file,new PngBitmapEncoderOptions());}
            }
        }
        finally{window.Close();f.Chat.UnregisterEvents();await f.Workspace.DisposeAsync();}
    }
}
