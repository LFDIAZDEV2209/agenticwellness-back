using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using CoppAddresd.Api;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Enums;
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

/// <summary>Política y validación reales del POST de instancias, con persistencia sustituida.</summary>
public sealed class AgentInstanceAssignmentHttpTests
{
    private static readonly Guid LinkedUser = Guid.NewGuid();
    private static readonly Guid AgentType = Guid.NewGuid();
    private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var token = Request.Headers.Authorization.ToString();
            if (token.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new("aud", "erp") };
            if (token == "Bearer synthetic-update") claims.Add(new("permission", "Agents.Update"));
            if (token == "Bearer synthetic-view") claims.Add(new("permission", "Agents.View"));
            var user = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(user, Scheme.Name)));
        }
    }

    private static WebApplicationFactory<ApiEntryPoint> Host(IAgentCatalogRepository catalog) =>
        new WebApplicationFactory<ApiEntryPoint>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing"); builder.UseSetting("SkipDatabaseInitialization", "true");
            builder.UseSetting("Storage:SignatureKey", "synthetic-agent-selector-signature-key-for-tests");
            builder.UseSetting("ConnectionStrings:DefaultConnection", "Host=127.0.0.1;Port=1;Database=unused;Username=unused");
            builder.UseSetting("Jwt:Secret", "synthetic-agent-selector-jwt-secret-only-for-tests"); builder.UseSetting("Jwt:Issuer", "test");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddAuthentication("AgentAssignment").AddScheme<AuthenticationSchemeOptions, TestAuth>("AgentAssignment", _ => { });
                services.Configure<AuthenticationOptions>(o => { o.DefaultAuthenticateScheme = "AgentAssignment";
                    o.DefaultChallengeScheme = "AgentAssignment"; o.DefaultForbidScheme = "AgentAssignment"; });
                services.AddSingleton(catalog);
            });
        });

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("synthetic-no-permission", HttpStatusCode.Forbidden)]
    [InlineData("synthetic-view", HttpStatusCode.Forbidden)]
    public async Task Asignar_SinAgentsUpdate_RechazaAntesDelHandler(string? token, HttpStatusCode expected)
    {
        var catalog = Substitute.For<IAgentCatalogRepository>();
        await using var host = Host(catalog); using var client = host.CreateClient();
        if (token is not null) client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        using var result = await client.PostAsJsonAsync("/api/v1/agents/instances", new { userId = LinkedUser, agentTypeId = AgentType });
        Assert.Equal(expected, result.StatusCode); Assert.Empty(catalog.ReceivedCalls());
    }

    [Fact]
    public async Task Asignar_ConAgentsUpdate_UsaUserIdDeCuentaYDevuelve201()
    {
        var catalog = Substitute.For<IAgentCatalogRepository>();
        var type = new AgentType { Id = AgentType, Name = "Especialista", Status = AgentStatus.Activo };
        catalog.GetAgentTypeAsync(AgentType, Arg.Any<CancellationToken>()).Returns(type);
        catalog.GetInstanceAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(c => new AgentInstance
            { Id = c.Arg<Guid>(), UserId = LinkedUser, AgentTypeId = AgentType, AgentType = type });
        await using var host = Host(catalog); using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "synthetic-update");
        using var result = await client.PostAsJsonAsync("/api/v1/agents/instances", new { userId = LinkedUser, agentTypeId = AgentType });
        Assert.Equal(HttpStatusCode.Created, result.StatusCode);
        await catalog.Received(1).AddInstanceAsync(Arg.Is<AgentInstance>(i => i.UserId == LinkedUser && i.AgentTypeId == AgentType), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Asignar_IdentificadorVacio_400SinPersistencia(bool missingUser)
    {
        var catalog = Substitute.For<IAgentCatalogRepository>();
        await using var host = Host(catalog); using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "synthetic-update");
        using var result = await client.PostAsJsonAsync("/api/v1/agents/instances", new
            { userId = missingUser ? Guid.Empty : LinkedUser, agentTypeId = missingUser ? AgentType : Guid.Empty });
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode); Assert.Empty(catalog.ReceivedCalls());
    }
}
