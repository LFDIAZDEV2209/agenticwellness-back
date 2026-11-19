using CoppAddresd.Application.Features.HealthTests;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Enums.HealthTests;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace CoppAddresd.Infrastructure.Services;

public sealed class InitialBatteryMeasurementSync(AppDbContext db, ILogger<InitialBatteryMeasurementSync> logger)
    : IInitialBatteryMeasurementSync
{
    private sealed record PendingBattery(Guid BatteryId, Guid PatientId, DateTime DueAt);
    private sealed record Measurement(Guid MetricId, Guid UnitId, Guid EvaluationId, decimal Value, DateTime ObservedAt,
        string IdempotencyKey);

    private IQueryable<PendingBattery> PendingBatteries() => db.Database.SqlQuery<PendingBattery>($"""
        SELECT ba.id AS "BatteryId", ba.patient_id AS "PatientId", ba.measurements_sync_due_at AS "DueAt"
        FROM app.health_test_battery_assignments ba
        JOIN app.health_test_batteries b ON b.id = ba.battery_id
        WHERE b.code = 'bateria-inicial' AND ba.status = 'completed' AND ba.measurements_sync_due_at <= now()
        """);

    public async Task<int> ReconcileBatchAsync(CancellationToken ct)
    {
        var batch = await PendingBatteries().OrderBy(b => b.DueAt).ThenBy(b => b.BatteryId).Take(50).ToListAsync(ct);
        foreach (var battery in batch)
            await SyncAsync(battery.PatientId, battery.BatteryId, ct);

        return batch.Count;
    }

    public async Task SyncAsync(Guid patientId, Guid batteryAssignmentId, CancellationToken ct)
    {
        var pending = PendingBatteries().Where(b => b.PatientId == patientId && b.BatteryId == batteryAssignmentId);
        if (!await pending.AnyAsync(ct)) return;

        try
        {
            await db.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(token);
                // El submit toma el mismo bloqueo primero. La FK también coordina altas de mediciones concurrentes.
                var timezones = await db.Database.SqlQuery<string>($"""
                    SELECT timezone AS "Value" FROM app.patient_profiles WHERE id = {patientId} FOR UPDATE
                    """).ToListAsync(token);
                if (timezones.Count == 0 || !await pending.AnyAsync(token)) return;

                var measurements = await BuildMeasurementsAsync(patientId, batteryAssignmentId, ResolveZone(timezones[0]), token);
                if (measurements.Count > 0)
                    await InsertAsync(patientId, measurements, token);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE app.health_test_battery_assignments SET measurements_sync_due_at = NULL
                    WHERE id = {batteryAssignmentId}
                    """, token);
                await transaction.CommitAsync(token);
            }, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Reintentando mediciones de la batería {BatteryId}: tipo={FailureType}",
                batteryAssignmentId, ex.GetType().Name);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE app.health_test_battery_assignments SET measurements_sync_due_at = now() + interval '30 seconds'
                WHERE id = {batteryAssignmentId} AND measurements_sync_due_at <= now()
                """, ct);
        }
    }

    private async Task<List<Measurement>> BuildMeasurementsAsync(
        Guid patientId, Guid batteryId, TimeZoneInfo zone, CancellationToken ct)
    {
        var codes = InitialBatteryMeasurementMapping.QuestionCodes;
        var answers = await db.HealthTestResponses.AsNoTracking()
            .Where(r => r.Evaluation!.PatientId == patientId
                && r.Evaluation.Assignment!.PatientId == patientId
                && r.Evaluation.Assignment.BatteryAssignmentId == batteryId
                && r.Evaluation.Assignment.Status == HealthTestAssignmentStatus.completed
                && r.Evaluation.Status == HealthTestEvaluationStatus.completed && r.Evaluation.CompletedAt != null
                && r.Evaluation.Assignment.VersionId == r.Evaluation.VersionId
                && r.Evaluation.Version!.Instrument!.Code == InitialBatteryMeasurementMapping.InstrumentCode
                && r.Question!.VersionId == r.Evaluation.VersionId
                && r.Question.Type == HealthTestQuestionType.num && codes.Contains(r.Question.Code)
                && r.ValueText != null && r.AnswerOptionId == null)
            .OrderByDescending(r => r.Evaluation!.CompletedAt).ThenBy(r => r.Id)
            .Select(r => new { r.EvaluationId, Observed = r.Evaluation!.CompletedAt!.Value, r.Question!.Code,
                r.Question.Unit, r.Question.MinValue, r.Question.MaxValue, Answer = r.ValueText! })
            .ToListAsync(ct);

        var candidates = answers.Select(a => new { Answer = a,
                Measurement = InitialBatteryMeasurementMapping.Map(a.Code, a.Unit, a.MinValue, a.MaxValue, a.Answer) })
            .Where(a => a.Measurement.HasValue)
            .Select(a => new { a.Answer.EvaluationId, a.Answer.Observed, Measurement = a.Measurement!.Value })
            .ToList();
        if (candidates.Count == 0) return [];

        var metricCodes = candidates.Select(c => c.Measurement.Metric).Distinct().ToArray();
        var metrics = await db.MeasurementMetrics.AsNoTracking()
            .Where(m => m.IsActive && m.DefaultUnit!.IsActive && metricCodes.Contains(m.Code))
            .Select(m => new { m.Id, m.Code, m.DefaultUnitId, UnitCode = m.DefaultUnit!.Code })
            .ToDictionaryAsync(m => m.Code, ct);
        if (candidates.Any(c => !metrics.TryGetValue(c.Measurement.Metric, out var metric) || metric.UnitCode != c.Measurement.Unit))
            throw new InvalidOperationException("El catálogo de mediciones no coincide con la unidad de la batería inicial.");

        var metricIds = metrics.Values.Select(m => m.Id).ToArray();
        var first = candidates.Min(c => c.Observed).AddDays(-2);
        var last = candidates.Max(c => c.Observed).AddDays(2);
        var existing = await db.ClinicalMeasurements.AsNoTracking()
            .Where(m => m.PatientId == patientId && metricIds.Contains(m.MetricId)
                && m.ObservedAt >= first && m.ObservedAt < last)
            .Select(m => new { m.MetricId, m.ObservedAt }).ToListAsync(ct);
        var occupiedDays = existing.Select(m => (m.MetricId, Day: LocalDay(m.ObservedAt, zone))).ToHashSet();

        var result = new List<Measurement>();
        foreach (var candidate in candidates)
        {
            var metric = metrics[candidate.Measurement.Metric];
            if (!occupiedDays.Add((metric.Id, LocalDay(candidate.Observed, zone)))) continue;

            result.Add(new Measurement(metric.Id, metric.DefaultUnitId, candidate.EvaluationId, candidate.Measurement.Value,
                candidate.Observed, $"health_test:bateria_inicial:{candidate.EvaluationId:D}:{metric.Code}"));
        }
        return result;
    }

    private Task InsertAsync(Guid patientId, List<Measurement> measurements, CancellationToken ct)
    {
        var metricIds = measurements.Select(m => m.MetricId).ToArray();
        var unitIds = measurements.Select(m => m.UnitId).ToArray();
        var evaluationIds = measurements.Select(m => m.EvaluationId).ToArray();
        var values = measurements.Select(m => m.Value).ToArray();
        var observations = measurements.Select(m => m.ObservedAt).ToArray();
        var keys = measurements.Select(m => m.IdempotencyKey).ToArray();
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO app.clinical_measurements (patient_id, metric_id, unit_id, batch_id, value, observed_at, source, idempotency_key)
            SELECT {patientId}, x.metric_id, x.unit_id, x.evaluation_id, x.value, x.observed_at, 'health_test', x.key
            FROM unnest({metricIds}, {unitIds}, {evaluationIds}, {values}, {observations}, {keys})
                AS x(metric_id, unit_id, evaluation_id, value, observed_at, key)
            ON CONFLICT (patient_id, idempotency_key) WHERE idempotency_key IS NOT NULL DO NOTHING
            """, ct);
    }

    private static DateOnly LocalDay(DateTime utc, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, zone));

    private static TimeZoneInfo ResolveZone(string timezone)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(timezone); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }
}
