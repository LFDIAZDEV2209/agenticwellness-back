using CoppAddresd.Application.Features.Nutrition.Commands;
using CoppAddresd.Application.Features.Nutrition.Queries;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

public sealed class NutritionCycleHighlightStore(AppDbContext db) : INutritionCycleHighlightStore
{
    public Task<T> WithPatientLockAsync<T>(Guid patientId, Func<CancellationToken, Task<T>> operation, CancellationToken ct)
        => db.Database.CreateExecutionStrategy().ExecuteAsync(async writeCt =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(writeCt);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM app.patient_profiles WHERE id = {patientId} FOR UPDATE", writeCt);
            var result = await operation(writeCt); await tx.CommitAsync(writeCt); return result;
        }, ct);

    public Task<HighlightIntakeHeader?> IntakeAsync(Guid intakeId, CancellationToken ct)
        => db.NutritionIntakeLogs.AsNoTracking().Where(l => l.Id == intakeId)
            .Select(l => new HighlightIntakeHeader(l.PatientId, l.LocalDate)).SingleOrDefaultAsync(ct);

    public async Task PinAsync(Guid cycleId, Guid intakeId, Guid actorId, string? note, CancellationToken ct)
        => await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO app.nutrition_cycle_highlights (cycle_id, intake_log_id, pinned_by, pinned_at, note)
            VALUES ({cycleId}, {intakeId}, {actorId}, clock_timestamp(), {note})
            ON CONFLICT (cycle_id, intake_log_id) DO UPDATE
            SET pinned_by = EXCLUDED.pinned_by, pinned_at = EXCLUDED.pinned_at, note = EXCLUDED.note
            """, ct);

    public async Task UnpinAsync(Guid cycleId, Guid intakeId, CancellationToken ct)
        => await db.Set<NutritionCycleHighlight>().Where(h => h.CycleId == cycleId && h.IntakeLogId == intakeId).ExecuteDeleteAsync(ct);

    public async Task<IntakeExamplesPage> ListAsync(Guid cycleId, Guid patientId, bool notes, int page, int pageSize, CancellationToken ct)
    {
        var query = from h in db.Set<NutritionCycleHighlight>().AsNoTracking()
            join c in db.NutritionCycles on h.CycleId equals c.Id
            join i in db.NutritionIntakeLogs on h.IntakeLogId equals i.Id
            where c.Id == cycleId && c.PatientId == patientId && i.PatientId == patientId
            select new { Highlight = h, Intake = i };
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.Highlight.PinnedAt).ThenBy(x => x.Intake.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new IntakeExampleDto(x.Intake.Id, x.Intake.LocalDate, x.Intake.MealCode.ToString(),
                new IntakeMacrosDto(x.Intake.Calories, x.Intake.ProteinG, x.Intake.CarbsG, x.Intake.FatG, x.Intake.FiberG),
                x.Intake.WaterMl, x.Highlight.PinnedBy, x.Highlight.PinnedAt, notes ? x.Highlight.Note : null)).ToArrayAsync(ct);
        return new(page, pageSize, total, rows);
    }
}
