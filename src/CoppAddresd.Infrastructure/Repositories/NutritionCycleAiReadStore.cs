using CoppAddresd.Application.Features.Nutrition.Commands;
using CoppAddresd.Application.Features.Nutrition.Queries;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

/// <summary>Consultas por conjuntos: agregados de todo el período, muestra de 100 y mediciones compatibles con 3.12.</summary>
public sealed class NutritionCycleAiReadStore(AppDbContext db) : INutritionCycleAiReadStore
{
    public Task<NutritionCycle?> PreviousAsync(Guid patientId, int number, CancellationToken ct)
        => db.NutritionCycles.AsNoTracking().Where(c => c.PatientId == patientId && c.Number != null && c.Number < number)
            .OrderByDescending(c => c.Number).FirstOrDefaultAsync(ct);

    public async Task<CycleAiPeriodData> PeriodAsync(Guid patientId, DateOnly from, DateOnly to, bool samples, CancellationToken ct)
    {
        var query = db.NutritionIntakeLogs.AsNoTracking().Where(l => l.PatientId == patientId && l.LocalDate >= from && l.LocalDate <= to);
        var days = await query.Select(l => l.LocalDate).Distinct().CountAsync(ct);
        var counts = await query.GroupBy(l => l.MealCode).Select(g => new { Meal = g.Key.ToString(), Count = g.Count() }).ToArrayAsync(ct);
        var byMeal = counts.ToDictionary(x => x.Meal, x => x.Count);
        var meals = new CycleAiMealCounts(byMeal.GetValueOrDefault("des"), byMeal.GetValueOrDefault("alm"), byMeal.GetValueOrDefault("mer"),
            byMeal.GetValueOrDefault("cen"), byMeal.GetValueOrDefault("agua"));
        if (!samples) return new(days, meals, []);
        var rows = await query.OrderByDescending(l => l.LocalDate).ThenBy(l => l.MealCode).ThenBy(l => l.Id).Take(100)
            .Select(l => new { l.LocalDate, Meal = l.MealCode.ToString(), l.Source, l.WaterMl, l.FoodAnalysisId,
                Macros = new IntakeMacrosDto(l.Calories, l.ProteinG, l.CarbsG, l.FatG, l.FiberG) }).ToArrayAsync(ct);
        var ids = rows.Where(r => r.FoodAnalysisId != null).Select(r => r.FoodAnalysisId!.Value).Distinct().ToArray();
        // Solo valores numéricos del análisis propio, sin nombres, fotos, URLs ni claves de objetos.
        var analyses = ids.Length == 0 ? [] : await (from a in db.FoodAnalyses.AsNoTracking()
            join p in db.PatientProfiles on a.UserId equals p.UserId
            where p.Id == patientId && a.UserId != null && ids.Contains(a.AnalysisId)
            select new { a.AnalysisId, Macros = new IntakeMacrosDto(a.SummaryCalories, a.SummaryProtein, a.SummaryCarbohydrates, a.SummaryFat, a.SummaryFiber) }).ToArrayAsync(ct);
        var estimates = analyses.ToDictionary(a => a.AnalysisId, a => a.Macros);
        return new(days, meals, rows.Select(r => new CycleAiSample(r.LocalDate, r.Meal, r.Macros, r.WaterMl, r.Source,
            r.FoodAnalysisId is { } id ? estimates.GetValueOrDefault(id) : null)).ToArray());
    }

    public async Task<CycleAiMeasurements> MeasurementsAsync(Guid patientId, DateTime throughExclusiveUtc, CancellationToken ct)
    {
        string[] weight = ["weight", "weight_kg", "peso"], waist = ["waist", "waist_cm", "waist_circumference", "cintura", "abdominal_circumference"];
        string[] bmi = ["bmi", "imc"], glucose = ["glucose", "glucose_fasting", "fasting_glucose", "glicemia"];
        var points = db.ClinicalMeasurements.AsNoTracking().Where(m => m.PatientId == patientId && m.ObservedAt < throughExclusiveUtc)
            .Select(m => new { m.Id, m.Value, m.ObservedAt, m.RecordedAt, Code = m.Metric!.Code.ToLower(), Unit = m.Unit!.Symbol.ToLower().Replace("²", "2"), UnitCode = m.Unit.Code.ToLower() });
        var compatible = points.Where(m =>
                weight.Contains(m.Code) && (m.Unit == "kg" || m.UnitCode == "kg") && m.Value > 0 && m.Value <= 1000
                || waist.Contains(m.Code) && (m.Unit == "cm" || m.UnitCode == "cm") && m.Value > 0 && m.Value <= 500
                || bmi.Contains(m.Code) && (m.Unit == "kg/m2" || m.UnitCode == "kg_m2") && m.Value > 0 && m.Value <= 200
                || glucose.Contains(m.Code) && (m.Unit == "mg/dl" || m.UnitCode == "mg_dl") && m.Value > 0 && m.Value <= 2000)
            .Select(m => new { Kind = weight.Contains(m.Code) ? "weight" : waist.Contains(m.Code) ? "waist" : bmi.Contains(m.Code) ? "bmi" : "glucose", m.Value, m.ObservedAt, m.RecordedAt, m.Id });
        var latest = await compatible.GroupBy(m => m.Kind).Select(g => g.OrderByDescending(m => m.ObservedAt)
            .ThenByDescending(m => m.RecordedAt).ThenBy(m => m.Id).First()).ToArrayAsync(ct);
        var values = latest.ToDictionary(m => m.Kind, m => (decimal?)m.Value);
        // No derivar IMC, glucosa ni otros valores ausentes; no asumir unidades de registros incompatibles.
        return new(values.GetValueOrDefault("weight"), values.GetValueOrDefault("waist"), values.GetValueOrDefault("bmi"), values.GetValueOrDefault("glucose"));
    }
}
