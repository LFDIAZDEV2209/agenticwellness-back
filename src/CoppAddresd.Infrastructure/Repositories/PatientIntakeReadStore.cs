using CoppAddresd.Application.Features.Nutrition.Engine;
using CoppAddresd.Application.Features.Nutrition.Cases;
using CoppAddresd.Application.Features.Nutrition.Queries;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

/// <summary>Proyecciones por conjuntos acotadas al paciente y a los días de una página.</summary>
public sealed class PatientIntakeReadStore(AppDbContext db) : IPatientIntakeReadStore
{
    public Task<string?> TimezoneAsync(Guid patientId, CancellationToken ct) => db.PatientProfiles.AsNoTracking()
        .Where(p => p.Id == patientId && p.DeletedAt == null).Select(p => p.Timezone).SingleOrDefaultAsync(ct);

    public async Task<PatientIntakeReadData> LoadAsync(Guid patientId, DateOnly from, DateOnly to, string timezone, bool photos, CancellationToken ct)
    {
        var cycles = await db.NutritionCycles.AsNoTracking().Where(c => c.PatientId == patientId && c.Number != null && c.StartDate <= to).ToArrayAsync(ct);
        var headers = await db.NutritionPrescriptions.AsNoTracking().Where(p => p.PatientId == patientId && p.State == "aprobado"
            && p.ApprovedAt != null && p.StartDate != null && p.EndDate != null && p.StartDate <= to)
            .Select(p => new { p.Id, p.StartDate, p.EndDate, p.ApprovedAt, p.Version }).ToArrayAsync(ct);
        var candidates = headers.Select(p => new EffectivePrescription(p.Id, patientId, p.StartDate!.Value, p.EndDate!.Value, p.ApprovedAt, [], p.Version)).ToArray();
        // Menús solo de versiones que pueden verse en esta página, no todos los años del paciente.
        var ids = Enumerable.Range(0, to.DayNumber - from.DayNumber + 1)
            .Select(n => NutritionPublicationTimeline.Select(candidates, patientId, from.AddDays(n), timezone)?.Id)
            .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();
        var meals = await db.NutritionPrescriptionMeals.AsNoTracking().Where(m => ids.Contains(m.PrescriptionId))
            .Select(m => new { m.Id, m.PrescriptionId, m.DayNumber, m.MealType, m.MenuName }).ToArrayAsync(ct);
        var mealIds = meals.Select(m => m.Id).ToArray();
        var items = await db.NutritionPrescriptionItems.AsNoTracking().Where(i => mealIds.Contains(i.MealId))
            .Select(i => new { i.MealId, i.Grams, i.FoodItem!.Id, i.FoodItem.Calories100, i.FoodItem.Protein100,
                i.FoodItem.Carbs100, i.FoodItem.Fat100, i.FoodItem.Fiber100 }).ToArrayAsync(ct);
        var byMeal = items.ToLookup(i => i.MealId);
        // Macros por el motor canónico (incluye Atwater); no se mezclan con estimados de foto.
        var foods = items.DistinctBy(i => i.Id).ToDictionary(i => i.Id, i => new CoppAddresd.Domain.Entities.NutritionFoodItem
        { Id = i.Id, Calories100 = i.Calories100, Protein100 = i.Protein100, Carbs100 = i.Carbs100, Fat100 = i.Fat100, Fiber100 = i.Fiber100 });
        var engine = new NutritionEngine(foods);
        var menus = meals.Select(m =>
        {
            var macros = engine.SumItems(byMeal[m.Id].Select(i => new NutritionItem(i.Id, i.Grams)));
            return new IntakeReadMenu(m.PrescriptionId, m.DayNumber, new(m.MealType, m.MenuName,
                new((decimal)macros.K, (decimal)macros.P, (decimal)macros.C, (decimal)macros.F, (decimal)macros.Fi)));
        }).ToArray();
        var logs = await db.NutritionIntakeLogs.AsNoTracking().Where(l => l.PatientId == patientId && l.LocalDate >= from && l.LocalDate <= to)
            .OrderByDescending(l => l.LocalDate).ThenBy(l => l.MealCode).ThenBy(l => l.Id)
            .Select(l => new IntakeReadRecord(l.Id, l.LocalDate, l.MealCode.ToString(), new(l.Calories, l.ProteinG, l.CarbsG, l.FatG, l.FiberG),
                l.WaterMl, l.Source, l.FoodAnalysisId)).ToArrayAsync(ct);
        var analysisIds = logs.Where(l => l.AnalysisId.HasValue).Select(l => l.AnalysisId!.Value).Distinct().ToArray();
        // Un enlace legacy inconsistente no autoriza leer el análisis de otro paciente.
        var analyses = await (from a in db.FoodAnalyses.AsNoTracking() join p in db.PatientProfiles on a.UserId equals p.UserId
            where p.Id == patientId && a.UserId != null && analysisIds.Contains(a.AnalysisId)
            select new { a.Id, a.AnalysisId, a.Status, Key = photos ? a.ImageKey : null,
                a.SummaryCalories, a.SummaryProtein, a.SummaryCarbohydrates, a.SummaryFat, a.SummaryFiber }).ToArrayAsync(ct);
        var rowIds = analyses.Select(a => a.Id).ToArray();
        var parts = await db.FoodAnalysisItems.AsNoTracking().Where(i => rowIds.Contains(i.AnalysisId))
            .OrderBy(i => i.ItemIndex).ThenBy(i => i.Id)
            .Select(i => new { i.AnalysisId, Item = new IntakeAnalysisItemDto(i.Name, i.DetectionConfidence, i.PortionSize, i.EstimatedGrams,
                new(i.Calories, i.Protein, i.Carbohydrates, i.Fat, i.Fiber)) }).ToArrayAsync(ct);
        var partsByAnalysis = parts.ToLookup(i => i.AnalysisId, i => i.Item);
        var analysisData = analyses.Select(a => new IntakeReadAnalysis(new(a.AnalysisId, a.Status,
            new(a.SummaryCalories, a.SummaryProtein, a.SummaryCarbohydrates, a.SummaryFat, a.SummaryFiber), partsByAnalysis[a.Id].ToArray()), a.Key)).ToArray();
        var menuDays = menus.ToLookup(m => m.PrescriptionId, m => m.DayNumber);
        return new(cycles, headers.Select(p => new EffectivePrescription(p.Id, patientId, p.StartDate!.Value, p.EndDate!.Value, p.ApprovedAt,
            menuDays[p.Id].Distinct().ToArray(), p.Version)).ToArray(), menus, logs, analysisData);
    }
}
