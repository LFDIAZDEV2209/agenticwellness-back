using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using CoppAddresd.Api.Authorization;
using CoppAddresd.Api.Constants;
using CoppAddresd.Api.Controllers;
using CoppAddresd.Api.Middleware;
using CoppAddresd.Application.Features.ProgramProgress.WeeklySuggestions;
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

namespace CoppAddresd.UnitTests.ProgramProgress;

public sealed class WeeklySuggestionsHttpTests
{
    private sealed class Auth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var audience = Request.Headers["X-Test-Audience"].ToString();
            return Task.FromResult(audience.Length == 0 ? AuthenticateResult.NoResult() : AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim("aud", audience),
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], Scheme.Name)), Scheme.Name)));
        }
    }
    [Fact]
    public async Task RutaReal_AudienciaPermisosAlcance_BindingESEN_YFalloOmitibleSinCache()
    {
        var patient = Guid.NewGuid(); var foreign = Guid.NewGuid(); var guard = Substitute.For<IPatientScopeGuard>();
        guard.EnsureResourceAsync(PatientResource.Enrollment, foreign, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromException<Guid>(new NotFoundException("Recurso no encontrado.")));
        var mediator = Substitute.For<IMediator>(); GenerateWeeklySuggestions? generated = null; GetWeeklySuggestions? read = null; ApplyWeeklySuggestion? applied = null;
        mediator.Send(Arg.Any<ApplyWeeklySuggestion>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            applied = call.Arg<ApplyWeeklySuggestion>();
            if (!applied.Confirmed) throw new UnprocessableEntityException("HUMAN_CONFIRMATION_REQUIRED: confirma la aplicación.");
            return new ApplyWeeklySuggestionResult("applied", applied.BriefId, applied.Index, default!);
        });
        mediator.Send(Arg.Any<GenerateWeeklySuggestions>(), Arg.Any<CancellationToken>()).Returns(call =>
        { generated = call.Arg<GenerateWeeklySuggestions>(); return new WeeklySuggestionsResult("unavailable"); });
        mediator.Send(Arg.Any<GetWeeklySuggestions>(), Arg.Any<CancellationToken>()).Returns(call =>
        { read = call.Arg<GetWeeklySuggestions>(); return new WeeklySuggestionsResult("empty"); });
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(guard); builder.Services.AddSingleton(mediator);
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, Auth>("test", _ => { });
        builder.Services.AddAuthorization(o => o.AddPolicy(NutritionPolicies.Staff, NutritionPolicies.Configure));
        builder.Services.AddControllers().AddApplicationPart(typeof(WeeklySuggestionsController).Assembly);
        await using var app = builder.Build(); app.UseMiddleware<ExceptionHandlingMiddleware>(); app.UseRouting();
        app.UseAuthentication(); app.UseAuthorization(); app.MapControllers(); await app.StartAsync();
        using var http = app.GetTestClient(); var path = $"/api/v1/program/erp/enrollments/{patient}/weeks/3/ai-suggestions";
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(path)).StatusCode);
        http.DefaultRequestHeaders.Add("X-Test-Audience", "app");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync(path, new { language = "en" })).StatusCode); Assert.Null(generated);
        http.DefaultRequestHeaders.Remove("X-Test-Audience"); http.DefaultRequestHeaders.Add("X-Test-Audience", "erp");
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/v1/program/erp/enrollments/{foreign}/weeks/3/ai-suggestions")).StatusCode); Assert.Null(read);
        var response = await http.PostAsJsonAsync(path, new { language = "en" }); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<WeeklySuggestionsResult>(); Assert.Equal("unavailable", result!.Status); Assert.Null(result.Draft);
        Assert.Equal(new GenerateWeeklySuggestions(patient, 3, "en"), generated); Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(path + "?language=es")).StatusCode); Assert.Equal(new GetWeeklySuggestions(patient, 3, "es"), read);
        foreach (var permission in new[] { "Program.Profile360View", "Program.AiView", "Program.AiManage", "Program.Edit" })
            await guard.Received().EnsureResourceAsync(PatientResource.Enrollment, patient, permission, Arg.Any<CancellationToken>());
        var brief = Guid.NewGuid(); var applyPath = path + $"/{brief}/suggestions/1/apply";
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync(applyPath, new { confirmed = true })).StatusCode);
        Assert.Equal(new ApplyWeeklySuggestion(patient, 3, brief, 1, true), applied);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await http.PostAsJsonAsync(applyPath, new { })).StatusCode);
        Assert.False(applied!.Confirmed);
    }
}
