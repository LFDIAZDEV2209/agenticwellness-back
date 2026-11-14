using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using CoppAddresd.Api.Context;
using CoppAddresd.Api.Controllers;
using CoppAddresd.Application.Features.Nutrition.Queries;
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

public sealed class NutritionRecommendationsHttpTests
{
    private sealed class Auth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var audience = Request.Headers["X-Test-Audience"].ToString();
            return Task.FromResult(audience.Length == 0 ? AuthenticateResult.NoResult()
                : AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim("aud", audience)], Scheme.Name)), Scheme.Name)));
        }
    }

    [Fact]
    public async Task HttpRecommendations_RequiereApp_JwtNoIdsDelCliente_YTextoDelIdiomaSolicitado()
    {
        var owner = Guid.NewGuid(); var current = Substitute.For<ICurrentContext>(); current.UserId.Returns(owner);
        var mediator = Substitute.For<IMediator>(); GetMyNutritionRecommendations? sent = null;
        mediator.Send(Arg.Any<GetMyNutritionRecommendations>(), Arg.Any<CancellationToken>()).Returns(call =>
        { sent = call.Arg<GetMyNutritionRecommendations>(); return new NutritionRecommendationsDto(null, []); });
        using var server = new TestServer(new WebHostBuilder().ConfigureServices(services =>
        {
            services.AddLogging(); services.AddSingleton(current); services.AddSingleton(mediator);
            services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, Auth>("test", _ => { });
            services.AddAuthorization(); services.AddControllers().AddApplicationPart(typeof(MyNutritionRecommendationsController).Assembly);
        }).Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(e => e.MapControllers()); }));
        using var client = server.CreateClient(); const string path = "/api/v1/me/nutrition/cycles/recommendations";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode); Assert.Null(sent);
        client.DefaultRequestHeaders.Add("X-Test-Audience", "erp");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode); Assert.Null(sent);
        client.DefaultRequestHeaders.Remove("X-Test-Audience"); client.DefaultRequestHeaders.Add("X-Test-Audience", "app");
        client.DefaultRequestHeaders.Add("X-App-Language", "en");
        var response = await client.GetAsync(path + $"?userId={Guid.NewGuid()}&patientId={Guid.NewGuid()}&cycleId={Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal(owner, sent!.UserId); Assert.Equal("en", sent.Language);
        Assert.Equal("{\"current\":null,\"previous\":[]}", await response.Content.ReadAsStringAsync());
        Assert.Equal("en", Assert.Single(response.Content.Headers.ContentLanguage)); Assert.True(response.Headers.CacheControl!.NoStore);
        response = await client.GetAsync(path + "?language=es"); Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("es", sent.Language);
    }
}
