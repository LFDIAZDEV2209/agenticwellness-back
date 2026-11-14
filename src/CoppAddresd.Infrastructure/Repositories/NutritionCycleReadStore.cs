using System.Data;
using CoppAddresd.Application.Features.Nutrition.Queries;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

/// <summary>Lectura por rango, por conjuntos y bajo snapshot consistente; no materializa menús ni fotos.</summary>
public sealed class NutritionCycleReadStore(AppDbContext db) : INutritionCycleReadStore
{
    public Task<T> ReadAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
        => db.Database.CreateExecutionStrategy().ExecuteAsync(async readCt =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, readCt);
            await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", readCt);
            var result = await operation(readCt); await tx.CommitAsync(readCt); return result;
        }, ct);

    public Task<NutritionCycleReadHeader?> HeaderAsync(Guid cycleId, Guid patientId, CancellationToken ct)
        => (from c in db.NutritionCycles.AsNoTracking()
            join p in db.PatientProfiles.AsNoTracking() on c.PatientId equals p.Id
            where c.Id == cycleId && c.PatientId == patientId
            select new NutritionCycleReadHeader(c, p.Timezone,
                db.NutritionCycles.Where(s => s.PatientId == patientId && s.Number > c.Number).Min(s => s.StartDate)))
            .SingleOrDefaultAsync(ct);

    public async Task<NutritionCycleReadData> RangeAsync(Guid cycleId, Guid patientId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var headers = await db.NutritionPrescriptions.AsNoTracking().Where(p => p.CycleId == cycleId && p.PatientId == patientId
                && p.State == "aprobado" && p.ApprovedAt != null && p.StartDate != null && p.EndDate != null && p.StartDate <= to)
            .Select(p => new { p.Id, p.StartDate, p.EndDate, p.ApprovedAt, p.Version }).ToArrayAsync(ct);
        var ids = headers.Select(p => p.Id).ToArray();
        var menus = await db.NutritionPrescriptionMeals.AsNoTracking().Where(m => ids.Contains(m.PrescriptionId))
            .GroupBy(m => new { m.PrescriptionId, m.DayNumber }).Select(g => new CycleMealCount(g.Key.PrescriptionId, g.Key.DayNumber, g.Count())).ToArrayAsync(ct);
        // Agua no es una comida: no infla el numerador ni la cobertura de comidas.
        var logs = await db.NutritionIntakeLogs.AsNoTracking().Where(l => l.PatientId == patientId && l.LocalDate >= from && l.LocalDate <= to && l.MealCode != MealCode.agua)
            .OrderBy(l => l.LocalDate).ThenBy(l => l.MealCode).ThenBy(l => l.Id)
            .Select(l => new CycleIntake(l.Id, l.LocalDate, l.MealCode.ToString())).ToArrayAsync(ct);
        return new(headers.Select(p => new EffectivePrescription(p.Id, patientId, p.StartDate!.Value, p.EndDate!.Value, p.ApprovedAt,
            menus.Where(m => m.PrescriptionId == p.Id).Select(m => m.DayNumber).ToArray(), p.Version)).ToArray(), menus, logs);
    }
}
