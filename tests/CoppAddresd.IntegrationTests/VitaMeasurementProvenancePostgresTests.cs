using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using CoppAddresd.Api.Context;
using CoppAddresd.Application.Features.Agents.Vita;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Infrastructure.Repositories;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    [NutritionPostgresFact]
    public async Task VitaProvenance_AppAndErpExposeRecordedChannelReporterAndStatus()
    {
        await using var db = Context();
        var patient = await PatientAsync(db);
        var userId = patient.UserId!.Value;
        var (metric, unit) = await VitaAMetricAsync(db, "heart_rate", "bpm", "bpm", "vital");
        var professional = VitaAReading(patient.Id, metric, unit, VitaANow.UtcDateTime.AddHours(-3), 73m, "professional");
        professional.SourceChannel = "professional";
        professional.ReportedBy = "professional";
        var historic = VitaAReading(patient.Id, metric, unit, VitaANow.UtcDateTime.AddHours(-4), 72m, "professional");
        db.ClinicalMeasurements.AddRange(professional, historic);
        await db.SaveChangesAsync();

        await using var host = await MeasurementContractHostAsync(userId, captureEnabled: true);
        using var client = VitaAClient(host);
        var voice = await CapturePulseAsync("vita_voice", "voice-provenance", VitaANow.AddHours(-1), 75m);
        var whatsapp = await CapturePulseAsync("vita_whatsapp", "whatsapp-provenance", VitaANow.AddHours(-2), 76m);

        using var withoutAuth = await client.GetAsync("/api/v1/me/measurements");
        Assert.Equal(HttpStatusCode.Unauthorized, withoutAuth.StatusCode);
        client.DefaultRequestHeaders.Add("X-Test-Measurement-Auth", userId.ToString());
        var mobile = await client.GetFromJsonAsync<JsonElement>("/api/v1/me/measurements?pageSize=10&codes=heart_rate");
        var erp = await client.GetFromJsonAsync<JsonElement>($"/api/v1/patients/{patient.Id}/measurements");
        Assert.False(mobile.GetProperty("hasNextPage").GetBoolean());
        Assert.Equal(JsonValueKind.Null, mobile.GetProperty("nextCursor").ValueKind);
        foreach (var payload in new[] { mobile.GetProperty("items"), erp })
        {
            var rows = payload.EnumerateArray().ToArray();
            Assert.Equal(new[] { voice, whatsapp, professional.Id, historic.Id }, rows.Select(x => x.GetProperty("id").GetGuid()));
            AssertMeasurementProvenance(rows[0], "patient", "vita_voice", "patient");
            AssertMeasurementProvenance(rows[1], "patient", "vita_whatsapp", "patient");
            AssertMeasurementProvenance(rows[2], "professional", "professional", "professional");
            AssertMeasurementProvenance(rows[3], "professional", null, null);
        }

        async Task<Guid> CapturePulseAsync(string channel, string turn, DateTimeOffset observedAt, decimal pulse)
        {
            var input = new ProposeVitaCaptureRequest("record_measurement", channel, turn, observedAt,
                MetricCode: "heart_rate", UnitCode: "bpm", Value: pulse);
            var proposal = await ProposeCaptureAsync(client, userId, input);
            return (await ConfirmCaptureAsync(client, userId, input, proposal.Token)).EntityId;
        }
    }

    [NutritionPostgresFact]
    public async Task VitaProvenance_ConfirmedCorrectionSupersedesTheOriginalWithoutDeletingIt()
    {
        await using var db = Context();
        var patient = await PatientAsync(db);
        var (metric, unit) = await VitaAMetricAsync(db, "heart_rate", "bpm", "bpm", "vital");
        var original = VitaAReading(patient.Id, metric, unit, VitaANow.UtcDateTime.AddHours(-2), 72m, "patient");
        db.ClinicalMeasurements.Add(original);
        await db.SaveChangesAsync();

        var userId = patient.UserId!.Value;
        await using var host = await MeasurementContractHostAsync(userId);
        using var client = VitaAClient(host);
        client.DefaultRequestHeaders.Add("X-Test-Measurement-Auth", userId.ToString());
        const string mobilePath = "/api/v1/me/measurements?codes=heart_rate";
        var before = await client.GetFromJsonAsync<JsonElement>(mobilePath);
        Assert.Equal("confirmed", Assert.Single(before.GetProperty("items").EnumerateArray()).GetProperty("status").GetString());

        var correction = VitaAReading(patient.Id, metric, unit, VitaANow.UtcDateTime.AddHours(-1), 75m, "patient");
        correction.SupersedesId = original.Id;
        correction.Status = "pending_confirmation";
        db.ClinicalMeasurements.Add(correction);
        await db.SaveChangesAsync();
        var pending = await client.GetFromJsonAsync<JsonElement>(mobilePath);
        Assert.Contains(pending.GetProperty("items").EnumerateArray(), row =>
            row.GetProperty("id").GetGuid() == original.Id && row.GetProperty("status").GetString() == "confirmed");

        correction.Status = "confirmed";
        await db.SaveChangesAsync();

        var mobile = await client.GetFromJsonAsync<JsonElement>(mobilePath);
        var readings = mobile.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, readings.Length);
        AssertStatus(Assert.Single(readings, row => row.GetProperty("id").GetGuid() == original.Id), original.Id, "superseded");
        AssertStatus(Assert.Single(readings, row => row.GetProperty("id").GetGuid() == correction.Id), correction.Id, "confirmed");
        var erp = await client.GetFromJsonAsync<JsonElement>($"/api/v1/patients/{patient.Id}/measurements");
        AssertStatus(Assert.Single(erp.EnumerateArray()), correction.Id, "confirmed");

        var stored = await db.ClinicalMeasurements.AsNoTracking().Where(row => row.PatientId == patient.Id).ToListAsync();
        var preserved = Assert.Single(stored, row => row.Id == original.Id);
        Assert.Equal(72m, preserved.Value);
        Assert.Equal("confirmed", preserved.Status);
        Assert.Equal(original.Id, Assert.Single(stored, row => row.Id == correction.Id).SupersedesId);

        static void AssertStatus(JsonElement row, Guid id, string status)
        {
            Assert.Equal(id, row.GetProperty("id").GetGuid());
            Assert.Equal(status, row.GetProperty("status").GetString());
        }
    }

    private Task<WebApplication> MeasurementContractHostAsync(Guid userId, bool captureEnabled = false)
    {
        var current = Substitute.For<ICurrentContext>();
        current.UserId.Returns(userId);
        current.HasPermissionAsync("Patients.View", Arg.Any<CancellationToken>()).Returns(true);
        return VitaAHostAsync(captureEnabled: captureEnabled, configureServices: services =>
        {
            services.AddAuthentication("measurements-test")
                .AddScheme<AuthenticationSchemeOptions, MeasurementContractAuthentication>("measurements-test", _ => { });
            services.AddSingleton(current);
            services.AddSingleton(Substitute.For<IEmployeeRepository>());
            services.AddScoped<IPatientMeasurementRepository, PatientMeasurementRepository>();
        });
    }

    private static void AssertMeasurementProvenance(JsonElement row, string source, string? channel, string? reporter)
    {
        Assert.Equal("heart_rate", row.GetProperty("metricCode").GetString());
        Assert.Equal(source, row.GetProperty("source").GetString());
        Assert.Equal(channel, row.GetProperty("sourceChannel").GetString());
        Assert.Equal(reporter, row.GetProperty("reportedBy").GetString());
        Assert.Equal("confirmed", row.GetProperty("status").GetString());
    }

    private sealed class MeasurementContractAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Guid.TryParse(Request.Headers["X-Test-Measurement-Auth"], out var userId))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
