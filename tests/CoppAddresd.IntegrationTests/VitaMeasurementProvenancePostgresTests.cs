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

        var current = Substitute.For<ICurrentContext>();
        current.UserId.Returns(userId);
        current.HasPermissionAsync("Patients.View", Arg.Any<CancellationToken>()).Returns(true);
        await using var host = await VitaAHostAsync(captureEnabled: true, configureServices: services =>
        {
            services.AddAuthentication("measurements-test")
                .AddScheme<AuthenticationSchemeOptions, MeasurementContractAuthentication>("measurements-test", _ => { });
            services.AddSingleton(current);
            services.AddSingleton(Substitute.For<IEmployeeRepository>());
            services.AddScoped<IPatientMeasurementRepository, PatientMeasurementRepository>();
        });
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
