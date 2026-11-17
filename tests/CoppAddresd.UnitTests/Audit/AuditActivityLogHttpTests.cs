using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using CoppAddresd.Api.Authorization;
using CoppAddresd.Api.Controllers;
using CoppAddresd.Api.Middleware;
using CoppAddresd.Application.Common.Behaviors;
using CoppAddresd.Application.Features.Audit;
using CoppAddresd.Application.Interfaces;
using FluentValidation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CoppAddresd.UnitTests.Audit;

public sealed class AuditActivityLogHttpTests
{
    private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var permission = Request.Headers["X-Test-Permission"].ToString();
            return Task.FromResult(permission.Length == 0 ? AuthenticateResult.NoResult()
                : AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("permission", permission), new Claim("aud", "erp"), new Claim(ClaimTypes.Role, "Admin")], Scheme.Name)), Scheme.Name)));
        }
    }

    [Fact]
    public async Task RutaReal_ExigeAuditView_ValidaYEnlazaContratoSinPayloads()
    {
        var repository = Substitute.For<IAuditLogRepository>();
        var id = Guid.NewGuid();
        repository.ListAsync(Arg.Any<ListAuditActivityLogQuery>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedAuditActivityLogResult([new(id, DateTimeOffset.Parse("2026-11-17T13:00:00Z"), "UPDATE", "erp", "clinics", "fila", "USER", "admin@example.test", "Admin", "127.0.0.1", "corr")], 1, 1, 20, 1));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(repository);
        builder.Services.AddMediatR(configuration =>
        {
            configuration.RegisterServicesFromAssembly(typeof(ListAuditActivityLogQuery).Assembly);
            configuration.AddOpenBehavior(typeof(ValidationBehavior<,>));
        });
        builder.Services.AddValidatorsFromAssembly(typeof(ListAuditActivityLogQuery).Assembly);
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuth>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        builder.Services.AddSingleton<IAuthorizationHandler, PermissionHandler>();
        builder.Services.AddControllers().AddApplicationPart(typeof(AuditController).Assembly);
        await using var app = builder.Build();
        app.UseMiddleware<ExceptionHandlingMiddleware>();
        app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.MapControllers();
        await app.StartAsync();
        using var http = app.GetTestClient();
        const string path = "/api/v1/audit/activity-log";
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(path)).StatusCode);
        http.DefaultRequestHeaders.Add("X-Test-Permission", "Program.View");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(path)).StatusCode);
        Assert.Empty(repository.ReceivedCalls());
        http.DefaultRequestHeaders.Remove("X-Test-Permission");
        http.DefaultRequestHeaders.Add("X-Test-Permission", "Audit.View");
        foreach (var invalid in new[] { "page=0", "pageSize=101", "pageSize=0", "action=INVALID", "from=2026-11-18T00:00:00Z&to=2026-11-17T00:00:00Z", "from=no-es-fecha" })
            Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync(path + "?" + invalid)).StatusCode);
        Assert.Empty(repository.ReceivedCalls());
        using var response = await http.GetAsync(path + "?schema=erp&table=clinics&action=UPDATE&actor=admin&from=2026-11-17T08:00:00-05:00&to=2026-11-17T13:00:00Z&search=corr");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await repository.Received(1).ListAsync(Arg.Is<ListAuditActivityLogQuery>(x => x.Schema == "erp" && x.Table == "clinics" && x.Action == "UPDATE"
            && x.Actor == "admin" && x.Search == "corr" && x.From!.Value.Offset == TimeSpan.Zero && x.From == x.To), Arg.Any<CancellationToken>());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "items", "page", "pageSize", "total", "totalPages" }, json.RootElement.EnumerateObject().Select(x => x.Name).OrderBy(x => x));
        var item = json.RootElement.GetProperty("items")[0];
        Assert.Equal(id, item.GetProperty("id").GetGuid());
        Assert.Equal(11, item.EnumerateObject().Count());
        foreach (var privateName in new[] { "oldData", "newData", "changedData", "metadata", "userId", "requestId" })
            Assert.False(item.TryGetProperty(privateName, out _));
    }
}
