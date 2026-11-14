using CoppAddresd.Application.Features.Nutrition.Commands;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

/// <summary>Marcas persistidas y estado derivado; el contador filtra el alcance antes de agrupar.</summary>
public sealed class NutritionIntakeDayReviewStore(AppDbContext db, IPatientScopeGuard scope) : INutritionIntakeDayReviewStore
{
    public Task<T> WithPatientLockAsync<T>(Guid patientId, Func<CancellationToken, Task<T>> operation, CancellationToken ct)
        => db.Database.CreateExecutionStrategy().ExecuteAsync(async writeCt =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(writeCt);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM app.patient_profiles WHERE id = {patientId} FOR UPDATE", writeCt);
            var result = await operation(writeCt); await tx.CommitAsync(writeCt); return result;
        }, ct);

    public async Task MarkAsync(Guid patientId, Guid cycleId, DateOnly day, Guid actorId, CancellationToken ct)
        => await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO app.nutrition_intake_day_reviews (patient_id, local_date, cycle_id, reviewed_by, reviewed_at)
            VALUES ({patientId}, {day}, {cycleId}, {actorId}, clock_timestamp())
            ON CONFLICT (patient_id, local_date) DO UPDATE
            SET cycle_id = EXCLUDED.cycle_id, reviewed_by = EXCLUDED.reviewed_by, reviewed_at = EXCLUDED.reviewed_at
            """, ct);

    public async Task UnmarkAsync(Guid patientId, Guid cycleId, DateOnly day, CancellationToken ct)
        => await db.NutritionIntakeDayReviews.Where(r => r.PatientId == patientId && r.CycleId == cycleId && r.LocalDate == day).ExecuteDeleteAsync(ct);

    public async Task<IReadOnlyList<IntakeDayReviewDto>> DaysAsync(Guid patientId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var latest = await db.NutritionIntakeLogs.AsNoTracking().Where(l => l.PatientId == patientId && l.LocalDate >= from && l.LocalDate <= to)
            .GroupBy(l => l.LocalDate).Select(g => new { Date = g.Key, Latest = g.Max(l => l.UpdatedAt ?? l.CreatedAt) }).ToDictionaryAsync(l => l.Date, ct);
        var reviews = await db.NutritionIntakeDayReviews.AsNoTracking().Where(r => r.PatientId == patientId && r.LocalDate >= from && r.LocalDate <= to)
            .ToDictionaryAsync(r => r.LocalDate, ct);
        return Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(n =>
        {
            var date = from.AddDays(n); var review = reviews.GetValueOrDefault(date); var data = latest.GetValueOrDefault(date);
            var modified = review is not null && data is not null && data.Latest > review.ReviewedAt;
            return new IntakeDayReviewDto(date, data is not null, review?.ReviewedAt, review?.ReviewedBy,
                data is not null && (review is null || modified), modified);
        }).ToArray();
    }

    public async Task<int> CountUnreviewedAsync(CancellationToken ct)
    {
        var visible = await scope.FilterAsync(db.NutritionIntakeLogs.AsNoTracking(), l => l.PatientId, "Nutrition.RecordsView", ct);
        return await visible.GroupBy(l => new { l.PatientId, l.LocalDate })
            .Select(g => new { g.Key.PatientId, g.Key.LocalDate, Latest = g.Max(l => l.UpdatedAt ?? l.CreatedAt) })
            .CountAsync(d => !db.NutritionIntakeDayReviews.Any(r => r.PatientId == d.PatientId && r.LocalDate == d.LocalDate && r.ReviewedAt >= d.Latest), ct);
    }
}
