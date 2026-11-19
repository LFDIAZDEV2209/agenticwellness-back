using System.Net;
using System.Net.Http.Json;
using CoppAddresd.Application.Features.Agents.Vita;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    private const string CaptureProposePath = VitaARoute + "/capture/propose";
    private const string CaptureConfirmPath = VitaARoute + "/capture/confirm";

    [NutritionPostgresFact]
    public async Task VitaCapture_Measurement_PersistsOneReceiptUnderConcurrentReplay_AndReadsIt()
    {
        await using var db = Context();
        var patient = await PatientAsync(db);
        await VitaAMetricAsync(db, "weight", "kg", "kg", "body_comp");
        await using var host = await VitaAHostAsync(captureEnabled: true);
        using var client = VitaAClient(host);
        var input = WeightCapture("same-turn");
        var proposal = await ProposeCaptureAsync(client, patient.UserId!.Value, input);
        Assert.Equal(TimeSpan.Zero, proposal.Data.ObservedAt.Offset);
        Assert.Equal(VitaANow.AddMinutes(5), proposal.ExpiresAt);
        Assert.Equal(0, await db.ClinicalMeasurements.CountAsync());
        Assert.Equal(0, await db.VitaWriteReceipts.CountAsync());

        var responses = await Task.WhenAll(ConfirmCaptureAsync(client, patient.UserId.Value, input, proposal.Token),
            ConfirmCaptureAsync(client, patient.UserId.Value, input, proposal.Token));
        Assert.Equal(responses[0], responses[1]);
        var measurement = await db.ClinicalMeasurements.AsNoTracking().SingleAsync();
        Assert.Equal(responses[0].EntityId, measurement.Id);
        Assert.Equal("vita_voice", measurement.SourceChannel);
        Assert.Equal("patient", measurement.ReportedBy);
        Assert.Equal("confirmed", measurement.Status);
        Assert.NotNull(measurement.IdempotencyKey);
        Assert.Equal(responses[0].ReceiptId, (await db.VitaWriteReceipts.AsNoTracking().SingleAsync()).Id);

        var reissued = await ProposeCaptureAsync(client, patient.UserId.Value, input with { Value = 74.50m });
        Assert.Equal(responses[0], await ConfirmCaptureAsync(client, patient.UserId.Value, input, reissued.Token));
        var changed = await ProposeCaptureAsync(client, patient.UserId.Value, input with { Value = 75m });
        using var conflict = await SendConfirmCaptureAsync(client, patient.UserId.Value, input, changed.Token);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var read = await client.GetFromJsonAsync<VitaProfileDto>($"{VitaARoute}/profile?userId={patient.UserId}");
        Assert.Equal(74.5m, read!.Anthropometry.Weight!.Value);
        Assert.Equal(measurement.Id, read.Anthropometry.Weight.MeasurementId);
    }

    [NutritionPostgresFact]
    public async Task VitaCapture_InvalidExpiredAndForeignTokens_LeaveNoRows()
    {
        await using var db = Context();
        var patient = await PatientAsync(db);
        var other = await PatientAsync(db);
        await VitaAMetricAsync(db, "weight", "kg", "kg", "body_comp");
        var clock = new CaptureClock();
        await using var host = await VitaAHostAsync(captureEnabled: true, clock: clock);
        using var client = VitaAClient(host);
        using var noKey = VitaAClient(host, attachKey: false);
        var input = WeightCapture("invalid-tests");
        using var rejected = await noKey.PostAsJsonAsync($"{CaptureProposePath}?userId={patient.UserId}", input);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        using var invalidUnit = await client.PostAsJsonAsync($"{CaptureProposePath}?userId={patient.UserId}", input with { UnitCode = "lb" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidUnit.StatusCode);
        using var invalidRange = await client.PostAsJsonAsync($"{CaptureProposePath}?userId={patient.UserId}", input with { Value = 501m });
        Assert.Equal(HttpStatusCode.BadRequest, invalidRange.StatusCode);

        var proposal = await ProposeCaptureAsync(client, patient.UserId!.Value, input);
        var parts = proposal.Token.Split('.');
        parts[2] = (parts[2][0] == 'A' ? "B" : "A") + parts[2][1..];
        using var tampered = await SendConfirmCaptureAsync(client, patient.UserId.Value, input, string.Join('.', parts));
        Assert.Equal(HttpStatusCode.BadRequest, tampered.StatusCode);
        using var notConfirmed = await SendConfirmCaptureAsync(client, patient.UserId.Value, input, proposal.Token, confirmed: false);
        Assert.Equal(HttpStatusCode.BadRequest, notConfirmed.StatusCode);
        using var otherPatient = await SendConfirmCaptureAsync(client, other.UserId!.Value, input, proposal.Token);
        Assert.Equal(HttpStatusCode.Forbidden, otherPatient.StatusCode);
        using var wrongChannel = await SendConfirmCaptureAsync(client, patient.UserId.Value, input with { Channel = "vita_chat" }, proposal.Token);
        Assert.Equal(HttpStatusCode.Conflict, wrongChannel.StatusCode);
        using var wrongTurn = await SendConfirmCaptureAsync(client, patient.UserId.Value, input with { TurnId = "other-turn" }, proposal.Token);
        Assert.Equal(HttpStatusCode.Conflict, wrongTurn.StatusCode);
        clock.UtcNow = proposal.ExpiresAt;
        using var expired = await SendConfirmCaptureAsync(client, patient.UserId.Value, input, proposal.Token);
        Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
        Assert.Equal(0, await db.ClinicalMeasurements.CountAsync());
        Assert.Equal(0, await db.VitaWriteReceipts.CountAsync());

        await using var disabled = await VitaAHostAsync();
        using var disabledClient = VitaAClient(disabled);
        using var disabledCapture = await disabledClient.PostAsJsonAsync($"{CaptureProposePath}?userId={patient.UserId}", input);
        Assert.Equal(HttpStatusCode.NotFound, disabledCapture.StatusCode);
    }

    [NutritionPostgresFact]
    public async Task VitaCapture_Correction_AppendsAndPreservesOriginalAndProfessionalRows()
    {
        await using var db = Context();
        var patient = await PatientAsync(db);
        var (weight, kg) = await VitaAMetricAsync(db, "weight", "kg", "kg", "body_comp");
        var previous = VitaAReading(patient.Id, weight, kg, VitaANow.UtcDateTime.AddHours(-2), 80m, "patient");
        var professional = VitaAReading(patient.Id, weight, kg, VitaANow.UtcDateTime.AddHours(-4), 78m, "professional");
        db.ClinicalMeasurements.AddRange(previous, professional);
        await db.SaveChangesAsync();

        await using var host = await VitaAHostAsync(captureEnabled: true);
        using var client = VitaAClient(host);
        var input = WeightCapture("correction") with { SupersedesId = previous.Id, ObservedAt = VitaANow.AddHours(-3) };
        var competing = input with { TurnId = "competing-correction" };
        var proposal = await ProposeCaptureAsync(client, patient.UserId!.Value, input);
        var race = await ProposeCaptureAsync(client, patient.UserId.Value, competing);
        var result = await ConfirmCaptureAsync(client, patient.UserId.Value, input, proposal.Token);
        using var stale = await SendConfirmCaptureAsync(client, patient.UserId.Value, competing, race.Token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var forbidden = await client.PostAsJsonAsync($"{CaptureProposePath}?userId={patient.UserId}",
            WeightCapture("forbidden") with { SupersedesId = professional.Id });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        var rows = await db.ClinicalMeasurements.AsNoTracking().ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.Equal(previous.Id, rows.Single(x => x.Id == result.EntityId).SupersedesId);
        Assert.Equal(80m, rows.Single(x => x.Id == previous.Id).Value);
        Assert.Equal(78m, rows.Single(x => x.Id == professional.Id).Value);

        var read = await client.GetFromJsonAsync<VitaProfileDto>($"{VitaARoute}/profile?userId={patient.UserId}");
        Assert.Equal(result.EntityId, read!.Anthropometry.Weight!.MeasurementId);
        Assert.DoesNotContain(read.Anthropometry.History, m => m.MeasurementId == previous.Id);
        Assert.Contains(read.Anthropometry.History, m => m.MeasurementId == professional.Id);
        var erp = await new ClinicalMeasurementRepository(db).ListForErpAsync(patient.Id);
        Assert.Equal(2, erp.Count);
    }

    [NutritionPostgresFact]
    public async Task VitaCapture_UnusualTemperature_RequiresAdditionalConfirmation()
    {
        await using var db = Context();
        var patient = await PatientAsync(db);
        await VitaAMetricAsync(db, "temperature_c", "celsius", "°C", "vital");
        await using var host = await VitaAHostAsync(captureEnabled: true);
        using var client = VitaAClient(host);
        var input = WeightCapture("unusual") with { MetricCode = "temperature_c", UnitCode = "celsius", Value = 45 };
        var proposal = await ProposeCaptureAsync(client, patient.UserId!.Value, input);
        Assert.True(proposal.RequiresAdditionalConfirmation);
        using var rejected = await SendConfirmCaptureAsync(client, patient.UserId.Value, input, proposal.Token);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        await ConfirmCaptureAsync(client, patient.UserId.Value, input, proposal.Token, confirmedUnusual: true);
        Assert.Equal("VITA_REVIEW_REQUIRED", (await db.ClinicalMeasurements.SingleAsync()).Notes);
    }

    [NutritionPostgresFact]
    public async Task VitaCapture_Medications_ReusesCanonicalDosesAndRollsBackOnReceiptFailure()
    {
        await using var db = Context();
        var patient = await PatientAsync(db);
        var clock = new CaptureClock();
        var module = new MedicationControlStore(db, Substitute.For<IPatientScopeGuard>(), clock);
        await module.CreateAsync(patient.Id, patient.UserId!.Value, "patient_reported",
            new(Guid.NewGuid(), null, "Producto QA", "supplement", 1, "unidad", "", "",
                ["08:00", "20:00"], "America/Bogota", VitaAToday.AddDays(-1), null), default);

        await using var host = await VitaAHostAsync(captureEnabled: true, clock: clock);
        using var client = VitaAClient(host);
        var read = await client.GetFromJsonAsync<VitaMedicationsDto>($"{VitaARoute}/medications?userId={patient.UserId}");
        var doses = Assert.Single(read!.Treatments).Doses;
        Assert.Equal(2, doses.Count);
        var first = DoseCapture(doses[0], "taken", "taken-turn") with { Channel = "vita_whatsapp" };
        var second = DoseCapture(doses[1], "omitted", "omitted-turn");
        var firstProposal = await ProposeCaptureAsync(client, patient.UserId.Value, first);
        var secondProposal = await ProposeCaptureAsync(client, patient.UserId.Value, second);
        Assert.Equal(0, await db.MedicationDoses.CountAsync());
        Assert.Equal(0, await db.VitaWriteReceipts.CountAsync());
        var taken = await ConfirmCaptureAsync(client, patient.UserId.Value, first, firstProposal.Token);
        Assert.Equal("medication_dose", taken.EntityType);
        Assert.Equal(doses[0].DoseId, taken.EntityId);
        Assert.Equal(taken, await ConfirmCaptureAsync(client, patient.UserId.Value, first, firstProposal.Token));
        Assert.Equal("vita_whatsapp", (await db.VitaWriteReceipts.SingleAsync()).Channel);

        // Hace fallar el último paso después de que el módulo haya guardado la toma en la transacción.
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE app.vita_write_receipts ADD CONSTRAINT reject_test_receipt CHECK (turn_id <> 'omitted-turn');
            """);
        using var failed = await SendConfirmCaptureAsync(client, patient.UserId.Value, second, secondProposal.Token);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(1, await db.MedicationDoses.CountAsync());
        Assert.Equal(1, await db.MedicationIntakeEvents.CountAsync());
        Assert.Equal(1, await db.MedicationDoseOperations.CountAsync());
        Assert.Equal(1, await db.VitaWriteReceipts.CountAsync());
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE app.vita_write_receipts DROP CONSTRAINT reject_test_receipt");
        var omitted = await ConfirmCaptureAsync(client, patient.UserId.Value, second, secondProposal.Token);
        Assert.Equal(omitted, await ConfirmCaptureAsync(client, patient.UserId.Value, second, secondProposal.Token));
        var workspace = await module.WorkspaceAsync(patient.Id, VitaAToday, "America/Bogota", false, default);
        Assert.Equal(new[] { "taken", "omitted" }, workspace.Doses.Select(d => d.Status));
        Assert.Equal(2, await db.MedicationIntakeEvents.CountAsync());
        Assert.Equal(2, await db.VitaWriteReceipts.CountAsync());
    }

    private static ProposeVitaCaptureRequest WeightCapture(string turn) =>
        new("record_measurement", "vita_voice", turn, VitaANow.AddHours(-1).ToOffset(TimeSpan.FromHours(-5)), "weight", 74.5m, "kg");

    private static ProposeVitaCaptureRequest DoseCapture(VitaMedicationDoseDto dose, string status, string turn) =>
        new("record_medication_dose", "vita_chat", turn, VitaANow.AddMinutes(-2),
            DoseId: dose.DoseId, ScheduledAt: dose.ScheduledAt, Status: status);

    private static async Task<VitaCaptureProposalDto> ProposeCaptureAsync(HttpClient client, Guid userId, ProposeVitaCaptureRequest input)
    {
        using var response = await client.PostAsJsonAsync($"{CaptureProposePath}?userId={userId}", input);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<VitaCaptureProposalDto>())!;
    }

    private static async Task<VitaCaptureReceiptDto> ConfirmCaptureAsync(HttpClient client, Guid userId,
        ProposeVitaCaptureRequest input, string token, bool confirmedUnusual = false)
    {
        using var response = await SendConfirmCaptureAsync(client, userId, input, token, confirmedUnusual: confirmedUnusual);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<VitaCaptureReceiptDto>())!;
    }

    private static Task<HttpResponseMessage> SendConfirmCaptureAsync(HttpClient client, Guid userId,
        ProposeVitaCaptureRequest input, string token, bool confirmed = true, bool confirmedUnusual = false)
        => client.PostAsJsonAsync($"{CaptureConfirmPath}?userId={userId}",
            new ConfirmVitaCaptureRequest(token, input.Channel, input.TurnId, confirmed, confirmedUnusual));

    private sealed class CaptureClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = VitaANow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
