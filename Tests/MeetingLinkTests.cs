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

public sealed class MeetingLinkTests
{
    [Theory]
    [InlineData("fireuserconnect://meeting/AbC_12-xy")]
    [InlineData("FIREUSERCONNECT://MEETING/AbC_12-xy")]
    public void MeetingProtocol_ParsesPublicLocatorWithoutChangingCase(string link) =>
        Assert.Equal("AbC_12-xy", MeetingLink.Parse(link));

    [Theory]
    [InlineData("https://meeting/code")]
    [InlineData("fireuserconnect://chat/code")]
    [InlineData("fireuserconnect://meeting/")]
    [InlineData("fireuserconnect://meeting/code/")]
    [InlineData("fireuserconnect://meeting/code?token=secret")]
    [InlineData("fireuserconnect://meeting/code#fragment")]
    [InlineData("fireuserconnect://user@meeting/code")]
    [InlineData("fireuserconnect://meeting:123/code")]
    [InlineData("fireuserconnect://meeting/code%2Fother")]
    [InlineData("fireuserconnect://meeting/code/../other")]
    [InlineData("fireuserconnect://meeting/abcdefghijklmnopqrstuvwxyz1234567")]
    public void MeetingProtocol_RejectsNonMeetingRoutesAndCredentialParameters(string link) =>
        Assert.Null(MeetingLink.Parse(link));

    [Fact]
    public void InvalidMeetingParameters_CannotFallBackToChatNavigation() =>
        Assert.Null(App.ParseDeepLinkUserId("fireuserconnect://meeting/code?userId=11111111-1111-1111-1111-111111111111"));

    [AvaloniaFact]
    public async Task MeetingLink_SurvivesSignInAndReusesOpenWorkspaceWithoutStartingMedia()
    {
        var auth = Substitute.For<IAuthService>();
        auth.CurrentUser.Returns(new UserData { Id = Guid.NewGuid() });
        auth.LoginAsync(Arg.Any<string>(), Arg.Any<string>()).Returns((true, (string?)null));
        var config = Substitute.For<IConfigurationService>(); config.ServerUrl.Returns("https://api.invalid");
        var api = Substitute.For<ICollaborationApi>();
        api.CalendarAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(new List<CalendarItem>());
        api.AnnouncementsAsync(Arg.Any<CancellationToken>()).Returns(new List<Announcement>());
        api.CatalogAsync(Arg.Any<CancellationToken>()).Returns(new WorkplaceCatalog(new()));
        var chat = Substitute.For<IChatService>(); chat.GetRecipientsAsync().Returns(new List<RecipientDto>());
        var media = Substitute.For<INativeMediaSession>();
        var main = new MainViewModel(config, auth, chat, Substitute.For<IUserService>(),
            Substitute.For<IImageService>(), Substitute.For<IFileService>(), Substitute.For<INotificationService>(), api, media);
        var login = Assert.IsType<LoginViewModel>(main.CurrentView);
        main.OpenMeeting("first-meeting");
        Assert.Same(login, main.CurrentView);
        login.Email = "person@example.invalid"; login.Password = "test-password";
        await login.LoginCommand.ExecuteAsync(null);
        var workspace = Assert.IsType<WorkspaceViewModel>(main.CurrentView);
        Assert.Equal("first-meeting", workspace.MeetingCode);
        Assert.Equal(1, workspace.SelectedWorkspaceTab);
        workspace.ReduceMotion = true;
        var view = new WorkspaceView { DataContext = workspace };
        var window = new Window { Width = 1200, Height = 850, Content = view };
        window.Show(); Dispatcher.UIThread.RunJobs();
        var tabs = view.GetVisualDescendants().OfType<TabControl>().Single();
        Assert.Equal(1, tabs.SelectedIndex);
        tabs.SelectedIndex = 0;
        main.OpenMeeting("second-meeting"); Dispatcher.UIThread.RunJobs();
        Assert.Same(workspace, main.CurrentView);
        Assert.Equal("second-meeting", workspace.MeetingCode);
        Assert.Equal(1, tabs.SelectedIndex);
        Assert.False(workspace.InCall);
        await api.DidNotReceive().MeetingTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await media.DidNotReceive().ConnectAsync(Arg.Any<MediaToken>(), Arg.Any<CancellationToken>());
        window.Close(); await main.ShutdownAsync();
    }
}
