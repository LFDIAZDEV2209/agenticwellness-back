using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using CoppAddresd.Api;
using CoppAddresd.Application.Features.Agents.Vita;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CoppAddresd.UnitTests.Features.Agents;

public sealed class VitaApiHttpTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid PatientId = Guid.NewGuid();
    private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var token = Request.Headers.Authorization.ToString();
            if (token is not ("Bearer synthetic-app" or "Bearer synthetic-erp")) return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, UserId.ToString()),
                new("aud", token.EndsWith("app", StringComparison.Ordinal) ? "app" : "erp")], Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
    private static WebApplicationFactory<ApiEntryPoint> Host(IProgramRepository programs, bool active = true, bool internalEnabled = true) =>
        new WebApplicationFactory<ApiEntryPoint>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing"); builder.UseSetting("SkipDatabaseInitialization", "true");
            builder.UseSetting("Storage:SignatureKey", "synthetic-vita-http-signature-only-for-tests");
            builder.UseSetting("ConnectionStrings:DefaultConnection", "Host=127.0.0.1;Port=1;Database=unused;Username=unused");
            builder.UseSetting("Jwt:Secret", "synthetic-vita-jwt-secret-only-for-http-tests");
            builder.UseSetting("Jwt:Issuer", "test"); builder.UseSetting("AiService:InternalApiKey", "synthetic-ai-internal");
            builder.UseSetting("Vita:InternalEnabled", internalEnabled.ToString());
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddAuthentication("VitaHttp").AddScheme<AuthenticationSchemeOptions, TestAuth>("VitaHttp", _ => { });
                services.Configure<AuthenticationOptions>(o => { o.DefaultAuthenticateScheme = "VitaHttp"; o.DefaultChallengeScheme = "VitaHttp"; o.DefaultForbidScheme = "VitaHttp"; });
                var patients = Substitute.For<IPatientRepository>();
                patients.GetByUserIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(new PatientProfile
                    { Id = PatientId, UserId = UserId, Status = active ? "Activo" : "Inactivo" });
                services.AddSingleton(patients); services.AddSingleton(programs);
                services.AddSingleton(Substitute.For<ILegalConsentService>());
            });
        });

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("synthetic-erp", HttpStatusCode.Forbidden)]
    [InlineData("synthetic-app", HttpStatusCode.OK)]
    public async Task Publico_ExigeJwtApp(string? token, HttpStatusCode expected)
    {
        await using var host = Host(Substitute.For<IProgramRepository>()); using var client = host.CreateClient();
        if (token is not null) client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        using var result = await client.GetAsync("/api/v1/vita/me/context?scope=day");
        Assert.Equal(expected, result.StatusCode);
    }

    [Fact]
    public async Task UserIdAjenoEnQuery_SeIgnoraYSeResuelvePropietarioJwt()
    {
        var programs = Substitute.For<IProgramRepository>();
        await using var host = Host(programs); using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "synthetic-app");
        using var result = await client.GetAsync($"/api/v1/vita/me/context?scope=day&userId={Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var body = await result.Content.ReadFromJsonAsync<VitaContextDto>(); Assert.Equal("empty", body!.Status);
        await programs.Received(1).GetActiveEnrollmentForPatientAsync(PatientId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScopeNoPermitido_FallaEnPipeline()
    {
        await using var host = Host(Substitute.For<IProgramRepository>()); using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "synthetic-app");
        using var result = await client.GetAsync("/api/v1/vita/me/context?scope=administration");
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("wrong", HttpStatusCode.Unauthorized)]
    [InlineData("synthetic-ai-internal", HttpStatusCode.OK)]
    public async Task Interno_ExigeSuClaveAunqueHayaJwt(string? key, HttpStatusCode expected)
    {
        await using var host = Host(Substitute.For<IProgramRepository>()); using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "synthetic-app");
        if (key is not null) client.DefaultRequestHeaders.Add("X-Internal-Key", key);
        using var result = await client.GetAsync($"/api/v1/internal/vita/context?userId={UserId}&scope=day");
        Assert.Equal(expected, result.StatusCode);
    }

    [Fact]
    public async Task PacienteInactivo_EnvelopeForbiddenCon403()
    {
        await using var host = Host(Substitute.For<IProgramRepository>(), active: false); using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "synthetic-app");
        using var result = await client.GetAsync("/api/v1/vita/me/context?scope=day");
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        Assert.Equal("forbidden", (await result.Content.ReadFromJsonAsync<VitaContextDto>())!.Status);
    }
}
