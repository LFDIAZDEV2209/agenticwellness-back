using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using CoppAddresd.Api.Configuration;
using CoppAddresd.Api.Constants;
using CoppAddresd.Api.Controllers;
using CoppAddresd.Api.Middleware;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services;
using CoppAddresd.Domain.Wellness365;
using CoppAddresd.Infrastructure.Repositories;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    [NutritionPostgresFact]
    public async Task Motor365TimelineHttpUsesLatestStoredDecisionAndAuditsScopedExport()
    {
        await using var db = Context();
        var patient = await PatientAsync(db);
        var foreign = await PatientAsync(db);
        var noProgram = await PatientAsync(db);
        var professional = NutritionProfessional(patient.Id);
        db.Professionals.Add(professional);
        db.PatientProfessionalAssignments.AddRange(
            new() { PatientId = patient.Id, ProfessionalId = professional.Id },
            new() { PatientId = noProgram.Id, ProfessionalId = professional.Id });
        var start = new DateOnly(2026, 11, 1);
        db.Motor365People.AddRange(CreateYear365Person(patient.Id, start), CreateYear365Person(foreign.Id, start));
        for (var version = 1; version <= 2; version++)
            db.Motor365EngineParams.Add(new() { Version = version, ParametersJson = "{}", ChangedBy = patient.UserId!.Value, Reason = "Fixture" });
        Motor365EngineEvent Item(Guid owner, int day, int version, int parameters, string kind, int date, string text) => new()
        {
            Id = Guid.NewGuid(), PersonId = owner, Day = day, PlanVersion = version, ParamsVersion = parameters,
            Kind = kind, MessageEs = text, MessageEn = $"EN {text}", OccurredAt = new DateTimeOffset(2026, 11, date, 0, 0, 0, TimeSpan.Zero),
        };
        var stale = Item(patient.Id, 14, 1, 1, "sin", 13, "Decisión anterior");
        var adjustment = Item(patient.Id, 14, 2, 2, "ajuste", 14, "=texto \"seguro\"");
        var phase = Item(patient.Id, 14, 1, 1, "modo", 14, "Cambio de fase");
        var other = Item(foreign.Id, 14, 1, 1, "meta", 14, "NO_VISIBLE");
        db.Motor365EngineEvents.AddRange(stale, adjustment, phase, other);
        await db.SaveChangesAsync();

        var key = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32));
        var user = Guid.NewGuid();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Host.UseDefaultServiceProvider(o => o.ValidateOnBuild = false);
        builder.Services.AddControllers().AddApplicationPart(typeof(Year365ReportController).Assembly);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<IPatientScopeActor, UsageHttpActor>();
        builder.Services.AddScoped<IPatientScopeStore, PatientScopeStore>();
        builder.Services.AddScoped<IPatientScopeGuard, PatientScopeGuard>();
        builder.Services.AddScoped(_ => Context());
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddMotor365Api();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            o.MapInboundClaims = false;
            o.TokenValidationParameters = new() { IssuerSigningKey = key, ValidIssuer = "timeline-qa", ValidAudiences = ["app", "erp"] };
        });
        await using var app = builder.Build();
        app.UseMiddleware<ExceptionHandlingMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };

        string Token(string audience = "erp", bool view = true, bool export = false)
        {
            var claims = new List<Claim> { new("sub", user.ToString()), new("professional_id", professional.Id.ToString()) };
            if (view) claims.Add(new("permission", PermissionCodes.ProgramProfile360View));
            if (export) claims.Add(new("permission", PermissionCodes.ProgramExport));
            return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("timeline-qa", audience,
                claims, expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new(key, SecurityAlgorithms.HmacSha256)));
        }

        async Task<HttpResponseMessage> Send(Guid id, string? token, string query = "", bool export = false)
        {
            using var request = new HttpRequestMessage(export ? HttpMethod.Post : HttpMethod.Get,
                $"/api/v1/program/erp/patients/{id}/motor-timeline{(export ? "/export" : "")}{query}");
            if (token is not null) request.Headers.Authorization = new("Bearer", token);
            return await http.SendAsync(request);
        }

        using (var result = await Send(patient.Id, null)) Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
        using (var result = await Send(patient.Id, Token("app", export: true))) Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        using (var result = await Send(patient.Id, Token(view: false))) Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        using (var result = await Send(foreign.Id, Token())) Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        using (var result = await Send(patient.Id, Token(), "?from=2026-11-15&to=2026-11-14")) Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        using (var result = await Send(patient.Id, Token(), "?kind=unknown")) Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        using (var result = await Send(noProgram.Id, Token()))
        {
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            var json = await result.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Empty(json.GetProperty("events").EnumerateArray());
        }
        using (var result = await Send(patient.Id, Token()))
        {
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            Assert.True(result.Headers.CacheControl?.NoStore);
            var json = await result.Content.ReadFromJsonAsync<JsonElement>();
            var ids = json.GetProperty("events").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToArray();
            Assert.Equal(new[] { adjustment.Id, phase.Id }, ids);
        }
        using (var result = await Send(patient.Id, Token(), "?from=2026-11-13&to=2026-11-13&kind=sin"))
        {
            var json = await result.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Empty(json.GetProperty("events").EnumerateArray());
        }
        using (var result = await Send(patient.Id, Token("app", export: true), export: true)) Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        using (var result = await Send(patient.Id, Token(), export: true)) Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        using (var result = await Send(patient.Id, Token(view: false, export: true), export: true)) Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        using (var result = await Send(foreign.Id, Token(export: true), export: true)) Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        using (var result = await Send(noProgram.Id, Token(export: true), export: true)) Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        using (var result = await Send(patient.Id, Token(export: true), "?lang=fr", export: true)) Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Empty(await db.Motor365AuditLog.AsNoTracking().Where(x => x.PersonId == patient.Id).ToArrayAsync());

        using var csv = await Send(patient.Id, Token(export: true), "?from=2026-11-14&to=2026-11-14&kind=ajuste&lang=es", export: true);
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        var content = await csv.Content.ReadAsStringAsync();
        Assert.Contains(adjustment.Id.ToString(), content);
        Assert.Contains("\"'=texto \"\"seguro\"\"\"", content);
        Assert.DoesNotContain(phase.Id.ToString(), content);
        Assert.DoesNotContain(patient.Id.ToString(), content);
        Assert.DoesNotContain("NO_VISIBLE", content);
        var audit = Assert.Single(await db.Motor365AuditLog.AsNoTracking().Where(x => x.PersonId == patient.Id).ToArrayAsync());
        Assert.Equal(user, audit.ActorUserId);
        Assert.Equal("timeline.export", audit.Action);
        Assert.Equal(4, await db.Motor365EngineEvents.CountAsync());

        db.Motor365EngineEvents.AddRange(Enumerable.Range(31, 43)
            .Select(day => Item(patient.Id, day, 2, 2, "modo", 14, "Decisión paginada")));
        await db.SaveChangesAsync();
        using (var result = await Send(patient.Id, Token(), "?page=1"))
        {
            var json = await result.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.GetProperty("hasNext").GetBoolean());
            Assert.Equal(40, json.GetProperty("events").GetArrayLength());
        }
        using (var result = await Send(patient.Id, Token(), "?page=2"))
        {
            var json = await result.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(json.GetProperty("hasNext").GetBoolean());
            Assert.Equal(5, json.GetProperty("events").GetArrayLength());
        }
    }
}
