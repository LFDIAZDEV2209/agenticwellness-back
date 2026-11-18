using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using CoppAddresd.Auth.Authorization;
using CoppAddresd.Auth.Constants;
using CoppAddresd.Auth.Controllers;
using CoppAddresd.Auth.Interfaces;
using CoppAddresd.Auth.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace CoppAddresd.UnitTests.Auth.Controllers;

/// <summary>HTTP y JWT reales: el contexto del llamador controla el aprovisionamiento.</summary>
public sealed class UserProvisioningAuthorizationTests
{
    private static readonly string[] AdminPermissions =
    [PermissionCodes.UsersCreate, PermissionCodes.UsersUpdate, PermissionCodes.RolesAssign,
     PermissionCodes.PermissionsAssign, PermissionCodes.SystemAdminSettings];

    [Theory]
    [InlineData(null, false, false, 201)]
    [InlineData(null, false, true, 403)]
    [InlineData("app", true, false, 403)]
    [InlineData("erp", false, false, 403)]
    [InlineData("erp", true, false, 201)]
    [InlineData("erp", true, true, 201)]
    public async Task Create_OnlyAuthorizedErpCallerCanProvision(
        string? audience, bool admin, bool assignments, int expectedStatus)
    {
        var service = Substitute.For<IUserService>();
        var response = new UserResponse(Guid.NewGuid().ToString(), "new@example.test", "Alta", "Prueba", true, DateTime.UtcNow, []);
        service.CreateAsync(Arg.Any<CreateUserRequest>(), Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns((true, (string?)null, response));
        using var harness = await Harness.Start(service, audience, admin);
        var result = await harness.Client.PostAsJsonAsync("/api/auth/users", new
        {
            email = "new@example.test", password = "Synthetic!Pass2026", firstName = "Alta", lastName = "Prueba",
            roleIds = assignments ? new[] { Guid.NewGuid() } : [],
            grantErpAccess = true, application = "erp", // Campos extra nunca autorizan acceso.
        });
        Assert.Equal(expectedStatus, (int)result.StatusCode);
        if (expectedStatus == 201)
            await service.Received(1).CreateAsync(Arg.Any<CreateUserRequest>(), Arg.Any<CancellationToken>(), audience == "erp");
        else
            await service.DidNotReceive().CreateAsync(Arg.Any<CreateUserRequest>(), Arg.Any<CancellationToken>(), Arg.Any<bool>());
    }

    [Theory]
    [InlineData(null, false, false, 200)]
    [InlineData(null, false, true, 403)]
    [InlineData("app", true, true, 403)]
    [InlineData("erp", false, true, 403)]
    [InlineData("erp", true, false, 200)]
    [InlineData("erp", true, true, 200)]
    public async Task Bulk_AnonymousCannotAssignRolesOrProvisionErp(
        string? audience, bool admin, bool assignments, int expectedStatus)
    {
        var service = Substitute.For<IUserService>();
        service.CreateBulkAsync(Arg.Any<BulkCreateUsersRequest>(), Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(new BulkCreateUsersResult());
        using var harness = await Harness.Start(service, audience, admin);
        var result = await harness.Client.PostAsJsonAsync("/api/auth/users/bulk", new
        {
            rows = new[] { new { email = "bulk@example.test", firstName = "Alta", lastName = "Prueba", roleName = assignments ? "Admin" : null } },
        });
        Assert.Equal(expectedStatus, (int)result.StatusCode);
        if (expectedStatus == 200)
            await service.Received(1).CreateBulkAsync(Arg.Any<BulkCreateUsersRequest>(), Arg.Any<CancellationToken>(), audience == "erp");
        else
            await service.DidNotReceive().CreateBulkAsync(Arg.Any<BulkCreateUsersRequest>(), Arg.Any<CancellationToken>(), Arg.Any<bool>());
    }

    [Theory]
    [InlineData(null, false, 401)]
    [InlineData("app", true, 403)]
    [InlineData("erp", false, 403)]
    [InlineData("erp", true, 204)]
    public async Task Repair_RequiresAuthenticatedErpAndAdministrativePermissions(string? audience, bool admin, int expectedStatus)
    {
        var service = Substitute.For<IUserService>();
        service.GrantErpAccessAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((true, (string?)null, false));
        using var harness = await Harness.Start(service, audience, admin);
        var result = await harness.Client.PostAsync($"/api/auth/users/{Guid.NewGuid()}/erp-access", null);
        Assert.Equal(expectedStatus, (int)result.StatusCode);
        if (expectedStatus != 204)
            await service.DidNotReceive().GrantErpAccessAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(PermissionCodes.UsersCreate, false)]
    [InlineData(PermissionCodes.SystemAdminSettings, false)]
    [InlineData(PermissionCodes.RolesAssign, true)]
    [InlineData(PermissionCodes.PermissionsAssign, true)]
    public async Task Create_EachRequiredPermissionIsEnforced(string missingPermission, bool assignments)
    {
        var service = Substitute.For<IUserService>();
        using var harness = await Harness.Start(service, "erp", true, missingPermission);
        var response = await harness.Client.PostAsJsonAsync("/api/auth/users", new
        {
            email = "blocked@example.test", password = "Synthetic!Pass2026", firstName = "Alta", lastName = "Prueba",
            roleIds = assignments ? new[] { Guid.NewGuid() } : [],
        });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await service.DidNotReceive().CreateAsync(Arg.Any<CreateUserRequest>(), Arg.Any<CancellationToken>(), Arg.Any<bool>());
    }

    [Theory]
    [InlineData(null, false, 401)]
    [InlineData("app", true, 403)]
    [InlineData("erp", false, 403)]
    [InlineData("erp", true, 200)]
    public async Task Link_ApplicationGrantNeedsErpAuthorityEvenWithoutRoles(string? audience, bool admin, int expectedStatus)
    {
        var service = Substitute.For<IUserService>();
        service.LinkAsync(Arg.Any<LinkUserAccountRequest>(), Arg.Any<CancellationToken>()).Returns(
            (true, (string?)null, new UserResponse(Guid.NewGuid().ToString(), "link@example.test", "Alta", "Prueba", true, DateTime.UtcNow, []), false));
        using var harness = await Harness.Start(service, audience, admin);
        var result = await harness.Client.PostAsJsonAsync("/api/auth/users/link", new { email = "link@example.test" });
        Assert.Equal(expectedStatus, (int)result.StatusCode);
        if (expectedStatus != 200)
            await service.DidNotReceive().LinkAsync(Arg.Any<LinkUserAccountRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("platform", null, false, 401)]
    [InlineData("platform/bulk", null, false, 401)]
    [InlineData("platform", "app", true, 403)]
    [InlineData("platform/bulk", "app", true, 403)]
    [InlineData("platform", "erp", false, 403)]
    [InlineData("platform/bulk", "erp", false, 403)]
    [InlineData("platform", "erp", true, 201)]
    [InlineData("platform/bulk", "erp", true, 200)]
    public async Task PlatformRoutes_NeverFallBackToPublicRegistration(string route, string? audience, bool admin, int expectedStatus)
    {
        var service = Substitute.For<IUserService>();
        service.CreateAsync(Arg.Any<CreateUserRequest>(), Arg.Any<CancellationToken>(), true).Returns(
            (true, (string?)null, new UserResponse(Guid.NewGuid().ToString(), "platform@example.test", "Alta", "Prueba", true, DateTime.UtcNow, [])));
        service.CreateBulkAsync(Arg.Any<BulkCreateUsersRequest>(), Arg.Any<CancellationToken>(), true).Returns(new BulkCreateUsersResult());
        using var harness = await Harness.Start(service, audience, admin);
        object body = route.EndsWith("bulk")
            ? new { rows = new[] { new { email = "platform@example.test", firstName = "Alta", lastName = "Prueba" } } }
            : new { email = "platform@example.test", password = "Synthetic!Pass2026", firstName = "Alta", lastName = "Prueba" };
        var response = await harness.Client.PostAsJsonAsync($"/api/auth/users/{route}", body);
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        if (expectedStatus is 401 or 403)
        {
            await service.DidNotReceive().CreateAsync(Arg.Any<CreateUserRequest>(), Arg.Any<CancellationToken>(), Arg.Any<bool>());
            await service.DidNotReceive().CreateBulkAsync(Arg.Any<BulkCreateUsersRequest>(), Arg.Any<CancellationToken>(), Arg.Any<bool>());
        }
    }

    private sealed class Harness(IHost host, HttpClient client) : IDisposable
    {
        public HttpClient Client { get; } = client;
        public void Dispose() { Client.Dispose(); host.Dispose(); }

        public static async Task<Harness> Start(IUserService service, string? audience, bool admin, string? missingPermission = null)
        {
            var key = new SymmetricSecurityKey(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            var host = await new HostBuilder().ConfigureWebHost(web =>
            {
                web.UseTestServer().ConfigureServices(services =>
                {
                    services.AddControllers().AddApplicationPart(typeof(UsersController).Assembly);
                    services.AddSingleton(service);
                    services.AddSingleton(Substitute.For<IPermissionService>());
                    services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
                        o.TokenValidationParameters = new() { ValidateIssuer = false, ValidateAudience = false, IssuerSigningKey = key, ValidateLifetime = true });
                    services.AddAuthorization(o => o.AddPolicy(ErpAudienceRequirement.PolicyName,
                        p => p.RequireAuthenticatedUser().AddRequirements(new ErpAudienceRequirement())));
                    services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
                    services.AddSingleton<IAuthorizationHandler, ErpAudienceHandler>();
                    services.AddSingleton<IAuthorizationHandler, PermissionHandler>();
                }).Configure(app =>
                {
                    app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
                    app.UseEndpoints(e => e.MapControllers());
                });
            }).StartAsync();
            var client = host.GetTestServer().CreateClient();
            if (audience is not null)
            {
                var claims = new List<Claim> { new("sub", Guid.NewGuid().ToString()), new("aud", audience) };
                if (admin) claims.AddRange(AdminPermissions.Where(p => p != missingPermission).Select(p => new Claim(PermissionClaimTypes.Permission, p)));
                var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(claims: claims,
                    expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new(key, SecurityAlgorithms.HmacSha256)));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            return new Harness(host, client);
        }
    }
}
