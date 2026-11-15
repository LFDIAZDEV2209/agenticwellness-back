using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using CoppAddresd.Api.Context;
using CoppAddresd.Api.Authorization;
using CoppAddresd.Api.Controllers;
using CoppAddresd.Api.Middleware;
using CoppAddresd.Application.Common.Behaviors;
using CoppAddresd.Application.Features.Media.PodcastMetadata;
using CoppAddresd.Application.Interfaces;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CoppAddresd.UnitTests.Media;

public sealed class PodcastMetadataHttpTests
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
    public async Task POST_ERP_PermisosPipelineTextoEstricto_BorradorEditableSinPublicacion()
    {
        var permissions = new HashSet<string> { "Media.Create", "Program.AiManage" }; // Sin Media.Publish ni datos de paciente.
        var current = Substitute.For<ICurrentContext>(); current.HasPermissionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => permissions.Contains(call.Arg<string>()));
        var actor = Substitute.For<IPatientScopeActor>(); actor.UserId.Returns(Guid.NewGuid());
        actor.HasPermissionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => permissions.Contains(call.Arg<string>()));
        var client = Substitute.For<IPodcastMetadataClient>(); var requests = new List<PodcastMetadataInput>(); var fail = false;
        client.SuggestAsync(Arg.Any<PodcastMetadataInput>(), Arg.Any<CancellationToken>()).Returns(call =>
        { requests.Add(call.Arg<PodcastMetadataInput>()); if (fail) throw new PodcastMetadataUnavailableException(); return PodcastMetadataTests.Ready(); });
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(current); builder.Services.AddSingleton(actor); builder.Services.AddSingleton(client);
        builder.Services.AddMediatR(o => o.RegisterServicesFromAssembly(typeof(SuggestPodcastMetadataCommand).Assembly));
        builder.Services.AddSingleton<IValidator<SuggestPodcastMetadataCommand>, SuggestPodcastMetadataCommandValidator>();
        builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, Auth>("test", _ => { });
        builder.Services.AddAuthorization(o => o.AddPolicy(NutritionPolicies.Staff, NutritionPolicies.Configure));
        builder.Services.AddControllers().AddApplicationPart(typeof(PodcastMetadataController).Assembly);
        await using var app = builder.Build(); app.UseMiddleware<ExceptionHandlingMiddleware>(); app.UseRouting();
        app.UseAuthentication(); app.UseAuthorization(); app.MapControllers(); await app.StartAsync();
        using var http = app.GetTestClient(); const string path = "/api/v1/media/podcast-metadata";
        var input = new PodcastMetadataInput("  Tema  ", "en", "  Editorial  ");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync(path, input)).StatusCode); Assert.Empty(requests);
        http.DefaultRequestHeaders.Add("X-Test-Audience", "app");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync(path, input)).StatusCode); Assert.Empty(requests);
        http.DefaultRequestHeaders.Remove("X-Test-Audience"); http.DefaultRequestHeaders.Add("X-Test-Audience", "erp");
        foreach (var permission in permissions.ToArray())
        {
            permissions.Remove(permission); Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync(path, input)).StatusCode);
            Assert.Empty(requests); permissions.Add(permission);
        }
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await http.GetAsync(path)).StatusCode); Assert.Empty(requests);
        foreach (var json in new[] { "{}", "{\"title\":\"\"}", "{\"title\":\"Tema\",\"language\":\"fr\"}", "{\"title\":\"Tema\",\"context\":null}",
            "{\"title\":\"Tema\",\"patientId\":\"synthetic\"}", "{\"title\":\"Tema\",\"status\":\"Published\"}",
            "{\"title\":\"Tema\",\"storageKey\":\"media/test.mp3\"}", "{\"title\":\"Tema\",\"audio\":\"file\"}",
            "{\"title\":\"Tema\",\"context\":\"https://private.invalid\"}" })
            Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Empty(requests);
        var response = await http.PostAsJsonAsync(path, input); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var draft = (await response.Content.ReadFromJsonAsync<PodcastMetadataResult>())!;
        Assert.Equal("ok", draft.Status); Assert.True(draft.IsEstimate); Assert.Equal("Habitos", draft.Draft!.Category);
        Assert.Equal(new PodcastMetadataInput("Tema", "en", "Editorial"), Assert.Single(requests));
        Assert.True(response.Headers.CacheControl!.NoStore); Assert.NotNull(draft.Model); Assert.NotNull(draft.GeneratedAt);
        fail = true;
        response = await http.PostAsJsonAsync(path, new { title = "Tema" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); var unavailable = (await response.Content.ReadFromJsonAsync<PodcastMetadataResult>())!;
        Assert.Equal("unavailable", unavailable.Status); Assert.Null(unavailable.Draft); Assert.Equal("es", requests.Last().Language);
        Assert.True(response.Headers.CacheControl!.NoStore);
    }
}
