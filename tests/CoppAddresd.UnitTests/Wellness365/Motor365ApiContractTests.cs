using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using CoppAddresd.Api.Configuration;
using CoppAddresd.Api.Controllers;
using CoppAddresd.Application.Common.Behaviors;
using CoppAddresd.Application.Features.Motor365;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using NSubstitute;

namespace CoppAddresd.UnitTests.Wellness365;

public sealed class Motor365ApiContractTests
{
    private static readonly SymmetricSecurityKey SigningKey = new(new byte[32]);

    public static TheoryData<string, string, int[]> Operations => new()
    {
        { "/v1/people", "post", [200, 201, 400, 401, 403, 404, 409, 413, 415, 422, 500, 503] },
        { "/v1/people/{id}", "get", [200, 400, 401, 403, 404, 500, 503] },
        { "/v1/people/{id}", "put", [200, 400, 401, 403, 404, 409, 413, 415, 422, 500, 503] },
        { "/v1/people/{id}/plan", "get", [200, 400, 401, 403, 404, 409, 500, 503] },
        { "/v1/people/{id}/plan/{day}", "get", [200, 400, 401, 403, 404, 409, 500, 503] },
        { "/v1/people/{id}/logs/{day}", "put", [200, 400, 401, 403, 404, 409, 413, 415, 422, 500, 503] },
        { "/v1/people/{id}/events", "get", [200, 400, 401, 403, 404, 409, 500, 503] },
        { "/v1/engine/params", "get", [200, 401, 403, 500, 503] },
        { "/v1/engine/params", "put", [200, 400, 401, 403, 409, 413, 415, 422, 500, 503] },
        { "/v1/catalog/foods", "get", [200, 401, 403, 500, 503] },
        { "/v1/catalog/exercises", "get", [200, 401, 403, 500, 503] },
        { "/v1/audit", "get", [200, 400, 401, 403, 500, 503] },
        { "/v1/health", "get", [200, 500, 503] },
    };

    [Fact]
    public async Task OpenApi_publica_las_13_operaciones_y_tipifica_cada_error()
    {
        await using var app = await HostAsync();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/swagger/motor365/swagger.json");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = json.RootElement.GetProperty("paths");
        Assert.Equal(13, paths.EnumerateObject().Sum(p => p.Value.EnumerateObject().Count()));
        Assert.Equal("bearer", json.RootElement.GetProperty("components").GetProperty("securitySchemes")
            .GetProperty("Bearer").GetProperty("scheme").GetString());
        foreach (var row in Operations)
        {
            var path = (string)row[0];
            var method = (string)row[1];
            var codes = (int[])row[2];
            var operation = paths.GetProperty(path).GetProperty(method);
            Assert.StartsWith("Motor365", operation.GetProperty("operationId").GetString());
            var responses = operation.GetProperty("responses");
            foreach (var code in codes)
            {
                var definition = responses.GetProperty(code.ToString());
                if (code >= 400)
                {
                    var schema = definition.GetProperty("content").GetProperty("application/json").GetProperty("schema");
                    Assert.Equal("#/components/schemas/MotorApiError", schema.GetProperty("$ref").GetString());
                }
            }
        }
        Assert.Empty(paths.GetProperty("/v1/health").GetProperty("get").GetProperty("security")[0].EnumerateObject());
    }

    [Theory]
    [InlineData(null, null, "/v1/catalog/foods", 401)]
    [InlineData("app", null, "/v1/catalog/foods", 200)]
    [InlineData("app", "Admin", "/v1/engine/params", 403)]
    [InlineData("erp", "Professional", "/v1/engine/params", 200)]
    [InlineData("erp", "Finance", "/v1/engine/params", 403)]
    [InlineData("erp", "Auditor", "/v1/audit", 200)]
    [InlineData("erp", "Professional", "/v1/audit", 403)]
    [InlineData(null, null, "/v1/health", 503)]
    public async Task Jwt_por_audiencia_y_rol(string? audience, string? role, string path, int status)
    {
        await using var app = await HostAsync();
        using var client = app.GetTestClient();
        if (audience is not null) Authenticate(client, audience, role);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US");
        using var result = await client.GetAsync(path);
        Assert.Equal(status, (int)result.StatusCode);
        if (status == 401) Assert.Contains(result.Headers.WwwAuthenticate, value => value.Scheme == "Bearer");
        if (status >= 400)
        {
            var body = Assert.IsType<MotorApiError>(await result.Content.ReadFromJsonAsync<MotorApiError>());
            Assert.Equal(body.Messages.En, body.Message);
            Assert.NotEqual(body.Messages.Es, body.Messages.En);
            Assert.False(string.IsNullOrWhiteSpace(body.TraceId));
        }
    }

