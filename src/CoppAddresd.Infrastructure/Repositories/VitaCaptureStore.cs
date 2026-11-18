using System.Globalization;
using CoppAddresd.Application.Features.Agents.Vita;
using CoppAddresd.Application.Features.Medications;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

/// <summary>Serializa las confirmaciones por paciente y guarda el recibo junto al resultado canónico.</summary>
public sealed class VitaCaptureStore(AppDbContext db, IClinicalMeasurementRepository measurements,
    MedicationControlStore medications, ILegalConsentService consent, TimeProvider clock, ProgramRepository programs) : IVitaCaptureStore
{
    public async Task ValidateMeasurementAsync(Guid patientId, VitaCaptureData data, CancellationToken ct)
        => await ResolveMeasurementAsync(patientId, data, ct);

    public Task<VitaCaptureCommit> ConfirmAsync(VitaCaptureProposal proposal, CancellationToken ct)
    {
        var data = proposal.Data;
        var patientId = proposal.PatientId;
        var hash = data.Program is { } program ? VitaVersions.Hash(new
        {
            data.Tool, data.ObservedAt, program.EnrollmentId, program.LocalDate, program.MoodScore, program.Meal?.MealCode,
            Calories = FingerprintValue(program.Meal?.Calories), Protein = FingerprintValue(program.Meal?.ProteinG),
            Carbs = FingerprintValue(program.Meal?.CarbsG), Fat = FingerprintValue(program.Meal?.FatG),
            Fiber = FingerprintValue(program.Meal?.FiberG),
        }) : VitaVersions.Hash(new
        {
            data.Tool, data.MetricCode, Value = data.Value?.ToString("G29", CultureInfo.InvariantCulture), data.UnitCode,
            data.ObservedAt, data.SupersedesId, data.DoseId, data.ScheduledAt, data.Status,
        });
        return db.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            // Misma exclusión que la aceptación y revocación de consentimiento.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT pg_advisory_xact_lock(hashtextextended({proposal.UserId.ToString()}, 0))
                """, token);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT id FROM app.patient_profiles WHERE id = {patientId} FOR UPDATE
                """, token);
            var ownsProfile = await db.PatientProfiles.AsNoTracking().AnyAsync(p => p.Id == patientId
                && p.UserId == proposal.UserId && p.DeletedAt == null && p.Status == "Activo", token);
            if (!ownsProfile) throw new ForbiddenException("VITA_CAPTURE_OWNER_MISMATCH");
            await consent.EnsureUserAsync(proposal.UserId, "chat", token);
            var now = clock.GetUtcNow();
            if (proposal.ExpiresAt <= now) throw VitaCaptureRules.Invalid("token", "VITA_CAPTURE_EXPIRED");
            if (data.Tool == VitaCaptureRules.Medication && !(await medications.GetAccessAsync(patientId, token)).Enabled)
                throw new ForbiddenException("VITA_MEDICATIONS_FORBIDDEN");

            var receipt = await db.VitaWriteReceipts.AsNoTracking().SingleOrDefaultAsync(r => r.PatientId == patientId
                && r.Channel == proposal.Channel && r.TurnId == proposal.TurnId && r.ToolName == data.Tool, token);
            if (receipt is not null)
            {
                if (receipt.RequestHash != hash) throw new BusinessRuleViolationException("VITA_CAPTURE_IDEMPOTENCY_CONFLICT");
                await transaction.CommitAsync(token);
                return new VitaCaptureCommit(ToResult(receipt));
            }

            var (entityType, entityId, effects) = await WriteEntityAsync(proposal, now, token);
            receipt = new VitaWriteReceipt
            {
                Id = Guid.NewGuid(), PatientId = patientId, Channel = proposal.Channel, TurnId = proposal.TurnId,
                ToolName = data.Tool, RequestHash = hash, EntityType = entityType, EntityId = entityId,
                CreatedAt = now.UtcDateTime,
            };
            db.VitaWriteReceipts.Add(receipt);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return new VitaCaptureCommit(ToResult(receipt), effects);
        }, ct);
    }

    private async Task<(string EntityType, Guid EntityId, VitaProgramCaptureEffects? Effects)> WriteEntityAsync(
        VitaCaptureProposal proposal, DateTimeOffset now, CancellationToken ct)
    {
        switch (proposal.Data.Tool)
        {
            case VitaCaptureRules.Measurement:
                return ("clinical_measurement", await InsertMeasurementAsync(proposal, now.UtcDateTime, ct), null);
            case VitaCaptureRules.Medication:
                return ("medication_dose", await RecordDoseAsync(proposal, now, ct), null);
            case VitaCaptureRules.Meal:
                var meal = await programs.RecordVitaMealAsync(proposal, ct);
                var intakeId = await db.NutritionIntakeLogs.AsNoTracking().Where(x => x.HabitCheckId == meal.HabitCheckId)
                    .Select(x => x.Id).SingleAsync(ct);
                return ("nutrition_intake_log", intakeId, new(meal.LocalDate, null, $"NUTRITION_{meal.MealCode}", meal.XpAwarded));
            case VitaCaptureRules.CheckIn:
                var checkin = await programs.RecordVitaCheckInAsync(proposal, ct);
                var checkinId = await db.TaskCompletions.AsNoTracking().Where(x => x.Id == checkin.TaskCompletionId)
                    .Select(x => x.DailyCheckinId).SingleAsync(ct);
                return ("daily_checkin", checkinId, new(proposal.Data.Program!.LocalDate,
                    "emocional", "TASK_emocional", checkin.PointsAwarded));
            default:
                throw VitaCaptureRules.Invalid("tool", "VITA_CAPTURE_INVALID_PROGRAM");
        }
    }

    private static string? FingerprintValue(decimal? value) => value?.ToString("G29", CultureInfo.InvariantCulture);

    private async Task<Guid> InsertMeasurementAsync(VitaCaptureProposal proposal, DateTime now, CancellationToken ct)
    {
        var data = proposal.Data;
        var catalog = await ResolveMeasurementAsync(proposal.PatientId, data, ct);
        var measurement = new ClinicalMeasurement
        {
            Id = Guid.NewGuid(), PatientId = proposal.PatientId, MetricId = catalog.MetricId, UnitId = catalog.UnitId,
            Value = data.Value!.Value, ObservedAt = data.ObservedAt.UtcDateTime, RecordedAt = now, CreatedAt = now,
            CreatedBy = proposal.UserId, Source = "patient", SourceChannel = proposal.Channel,
            ReportedBy = "patient", Status = "confirmed", SupersedesId = data.SupersedesId,
            IdempotencyKey = VitaVersions.Hash(new { proposal.Channel, proposal.TurnId, data.Tool }),
            Notes = VitaCaptureRules.RequiresAdditionalConfirmation(data) ? "VITA_REVIEW_REQUIRED" : null,
        };
        await measurements.AddBatchAsync([measurement], ct);
        return measurement.Id;
    }

    private async Task<MeasurementCatalog> ResolveMeasurementAsync(Guid patientId, VitaCaptureData data, CancellationToken ct)
    {
        var catalog = await db.MeasurementMetrics.AsNoTracking()
            .Where(m => m.IsActive && m.Code == data.MetricCode && m.DefaultUnit!.IsActive && m.DefaultUnit.Code == data.UnitCode)
            .Select(m => new MeasurementCatalog(m.Id, m.DefaultUnitId))
            .SingleOrDefaultAsync(ct) ?? throw VitaCaptureRules.Invalid("unitCode", "VITA_CAPTURE_CATALOG_UNAVAILABLE");

        if (data.SupersedesId is not { } previousId) return catalog;
        var previous = await db.ClinicalMeasurements.AsNoTracking().Where(m => m.Id == previousId && m.PatientId == patientId)
            .Select(m => new { m.Source, m.ReportedBy, m.SourceChannel, m.Status, m.MetricId, m.UnitId })
            .SingleOrDefaultAsync(ct);
        if (previous is null || previous.Source != "patient" || previous.ReportedBy is not (null or "patient")
            || previous.SourceChannel is "professional" or "lab" or "device")
            throw new ForbiddenException("VITA_CAPTURE_CORRECTION_FORBIDDEN");
        if (previous.MetricId != catalog.MetricId || previous.UnitId != catalog.UnitId)
            throw VitaCaptureRules.Invalid("supersedesId", "VITA_CAPTURE_CORRECTION_METRIC_MISMATCH");
        if (previous.Status != "confirmed" || !await db.EffectiveClinicalMeasurements(patientId)
            .AnyAsync(m => m.Id == previousId, ct))
            throw new BusinessRuleViolationException("VITA_CAPTURE_ALREADY_REPLACED");
        return catalog;
    }

    private async Task<Guid> RecordDoseAsync(VitaCaptureProposal proposal, DateTimeOffset now, CancellationToken ct)
    {
        var data = proposal.Data;
        var request = MedicationControlService.ValidateRecord(new RecordMedicationDoseRequest(
            proposal.Nonce, data.Status!, data.ObservedAt, ScheduledAt: data.ScheduledAt), now);
        var dose = await medications.RecordWithinPatientTransactionAsync(proposal.PatientId, proposal.UserId,
            "patient_reported", data.DoseId!.Value, request, ct);
        return dose.Id;
    }

    private static VitaCaptureReceiptDto ToResult(VitaWriteReceipt receipt) => new(receipt.Id, receipt.EntityType, receipt.EntityId);

    private sealed record MeasurementCatalog(Guid MetricId, Guid UnitId);
}
