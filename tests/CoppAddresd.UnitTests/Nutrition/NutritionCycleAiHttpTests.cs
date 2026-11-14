using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using CoppAddresd.Api.Authorization;
using CoppAddresd.Api.Controllers;
using CoppAddresd.Api.Middleware;
using CoppAddresd.Application.Features.Nutrition.Commands;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Exceptions;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CoppAddresd.UnitTests.Nutrition;

public sealed class NutritionCycleAiHttpTests
{
    private sealed class Auth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var audience = Request.Headers["X-Test-Audience"].ToString();
            return Task.FromResult(audience.Length == 0 ? AuthenticateResult.NoResult()
                : AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("aud", audience), new Claim(ClaimTypes.NameIdentifier, "142e6327-cc9f-474b-97f2-bb324130ec87")], Scheme.Name)), Scheme.Name)));
        }
    }

    [Fact]
    public async Task RutaReal_ERP_AlcanceYPermisos_AvisoManualYBindingDeRevisionIdioma()
    {
        var id = Guid.NewGuid(); var foreign = Guid.NewGuid(); var patient = Guid.NewGuid();
        var guard = Substitute.For<IPatientScopeGuard>();
        guard.EnsureResourceAsync(PatientResource.Cycle, id, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(patient);
        guard.EnsureResourceAsync(PatientResource.Cycle, foreign, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromException<Guid>(new NotFoundException("Ciclo no encontrado.")));
        var mediator = Substitute.For<IMediator>(); GenerateCycleAiDraft? sent = null;
        mediator.Send(Arg.Any<GenerateCycleAiDraft>(), Arg.Any<CancellationToken>()).Returns(call =>
        { sent = call.Arg<GenerateCycleAiDraft>(); return new GenerateCycleAiDraftResult("unavailable", sent.Revision, null, "ai_timeout", "Continue the manual review."); });
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        var services = builder.Services;
        services.AddLogging(); services.AddSingleton(guard); services.AddSingleton(mediator);
        services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, Auth>("test", _ => { });
        services.AddAuthorization(o => o.AddPolicy(NutritionPolicies.Staff, NutritionPolicies.Configure));
        services.AddControllers().AddApplicationPart(typeof(NutritionCycleReviewsController).Assembly);
        await using var app = builder.Build();
        app.UseMiddleware<ExceptionHandlingMiddleware>(); app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.MapControllers();
        await app.StartAsync();
        using var http = app.GetTestClient(); var path = $"/api/v1/nutrition/cycles/{id}/review/ai-draft";
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync(path, new { revision = 7, language = "en" })).StatusCode); Assert.Null(sent);
        http.DefaultRequestHeaders.Add("X-Test-Audience", "app");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync(path, new { revision = 7, language = "en" })).StatusCode); Assert.Null(sent);
        http.DefaultRequestHeaders.Remove("X-Test-Audience"); http.DefaultRequestHeaders.Add("X-Test-Audience", "erp");
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsJsonAsync($"/api/v1/nutrition/cycles/{foreign}/review/ai-draft", new { revision = 0 })).StatusCode); Assert.Null(sent);
        var response = await http.PostAsJsonAsync(path, new { revision = 7, language = "en" }); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new GenerateCycleAiDraft(id, 7, "en"), sent);
        var result = await response.Content.ReadFromJsonAsync<GenerateCycleAiDraftResult>(); Assert.Equal("unavailable", result!.Status); Assert.Equal("ai_timeout", result.WarningCode); Assert.Null(result.AiDraft);
        foreach (var permission in new[] { "Nutrition.Approve", "Nutrition.NotesView", "Nutrition.RecordsView" })
            await guard.Received().EnsureResourceAsync(PatientResource.Cycle, id, permission, Arg.Any<CancellationToken>());
    }
}
