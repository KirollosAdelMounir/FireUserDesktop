using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NSubstitute;
using UserIdentityDesktop.Core;
using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.Data;
using UserIdentityDesktop.Service;
using Xunit;
namespace UserIdentityDesktop.Tests;
public sealed class WorkplaceApiUpdateTests
{
    private sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> handle) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) => handle(request); }
    private sealed record Sent(HttpMethod Method,string Path,JsonElement? Body,string? Bearer);
    private static (CollaborationApi Api,List<Sent> Sent) Create()
    {
        var sent=new List<Sent>(); var auth=Substitute.For<IAuthService>(); var config=Substitute.For<IConfigurationService>(); config.ServerUrl.Returns("https://identity.invalid/");
        auth.CreateAuthenticatedClient().Returns(_=>
        {
            var client=new HttpClient(new Handler(async request=>
            {
                JsonElement? content=request.Content==null?null:JsonDocument.Parse(await request.Content.ReadAsStringAsync()).RootElement.Clone();
                sent.Add(new(request.Method,request.RequestUri!.PathAndQuery,content,request.Headers.Authorization?.Parameter));
                object body=request.RequestUri.AbsolutePath.EndsWith("/preview") ? new { shifts=Array.Empty<object>(),previewToken="preview",balances=Array.Empty<object>() } : request.RequestUri.AbsolutePath.EndsWith("/group") ? new { version=17 } : request.RequestUri.AbsolutePath.EndsWith("/resolve") ? new[] { new MeetingAudiencePerson(Guid.Empty,"Employee") } : new { success=true };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent(JsonSerializer.Serialize(new { status=true,data=body },ApiPayload.Json),Encoding.UTF8,"application/json") };
            })); client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer","session");return client;
        }); return(new CollaborationApi(auth,config),sent);
    }
    [Fact]
    public async Task AppearanceRequestsUseTheAccountSessionAndPreservePartialSettings()
    {
        var (api,sent)=Create();await api.AppearanceAsync(TestContext.Current.CancellationToken);
        await api.SaveAppearanceAsync(new(null,27),TestContext.Current.CancellationToken);
        Assert.Equal(HttpMethod.Get,sent[0].Method);Assert.Null(sent[0].Body);
        Assert.Equal(HttpMethod.Post,sent[1].Method);Assert.Equal("/api/Workplace/appearance/preferences",sent[1].Path);
        Assert.All(sent,item=>Assert.Equal("session",item.Bearer));
        var body=sent[1].Body!.Value;Assert.Equal(27,body.GetProperty("brightness").GetInt32());
        Assert.True(!body.TryGetProperty("twelveHour",out var clock)||clock.ValueKind==JsonValueKind.Null);
        Assert.False(body.TryGetProperty("userId",out _));
    }
    [Fact]
    public async Task VacationRange_SendsInclusiveEndDateAndServerCalculatedDaysWithReviewedToken()
    {
        var (api,sent)=Create();var user=Guid.NewGuid();var start=new DateOnly(2026,12,30);var end=new DateOnly(2027,1,4);
        await api.PreviewVacationRangeAsync(user,start,end,"Family",TestContext.Current.CancellationToken); await api.SubmitVacationRangeAsync(user,start,end,"Family","reviewed",TestContext.Current.CancellationToken);
        Assert.Equal("/api/Workplace/vacations/preview",sent[0].Path); Assert.Equal("/api/Workplace/vacations",sent[1].Path);
        foreach(var request in sent) { var body=request.Body!.Value;Assert.Equal("2026-12-30",body.GetProperty("startDate").GetString());Assert.Equal("2027-01-04",body.GetProperty("endDate").GetString());Assert.Equal(0,body.GetProperty("days").GetInt32());Assert.Equal(user,body.GetProperty("userId").GetGuid());Assert.Equal("session",request.Bearer); }
        Assert.Equal("reviewed",sent[1].Body!.Value.GetProperty("previewToken").GetString());
    }
    [Fact]
    public async Task UnitAndAssignmentWrites_UseExpectedRevisionAndAllowDefaultSchedule()
    {
        var (api,sent)=Create();var id=Guid.NewGuid();var unit=new UnitOption(id,Guid.NewGuid(),null,2,"ENG","Engineering",null,true);
        Assert.Equal(17,(await api.OrganizationRevisionAsync(TestContext.Current.CancellationToken)).Revision);
        await api.SaveUnitAsync(unit,null,17,false,TestContext.Current.CancellationToken);await api.EndAssignmentAsync(id,17,TestContext.Current.CancellationToken);
        Assert.Equal(HttpMethod.Put,sent[1].Method);Assert.Equal($"/api/Organization/units/{id}",sent[1].Path);var body=sent[1].Body!.Value;Assert.Equal(17,body.GetProperty("expectedRevision").GetInt64());Assert.True(body.GetProperty("updateWorkingSchedule").GetBoolean());Assert.Equal(JsonValueKind.Null,body.GetProperty("workingScheduleId").ValueKind);
        Assert.Equal(HttpMethod.Delete,sent[2].Method);Assert.Contains($"/api/Organization/assignments/{id}?endUtc=",sent[2].Path);Assert.EndsWith("expectedRevision=17",sent[2].Path);
    }
    [Fact]
    public async Task MeetingUnitsAndGuestEmail_UseAuthorizedRoutesAndEncodedLocator()
    {
        var (api,sent)=Create();var id=Guid.NewGuid();var members=await api.ResolveMeetingUnitsAsync(new[]{id},TestContext.Current.CancellationToken);await api.InviteMeetingEmailsAsync("meeting/code",new[]{"guest@example.com"},TestContext.Current.CancellationToken);
        Assert.Equal("Employee",Assert.Single(members).Name);Assert.Equal(id,sent[0].Body!.Value[0].GetGuid());Assert.Contains("Meetings/meeting%2Fcode/invitations/email",sent[1].Path);Assert.Equal("guest@example.com",sent[1].Body!.Value.GetProperty("emails")[0].GetString());
        await Assert.ThrowsAsync<InvalidOperationException>(()=>api.InviteMeetingAudienceAsync("other",new[]{id},Array.Empty<Guid>(),TestContext.Current.CancellationToken));Assert.Equal(2,sent.Count);
    }
    [Fact]
    public async Task PolicySave_PreservesIdentityVersionAndRulePayload()
    {
        var (api,sent)=Create();var id=Guid.NewGuid();var rule=JsonSerializer.SerializeToElement(new{id=Guid.NewGuid(),policyId=id,additionalDays=2});
        await api.SavePolicyAsync(new VacationPolicyEditor {Id=id,Version=5,CountryId=1,BaseDays=25},new[]{rule},TestContext.Current.CancellationToken);var body=Assert.Single(sent).Body!.Value;
        Assert.Equal(id,body.GetProperty("policy").GetProperty("id").GetGuid());Assert.Equal(5,body.GetProperty("policy").GetProperty("version").GetInt64());Assert.Equal(2,body.GetProperty("rules")[0].GetProperty("additionalDays").GetInt32());Assert.False(body.TryGetProperty("resetBalance",out _));
    }
}
