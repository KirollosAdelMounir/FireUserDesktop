using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
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

public sealed class ChatProfileTests
{
    private static (WorkspaceViewModel Workspace, IChatService Chat, IUserService Users, IImageService Images) Create()
    {
        var auth = Substitute.For<IAuthService>(); auth.CurrentUser.Returns(new UserData { Id = Guid.NewGuid(), FirstName = "Kirollos", LastName = "Adel" });
        var chat = Substitute.For<IChatService>(); var users = Substitute.For<IUserService>(); var images = Substitute.For<IImageService>();
        chat.GetChatHistoryAsync(Arg.Any<Guid>(), 0).Returns(new List<ChatMessage>());
        var vm = new ChatViewModel(auth, chat, users, images, Substitute.For<IFileService>(), Substitute.For<INotificationService>());
        return (new WorkspaceViewModel(vm, Substitute.For<ICollaborationApi>(), Substitute.For<INativeMediaSession>(), auth, chat) { ReduceMotion = true }, chat, users, images);
    }
    private static EmployeeOrganization Directory()
    {
        var org = Guid.NewGuid(); var division = Guid.NewGuid(); var team = Guid.NewGuid();
        return new(new("Fire User", org, "FU", CountryName: "Egypt", CityName: "Cairo"),
            new("Software Engineer", "Product Engineering", "Fire User", Guid.NewGuid(), team, "ENG", "Professional", "7B"),
            new() { new("Product Engineering", team, "ENG", org, 2), new("Technology", division, "TECH", org, 1), new("Fire User", Guid.NewGuid(), "FU", org, 0) },
            new() { new("Engineering Manager", "Product Engineering", "Fire User", LevelName: "Management", Grade: "001"), new("Chief Technology Officer", "Technology", "Fire User") },
            new() { new("Morgan Manager", "Engineering Manager"), new("Sam Manager", "Engineering Manager") });
    }
    private static void Click(Control control)
    {
        var root = TopLevel.GetTopLevel(control)!; var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;
        root.MouseMove(point); root.MouseDown(point, MouseButton.Left); root.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
    }
    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task SearchResultClick_OpensDirectChatWithoutProfileFetch_AndClosesSearch(bool existing)
    {
        var (workspace, chat, users, images) = Create(); var vm = workspace.Chat; var user = Guid.NewGuid();
        var group = new RecipientItemViewModel { ReceiverId = user, ReceiverName = "Group", IsGroup = true, ConversationId = "group" }; vm.Recipients.Add(group);
        RecipientItemViewModel? direct = null;
        if (existing) { direct = new() { ReceiverId = user, ReceiverName = "Dina Nabil" }; vm.Recipients.Add(direct); }
        vm.SearchText = "unrelated filter"; vm.IdentitySearchText = "Dina";
        var result = new UserSearchItemViewModel(new SystemUserSearch { Id = user.ToString(), FirstName = "Dina", LastName = "Nabil" }, images); vm.SearchResults.Add(result);
        var view = new ChatView { DataContext = vm }; var window = new Window { Width = 1260, Height = 850, Content = view }; window.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            var find = view.FindControl<Button>("FindPeopleButton")!; find.Flyout!.ShowAt(find); Dispatcher.UIThread.RunJobs();
            var results = view.FindControl<ItemsControl>("PeopleSearchResults")!;
            Click(results.GetVisualDescendants().OfType<Button>().Single());
            Assert.False(find.Flyout.IsOpen); Assert.False(vm.IsProfileOverlayOpen); Assert.Equal(user, vm.SelectedRecipient!.ReceiverId); Assert.False(vm.SelectedRecipient.IsGroup);
            if (existing) Assert.Same(direct, vm.SelectedRecipient);
            Assert.Equal(2, vm.Recipients.Count); Assert.Contains(vm.SelectedRecipient, vm.FilteredRecipients);
            Assert.Empty(vm.IdentitySearchText); Assert.Empty(vm.SearchText); Assert.Empty(vm.SearchResults);
            Assert.True(view.FindControl<TextBox>("MessageInputBox")!.IsFocused);
            await users.DidNotReceive().GetUserProfileAsync(Arg.Any<Guid>());
            await chat.Received(1).GetChatHistoryAsync(user, 0);
            await chat.DidNotReceive().GetConversationHistoryAsync("group", Arg.Any<int>());
        }
        finally { window.Close(); vm.UnregisterEvents(); await workspace.DisposeAsync(); }
    }
    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task ChatHeaderAndOwnProfile_RenderOrganizationAndConnectedReportingHierarchy(bool ownProfile)
    {
        var (workspace, _, users, _) = Create(); var vm = workspace.Chat; var peer = Guid.NewGuid(); var profileId = ownProfile ? vm.CurrentUser.Id : peer;
        users.GetUserProfileAsync(profileId).Returns(new ViewUserProfileDto { FirstName = ownProfile ? "Kirollos" : "Dina", LastName = ownProfile ? "Adel" : "Nabil", Email = "colleague@example.test", Organization = Directory() });
        vm.SelectedRecipient = new() { ReceiverId = peer, ReceiverName = "Dina Nabil" };
        var view = new ChatView { DataContext = vm }; var window = new Window { Width = 1260, Height = 960, Content = view, RequestedThemeVariant = ownProfile ? ThemeVariant.Light : ThemeVariant.Dark }; window.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            if (ownProfile) await vm.OpenMyProfileCommand.ExecuteAsync(null);
            else Click(view.FindControl<Button>("ConversationProfileButton")!);
            Dispatcher.UIThread.RunJobs(); Assert.True(view.FindControl<Border>("ProfileOverlay")!.IsVisible);
            await users.Received(1).GetUserProfileAsync(profileId);
            var directory = Assert.Single(view.GetVisualDescendants().OfType<ProfileOrganizationView>()); var projection = Assert.IsType<ProfileOrganizationViewModel>(directory.DataContext);
            Assert.Equal(new[] { "Fire User", "Technology", "Product Engineering" }, projection.Departments.Select(x => x.Title));
            Assert.Equal(new[] { "Chief Technology Officer", "Engineering Manager", "Software Engineer" }, projection.Reporting.Select(x => x.Title));
            Assert.True(projection.Departments.Last().IsCurrent); Assert.True(projection.Reporting.Last().IsCurrent); Assert.Equal(2, projection.Managers.Count);
            directory.FindControl<Expander>("DepartmentHierarchy")!.IsExpanded = true; Dispatcher.UIThread.RunJobs();
            var text = directory.GetVisualDescendants().OfType<TextBlock>().Select(x => x.Text).ToArray();
            foreach (var expected in new[] { "Fire User", "Egypt · Cairo", "Software Engineer", "Professional · Grade 7B", "Morgan Manager", "Sam Manager", "Product Engineering · Management · Grade 001" }) Assert.Contains(expected, text);
            Assert.Equal(2, directory.GetVisualDescendants().OfType<Border>().Count(x => x.Classes.Contains("hierarchy-node") && x.Classes.Contains("current")));
            Save(window, ownProfile ? "profile-organization-light.png" : "profile-organization-dark.png");
            window.Width = 780; window.Height = 620; Dispatcher.UIThread.RunJobs();
            var card = view.FindControl<Border>("ProfileCard")!; Assert.True(card.Bounds.Width <= window.ClientSize.Width - 48); Assert.True(card.Bounds.Height <= window.ClientSize.Height - 48);
            vm.CloseUserProfileCommand.Execute(null); Assert.False(view.FindControl<Border>("ProfileOverlay")!.IsVisible);
        }
        finally { window.Close(); vm.UnregisterEvents(); await workspace.DisposeAsync(); }
    }
    [AvaloniaFact]
    public async Task ProfileSwitch_IgnoresPreviousUserResponse_AndCloseIgnoresPendingResponse()
    {
        var (workspace, _, users, images) = Create(); var vm = workspace.Chat;
        var first = new UserSearchItemViewModel(new SystemUserSearch { Id = Guid.NewGuid().ToString(), FirstName = "First" }, images);
        var second = new UserSearchItemViewModel(new SystemUserSearch { Id = Guid.NewGuid().ToString(), FirstName = "Second" }, images);
        var pendingFirst = new TaskCompletionSource<ViewUserProfileDto?>(); var pendingSecond = new TaskCompletionSource<ViewUserProfileDto?>();
        users.GetUserProfileAsync(first.UserId).Returns(pendingFirst.Task); users.GetUserProfileAsync(second.UserId).Returns(pendingSecond.Task);
        var openingFirst = vm.OpenUserProfileAsync(first); var openingSecond = vm.OpenUserProfileAsync(second);
        pendingFirst.SetResult(new() { Email = "first@example.test", Organization = Directory() }); await openingFirst;
        Assert.True(vm.IsLoadingProfile); Assert.Null(vm.ProfileData); Assert.Null(vm.ProfileOrganization); Assert.Equal("Second", vm.ProfileUserName);
        pendingSecond.SetResult(new() { Email = "second@example.test", Organization = Directory() }); await openingSecond;
        Assert.False(vm.IsLoadingProfile); Assert.Equal("second@example.test", vm.ProfileEmailText); Assert.Contains("Second", vm.ProfileOrganization!.Reporting.Last().Subtitle);
        var last = new TaskCompletionSource<ViewUserProfileDto?>(); users.GetUserProfileAsync(first.UserId).Returns(last.Task);
        var closing = vm.OpenUserProfileAsync(first); vm.CloseUserProfile(); last.SetResult(new() { Organization = Directory() }); await closing;
        Assert.False(vm.IsProfileOverlayOpen); Assert.Null(vm.ProfileOrganization); Assert.False(vm.IsLoadingProfile);
        vm.UnregisterEvents(); await workspace.DisposeAsync();
    }
    [Fact]
    public void OrganizationProjection_DistinguishesUnassignedVacantAndUnavailableManagers()
    {
        var unassigned = new ProfileOrganizationViewModel(new(null, null, new(), new(), new()), "Person");
        Assert.Equal("No current position assignment", unassigned.Position); Assert.False(unassigned.HasDepartments); Assert.False(unassigned.HasReporting); Assert.False(unassigned.HasManagers);
        var source = Directory(); var vacant = new ProfileOrganizationViewModel(source with { ReportingManagers = new() }, "Person");
        Assert.Equal("Reporting position is currently vacant", vacant.ManagerStatus); Assert.False(vacant.HasManagers);
        var unavailable = new ProfileOrganizationViewModel(source with { ReportingManagers = null }, "Person");
        Assert.Equal("Manager names are unavailable", unavailable.ManagerStatus);
        var truncated = new ProfileOrganizationViewModel(source with { ReportingManagersHasMore = true }, "Person"); Assert.True(truncated.ManagersHaveMore);
    }
    private static void Save(Window window, string fileName)
    {
        if (Environment.GetEnvironmentVariable("FIREUSER_TEST_ARTIFACTS") is not {} path) return;
        using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame); System.IO.Directory.CreateDirectory(path);
        using var file = File.Create(Path.Combine(path, fileName)); frame.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
