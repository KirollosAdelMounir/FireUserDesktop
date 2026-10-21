using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using NSubstitute;
using UserIdentityDesktop.Core;
using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.Data;
using UserIdentityDesktop.Service;
using UserIdentityDesktop.Service.Media;
using Xunit;

namespace UserIdentityDesktop.Tests;

public sealed class ApiContractTests
{
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request);
    }
    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(JsonSerializer.Serialize(value, ApiPayload.Json), Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Payload_UnwrapsOneLevel_AndPreservesNativeJson()
    {
        using var plain = Json(new { token = "value", serverUrl = "wss://media.test" });
        using var wrapped = Json(new { status = true, data = new { token = "value", serverUrl = "wss://media.test" } });
        Assert.Equal(await ApiPayload.ReadAsync<MediaToken>(plain, TestContext.Current.CancellationToken), await ApiPayload.ReadAsync<MediaToken>(wrapped, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Payload_FailureIncludesPublicErrorAndTrace_WithoutRawPayload()
    {
        using var response = Json(new { status = false, errorText = "Not allowed", traceId = "trace-123", token = "secret" }, HttpStatusCode.Forbidden);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => ApiPayload.ReadAsync<object>(response, TestContext.Current.CancellationToken));
        Assert.Contains("Not allowed", error.Message); Assert.Contains("trace-123", error.Message); Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public async Task Refresh_RotatesBothCredentials_AndNextRefreshUsesSuccessor()
    {
        var config = Substitute.For<IConfigurationService>(); config.ServerUrl.Returns("https://identity.test");
        config.SavedRefreshToken = "old";
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(claims: new[] { new Claim("nameid", Guid.NewGuid().ToString()) }, expires: DateTime.UtcNow.AddHours(1)));
        var sent = new List<string>(); var index = 0;
        using var client = new HttpClient(new Handler(async request =>
        {
            sent.Add(await request.Content!.ReadAsStringAsync());
            return Json(new { status = true, data = new { accessToken = token, refreshToken = ++index == 1 ? "successor" : "successor2" } });
        }));
        var auth = new AuthService(config, client);
        Assert.True(await auth.RefreshTokenAsync());
        Assert.Equal("successor", config.SavedRefreshToken);
        Assert.True(auth.IsAuthenticated);
        Assert.True(await auth.RefreshTokenAsync());
        Assert.Equal(new[] { "\"old\"", "\"successor\"" }, sent);
        Assert.Equal("successor2", auth.RefreshToken);
    }

    [Fact]
    public async Task CallClient_UsesBearerServerToken_AndEscapesCallIdentifier()
    {
        var config = Substitute.For<IConfigurationService>(); config.ServerUrl.Returns("https://identity.test/");
        var auth = Substitute.For<IAuthService>();
        Uri? uri = null; string? bearer = null;
        auth.CreateAuthenticatedClient().Returns(_ =>
        {
            var client = new HttpClient(new Handler(request =>
            {
                uri = request.RequestUri; bearer = request.Headers.Authorization?.Parameter;
                return Task.FromResult(Json(new { status = true, data = new { serverUrl = "wss://media.test", token = "room-token", callId = "a/b" } }));
            }));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "session-token"); return client;
        });
        var api = new CollaborationApi(auth, config);
        var result = await api.CallTokenAsync("a/b", TestContext.Current.CancellationToken);
        Assert.Equal("https://identity.test/api/Calls/a%2Fb/Token", uri!.AbsoluteUri);
        Assert.Equal("session-token", bearer); Assert.Equal("room-token", result.Token);
    }

    [Fact]
    public async Task ProfileDirectory_PreservesHierarchyAndGradeMetadataFromServer()
    {
        var user = Guid.NewGuid(); var organization = Guid.NewGuid(); var unit = Guid.NewGuid(); var requests = new List<string>();
        var config = Substitute.For<IConfigurationService>(); config.ServerUrl.Returns("https://identity.test"); var auth = Substitute.For<IAuthService>();
        auth.CreateAuthenticatedClient().Returns(_ => new HttpClient(new Handler(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(requests.Count == 1 ? Json(new { status = true, data = new { firstName = "Dina", lastName = "Nabil", email = "dina@example.test" } })
                : Json(new { status = true, data = new {
                    organization = new { id = organization, name = "Fire User", code = "FU", countryName = "Egypt", cityName = "Cairo" },
                    position = new { id = Guid.NewGuid(), title = "Engineer", unitId = unit, unitName = "Engineering", levelName = "Professional", grade = "007" },
                    unitPath = new[] { new { id = unit, organizationId = organization, type = 2, name = "Engineering", code = "ENG" } },
                    reportingPath = new[] { new { id = Guid.NewGuid(), title = "Manager", unitName = "Engineering", levelName = "Management", grade = "001" } },
                    reportingManagers = new[] { new { userId = Guid.NewGuid(), displayName = "Morgan Manager", positionTitle = "Manager" } }, reportingManagersHasMore = true
                } }));
        })));
        var profile = await new UserService(auth, config).GetUserProfileAsync(user);
        Assert.Equal(new[] { $"/api/ProfileSettings/User?userId={user}", $"/api/Organization/users/{user}" }, requests);
        Assert.Equal("Dina Nabil", profile!.FullName); var directory = profile.Organization!;
        Assert.Equal("Cairo", directory.Organization!.CityName); Assert.Equal("Egypt", directory.Organization.CountryName);
        Assert.Equal(unit, directory.Position!.UnitId); Assert.Equal("007", directory.Position.Grade);
        Assert.Equal(2, Assert.Single(directory.UnitPath).Type); Assert.Equal("001", Assert.Single(directory.ReportingPath).Grade);
        Assert.Equal("Morgan Manager", Assert.Single(directory.ReportingManagers!).DisplayName); Assert.True(directory.ReportingManagersHasMore);
    }

    [Fact]
    public void Capture_RefusesXWaylandFallbackEvenWhenDisplayExists()
    {
        Assert.Throws<InvalidOperationException>(() => NativeCapture.Input("screen", new("", "", ""), "linux", ":0", true, 1920, 1080));
        Assert.Throws<InvalidOperationException>(() => NativeCapture.Input("screen", new("", "", ""), "linux", null, true));
    }
    [Theory]
    [InlineData("windows", "microphone", "audio=Microphone (USB)")]
    [InlineData("windows", "camera", "video=Camera (USB)")]
    [InlineData("macos", "camera", "2:none")]
    [InlineData("linux", "camera", "/dev/video2")]
    public void Capture_KeepsDeviceNamesAsOneArgument(string platform, string kind, string expected)
    {
        var devices = platform == "windows" ? new MediaDevices("Microphone (USB)", "Camera (USB)", "") : new MediaDevices("default", platform == "macos" ? "2" : "/dev/video2", "");
        Assert.Equal(expected, NativeCapture.Input(kind, devices, platform, ":0", false).Last());
    }
}
