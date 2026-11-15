using System.Data;
using CoppAddresd.Application.Features.ProgramProgress.PatientSummary;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Enums.HealthTests;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

/// <summary>Lecturas agregadas por paciente/intervalo, sin texto clínico libre ni consultas por registro.</summary>
public sealed class PatientSummaryStore(AppDbContext db) : IPatientSummaryStore
{
    public Task<string> TimezoneAsync(Guid patientId, CancellationToken ct)
        => db.PatientProfiles.AsNoTracking().Where(p => p.Id == patientId && p.DeletedAt == null).Select(p => p.Timezone).SingleAsync(ct);

    public Task<PatientSummaryData> ReadAsync(Guid patientId, DateOnly from, DateOnly to, DateTime fromUtc, DateTime capturedAtUtc, CancellationToken ct)
        => db.Database.CurrentTransaction is not null ? ReadCoreAsync(patientId, from, to, fromUtc, capturedAtUtc, ct)
        : db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            var result = await ReadCoreAsync(patientId, from, to, fromUtc, capturedAtUtc, ct);
            await snapshot.CommitAsync(ct);
            return result;
        });

    private async Task<PatientSummaryData> ReadCoreAsync(Guid patientId, DateOnly from, DateOnly to, DateTime fromUtc, DateTime capturedAtUtc, CancellationToken ct)
    {
        var program = await db.ProgramEnrollments.AsNoTracking()
            .Where(e => e.PatientId == patientId && e.StartLocalDate <= to && e.CreatedAt <= capturedAtUtc)
            .OrderByDescending(e => e.StartLocalDate).ThenByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id)
            .Select(e => new { e.Status, e.CurrentWeekNumber, e.StartLocalDate }).FirstOrDefaultAsync(ct);
        var food = db.NutritionIntakeLogs.AsNoTracking().Where(l => l.PatientId == patientId && l.LocalDate >= from && l.LocalDate <= to
            && l.CreatedAt <= capturedAtUtc && l.MealCode != MealCode.agua);
        var foodCount = await food.CountAsync(ct);
        var foodDays = await food.Select(l => l.LocalDate).Distinct().CountAsync(ct);
        var tasks = db.TaskCompletions.AsNoTracking().Where(t => t.Enrollment!.PatientId == patientId
            && t.LocalDate >= from && t.LocalDate <= to && t.CompletedAt <= capturedAtUtc);
        var exercise = await tasks.CountAsync(t => t.TaskCode == TaskCode.ejercicio, ct);
        var podcasts = await tasks.CountAsync(t => t.TaskCode == TaskCode.podcast, ct);
        var tests = await db.HealthTestEvaluations.AsNoTracking().CountAsync(e => e.PatientId == patientId
            && e.Status == HealthTestEvaluationStatus.completed && e.CompletedAt >= fromUtc && e.CompletedAt <= capturedAtUtc, ct);
        var measurements = await db.ClinicalMeasurements.AsNoTracking().CountAsync(m => m.PatientId == patientId
            && m.ObservedAt >= fromUtc && m.ObservedAt <= capturedAtUtc && m.RecordedAt <= capturedAtUtc, ct);
        return new PatientSummaryData(program?.Status.ToString(), program?.CurrentWeekNumber, program?.StartLocalDate,
            foodCount, foodDays, exercise, podcasts, tests, measurements);
    }

    public Task<PatientAiBrief?> LatestAsync(Guid patientId, string language, CancellationToken ct)
        => db.PatientAiBriefs.FromSqlInterpolated($"""
            SELECT * FROM app.patient_ai_briefs
            WHERE patient_id = {patientId} AND kind = 'patient_summary' AND state = 'draft'
              AND draft_json ->> 'language' = {language}
            ORDER BY generated_at DESC, id DESC LIMIT 1
            """).AsNoTracking().SingleOrDefaultAsync(ct);

    public async Task SaveAsync(PatientAiBrief brief, CancellationToken ct)
    {
        db.PatientAiBriefs.Add(brief);
        await db.SaveChangesAsync(ct);
    }
}