    [Theory]
    [InlineData("Admin", 200)]
    [InlineData("clinical_admin", 200)]
    [InlineData("Professional", 403)]
    [InlineData("Auditor", 403)]
    public async Task Solo_roles_autorizados_escriben_parametros(string role, int status)
    {
        await using var app = await HostAsync();
        using var client = app.GetTestClient();
        Authenticate(client, "erp", role);
        using var response = await client.PutAsJsonAsync("/v1/engine/params", new { @params = new { } });
        Assert.Equal(status, (int)response.StatusCode);
    }

    [Fact]
    public async Task Enlace_http_y_validacion_MediatR_producen_el_mismo_error()
    {
        await using var app = await HostAsync();
        using var client = app.GetTestClient();
        Authenticate(client, "app", null);
        foreach (var day in new[] { "invalid", "366" })
        {
            using var response = await client.GetAsync($"/v1/people/{Guid.NewGuid()}/plan/{day}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var error = Assert.IsType<MotorApiError>(await response.Content.ReadFromJsonAsync<MotorApiError>());
            Assert.Equal("invalid_request", error.Code);
            Assert.Equal("day", Assert.Single(error.Issues).Field);
        }
    }

    [Theory]
    [InlineData("person_not_found", 404)]
    [InlineData("plan_locked", 409)]
    [InlineData("submission_conflict", 409)]
    [InlineData("version_conflict", 409)]
    [InlineData("future_day", 422)]
    [InlineData("eligibility_required", 422)]
    [InlineData("invalid_parameters", 422)]
    [InlineData("service_unavailable", 503)]
    public void Excepciones_del_modulo_usan_codigos_estables(string code, int status)
    {
        var result = Motor365Errors.Map(new MotorException(code));
        Assert.Equal((code, status), (result.Code, result.Status));
        Assert.Equal("internal_error", Motor365Errors.Map(new Exception("internal credentials")).Code);
    }

    private static void Authenticate(HttpClient client, string audience, string? role)
    {
        var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()) };
        if (role is not null) claims.Add(new(ClaimTypes.Role, role));
        var jwt = new JwtSecurityToken("test", audience, claims, expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
    }

    private static async Task<WebApplication> HostAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddControllers().AddApplicationPart(typeof(Motor365Controller).Assembly);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new()
            {
                ValidIssuer = "test", ValidAudiences = ["app", "erp"],
                IssuerSigningKey = SigningKey, ValidateIssuerSigningKey = true,
            };
        });
        builder.Services.AddMotor365Api();
        builder.Services.AddSingleton(TimeProvider.System);
        var store = Substitute.For<IMotor365Store>();
        store.InTransactionAsync(true, Arg.Any<Func<CancellationToken, Task<MotorParamsDto>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task<MotorParamsDto>>>()(call.Arg<CancellationToken>()));
        store.ReadAuditAsync(1, 20, Arg.Any<CancellationToken>()).Returns(Array.Empty<MotorAuditEntry>());
        builder.Services.AddSingleton(store);
        builder.Services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(typeof(GetMotorParams).Assembly);
            cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
        });
        builder.Services.AddValidatorsFromAssembly(typeof(GetMotorDayValidator).Assembly);
        builder.Services.AddSwaggerGen(options =>
        {
            Motor365Api.ConfigureMotor365OpenApi(options);
            options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer" });
        });
        var app = builder.Build();
        app.UseSwagger();
        app.UseMiddleware<Motor365Errors>();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        return app;
    }
}
