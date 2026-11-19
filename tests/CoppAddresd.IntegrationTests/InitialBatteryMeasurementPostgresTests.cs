using System.Net.Http.Json;
using System.Text.Json;
using CoppAddresd.Application.Features.HealthTests;
using CoppAddresd.Application.Features.HealthTests.Execution;
using CoppAddresd.Application.Features.HealthTests.Scoring;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Entities.HealthTests;
using CoppAddresd.Domain.Enums.HealthTests;
using CoppAddresd.Infrastructure.Persistence;
using CoppAddresd.Infrastructure.Repositories;
using CoppAddresd.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    [NutritionPostgresFact]
    public async Task InitialBatteryMeasurements_FinalSubmitAndReplay_ExposeTwoCanonicalValues()
    {
        await using var db = Context();
        var patient = await PatientAsync(db);
        var (history, last) = await CreateInitialBatteryForMeasurementsAsync(db, patient.Id, anotherTest: true);

        var evaluation = await SubmitMeasurementTestAsync(history, WithMeasurements("75,25", "165"));
        Assert.Empty(await PatientMeasurementsAsync(db, patient.Id));
        var final = await SubmitMeasurementTestAsync(last!, new([], "final-submit"));

        var stored = await PatientMeasurementsAsync(db, patient.Id);
        Assert.Equal(2, stored.Count);
        Assert.All(stored, row =>
        {
            Assert.Equal(evaluation.Id, row.BatchId);
            Assert.Equal(evaluation.CompletedAt, row.ObservedAt);
            Assert.Equal($"health_test:bateria_inicial:{evaluation.Id:D}:{row.Metric!.Code}", row.IdempotencyKey);
        });

        // Fuerza el replay del trabajo durable y el replay HTTP del envío final.
        await MakeMeasurementSyncDueAsync(db, history.BatteryAssignmentId!.Value);
        var finalReplay = await SubmitMeasurementTestAsync(last!, new([], "final-submit"));
        Assert.Equal(final.Id, finalReplay.Id);
        Assert.Equal(stored.Select(m => m.Id), (await PatientMeasurementsAsync(db, patient.Id)).Select(m => m.Id));
        Assert.Equal(0, await MeasurementSync(db).ReconcileBatchAsync(CancellationToken.None));

        await using var host = await MeasurementContractHostAsync(patient.UserId!.Value);
        using var client = VitaAClient(host);
        client.DefaultRequestHeaders.Add("X-Test-Measurement-Auth", patient.UserId.Value.ToString());
        var response = await client.GetFromJsonAsync<JsonElement>("/api/v1/me/measurements?codes=weight,height");
        var items = response.GetProperty("items").EnumerateArray().ToDictionary(r => r.GetProperty("metricCode").GetString()!);
        Assert.Equal(2, items.Count);
        VerifyItem(items["weight"], "kg", 75.25m);
        VerifyItem(items["height"], "cm", 165m);

        void VerifyItem(JsonElement item, string unit, decimal value)
        {
            Assert.Equal(value, item.GetProperty("value").GetDecimal());
            Assert.Equal(unit, item.GetProperty("unitCode").GetString());
            Assert.Equal(evaluation.CompletedAt, item.GetProperty("observedAt").GetDateTimeOffset().UtcDateTime);
            Assert.Equal("health_test", item.GetProperty("source").GetString());
            Assert.Equal("confirmed", item.GetProperty("status").GetString());
            Assert.False(item.TryGetProperty("batchId", out _));
        }
    }

    [NutritionPostgresFact]
    public async Task InitialBatteryMeasurements_ConcurrentBatteries_KeepProfessionalSameDayAndLatest()
    {
        await using var db = Context();
        var patient = await PatientAsync(db);
        var (first, _) = await CreateInitialBatteryForMeasurementsAsync(db, patient.Id);
        var (second, _) = await CreateInitialBatteryForMeasurementsAsync(db, patient.Id);
        var firstEvaluation = await SubmitMeasurementTestAsync(first, WithMeasurements("75", "165"), sync: false);
        var secondEvaluation = await SubmitMeasurementTestAsync(second, WithMeasurements("76", "166"), sync: false);
        var submittedAt = new DateTime(2026, 10, 12, 2, 0, 0, DateTimeKind.Utc);
        await db.HealthTestEvaluations.Where(e => e.Id == firstEvaluation.Id || e.Id == secondEvaluation.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.CompletedAt, submittedAt));

        var weight = await db.MeasurementMetrics.SingleAsync(m => m.Code == "weight");
        var height = await db.MeasurementMetrics.SingleAsync(m => m.Code == "height");
        // Las dos fechas UTC del peso pertenecen al mismo día en Bogotá.
        var professionalWeight = ProfessionalMeasurement(weight, 81m, submittedAt.AddHours(-3));
        var professionalHeight = ProfessionalMeasurement(height, 170m, submittedAt.AddDays(1));
        db.ClinicalMeasurements.AddRange(professionalWeight, professionalHeight);
        await db.SaveChangesAsync();

        await using var db1 = Context();
        await using var db2 = Context();
        await Task.WhenAll(
            MeasurementSync(db1).SyncAsync(patient.Id, first.BatteryAssignmentId!.Value, CancellationToken.None),
            MeasurementSync(db2).SyncAsync(patient.Id, second.BatteryAssignmentId!.Value, CancellationToken.None));

        var stored = await PatientMeasurementsAsync(db, patient.Id);
        Assert.Equal(3, stored.Count);
        var onlyWeight = Assert.Single(stored, m => m.MetricId == weight.Id);
        Assert.Equal(professionalWeight.Id, onlyWeight.Id);
        Assert.Equal(81m, onlyWeight.Value);
        var latestHeight = stored.Where(m => m.MetricId == height.Id).MaxBy(m => m.ObservedAt)!;
        Assert.Equal(professionalHeight.Id, latestHeight.Id);
        Assert.Equal(170m, latestHeight.Value);
        var imported = Assert.Single(stored, m => m.Source == "health_test");
        Assert.Equal(height.Id, imported.MetricId);
        Assert.Equal(submittedAt, imported.ObservedAt);
        Assert.Null(imported.SupersedesId);

        ClinicalMeasurement ProfessionalMeasurement(MeasurementMetric metric, decimal value, DateTime observedAt) => new()
        {
            PatientId = patient.Id, MetricId = metric.Id, UnitId = metric.DefaultUnitId,
            Source = "professional", SourceChannel = "professional", Value = value, ObservedAt = observedAt,
        };
    }

    [NutritionPostgresFact]
    public async Task InitialBatteryMeasurements_PostCommitFailure_RollsBackImportAndWorkerRetries()
    {
        await using var db = Context();
        var patient = await PatientAsync(db);
        var (assignment, _) = await CreateInitialBatteryForMeasurementsAsync(db, patient.Id);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_measurement_ack() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'simulated acknowledgement failure'; END; $$;
            CREATE TRIGGER fail_measurement_ack BEFORE UPDATE OF measurements_sync_due_at
                ON app.health_test_battery_assignments FOR EACH ROW
                WHEN (NEW.measurements_sync_due_at IS NULL) EXECUTE FUNCTION fail_measurement_ack();
            """);

        var evaluation = await SubmitMeasurementTestAsync(assignment, WithMeasurements("75", "250"));
        Assert.Equal(HealthTestEvaluationStatus.completed,
            await db.HealthTestEvaluations.Where(e => e.Id == evaluation.Id).Select(e => e.Status).SingleAsync());
        Assert.Empty(await PatientMeasurementsAsync(db, patient.Id));

        await using var workerDb = Context();
        Assert.Equal(0, await MeasurementSync(workerDb).ReconcileBatchAsync(CancellationToken.None));
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_measurement_ack ON app.health_test_battery_assignments");
        await MakeMeasurementSyncDueAsync(db, assignment.BatteryAssignmentId!.Value);
        Assert.Equal(1, await MeasurementSync(workerDb).ReconcileBatchAsync(CancellationToken.None));
        var item = Assert.Single(await PatientMeasurementsAsync(db, patient.Id));
        Assert.Equal("weight", item.Metric!.Code);
        Assert.Equal(75m, item.Value);
        Assert.Equal(0, await MeasurementSync(workerDb).ReconcileBatchAsync(CancellationToken.None));
    }

    private async Task<(HealthTestAssignment History, HealthTestAssignment? Other)> CreateInitialBatteryForMeasurementsAsync(
        AppDbContext db, Guid patientId, bool anotherTest = false)
    {
        var battery = await db.HealthTestBatteries.SingleAsync(b => b.Code == "bateria-inicial");
        var group = new HealthTestBatteryAssignment { PatientId = patientId, BatteryId = battery.Id };
        db.HealthTestBatteryAssignments.Add(group);
        var history = await AddTestAsync(InitialBatteryMeasurementMapping.InstrumentCode);
        var other = anotherTest ? await AddTestAsync("temperamento") : null;
        await db.SaveChangesAsync();
        return (history, other);

        async Task<HealthTestAssignment> AddTestAsync(string code)
        {
            var version = await db.HealthTestVersions.SingleAsync(v => v.IsCurrent && v.Instrument!.Code == code);
            var assignment = new HealthTestAssignment { PatientId = patientId, BatteryAssignment = group, VersionId = version.Id };
            db.HealthTestAssignments.Add(assignment);
            return assignment;
        }
    }

    private static SubmitEvaluationRequest WithMeasurements(string weight, string height) => new([
        new(Guid.Parse("817090ff-c4b4-5a50-ae87-bad44585e9f2"), null, weight),
        new(Guid.Parse("ca9f42a9-d070-5d45-955f-dea717b3f2c5"), null, height),
    ], "history-submit");

    private async Task<HealthTestEvaluationDto> SubmitMeasurementTestAsync(
        HealthTestAssignment assignment, SubmitEvaluationRequest request, bool sync = true)
    {
        await using var db = Context();
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IScoreStrategy, InventoryScoreStrategy>(HealthTestScoringStrategy.inventory);
        services.AddKeyedSingleton<IScoreStrategy, SubscaleScoreStrategy>(HealthTestScoringStrategy.subscale);
        await using var provider = services.BuildServiceProvider();
        var handler = new SubmitEvaluationCommandHandler(new HealthTestRepository(db), new ScoreStrategyRegistry(provider),
            new ScoreRangeEngine(), new IndicatorEngine(), new AlertEngine(), assignmentRepository: new ProgramAssignmentRepository(db),
            measurementSync: sync ? MeasurementSync(db) : null);
        return await handler.Handle(new SubmitEvaluationCommand(assignment.Id, request), CancellationToken.None);
    }

    private static InitialBatteryMeasurementSync MeasurementSync(AppDbContext db) =>
        new(db, NullLogger<InitialBatteryMeasurementSync>.Instance);

    private static Task<List<ClinicalMeasurement>> PatientMeasurementsAsync(AppDbContext db, Guid patientId) =>
        db.ClinicalMeasurements.AsNoTracking().Include(m => m.Metric).Where(m => m.PatientId == patientId)
            .OrderBy(m => m.Id).ToListAsync();

    private static Task MakeMeasurementSyncDueAsync(AppDbContext db, Guid batteryId) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE app.health_test_battery_assignments SET measurements_sync_due_at = now() WHERE id = {batteryId}
            """);
}
