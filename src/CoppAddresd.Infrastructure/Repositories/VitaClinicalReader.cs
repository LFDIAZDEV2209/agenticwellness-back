using CoppAddresd.Application.Features.Agents.Vita;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

/// <summary>Proyecta solo los campos que consume Vita, sin cargar expedientes o planes completos.</summary>
public sealed class VitaClinicalReader(AppDbContext db) : IVitaClinicalReader
{
    private static readonly string[] AnthropometryMetrics = ["weight", "height", "bmi"];
    private static readonly string[] VitalMetrics =
    [
        "heart_rate", "o2_saturation", "systolic_bp", "diastolic_bp", "temperature_c",
        "sleep_minutes", "step_count", "activity_kcal", "distance_m", "glucose_fasting", "body_fat",
    ];
    private static readonly string[] ClinicalSources = ["device", "patient", "professional", "lab"];

    public async Task<VitaProfileDetails> GetProfileAsync(Guid patientId, DateOnly today, DateTime asOf,
        CancellationToken ct)
    {
        var allergies = await db.PatientAllergies.AsNoTracking()
            .Where(a => a.PatientId == patientId)
            .OrderBy(a => a.Allergen!.Name).ThenBy(a => a.Id)
            .Select(a => a.Allergen!.Name).ToArrayAsync(ct);
        var (goals, restrictions) = await GetNutritionAsync(patientId, today, ct);
        var legacy = db.VitalSigns.AsNoTracking().Where(v => v.PatientId == patientId && v.MeasuredAt <= asOf);
        // Las tomas heredadas no registran un actor ni un dispositivo por lectura.
        var historicWeights = legacy.Where(v => v.WeightKg != null).Select(v => new MeasurementRow
        {
            Id = v.Id, Code = "weight", Value = v.WeightKg!.Value, UnitCode = "kg", UnitSymbol = "kg",
            ObservedAt = v.MeasuredAt, RecordedAt = v.CreatedAt, Source = null,
        });
        var historicHeights = legacy.Where(v => v.HeightCm != null).Select(v => new MeasurementRow
        {
            Id = v.Id, Code = "height", Value = v.HeightCm!.Value, UnitCode = "cm", UnitSymbol = "cm",
            ObservedAt = v.MeasuredAt, RecordedAt = v.CreatedAt, Source = null,
        });
        var measurements = Measurements(patientId, AnthropometryMetrics, asOf)
            .Concat(historicWeights).Concat(historicHeights);
        var readings = await ReadSeriesAsync(measurements, 100, ct);
        var latest = readings.Latest.ToDictionary(m => m.MetricCode);
        return new(goals, allergies, restrictions, new(latest.GetValueOrDefault("weight"),
            latest.GetValueOrDefault("height"), latest.GetValueOrDefault("bmi"), readings.History, readings.HasMore));
    }

    public Task<VitaVitalsDetails> GetVitalsAsync(Guid patientId, DateTime fromUtc, DateTime endUtc, DateTime asOf,
        CancellationToken ct) => ReadSeriesAsync(Measurements(patientId, VitalMetrics, asOf)
            .Where(m => m.ObservedAt >= fromUtc && m.ObservedAt < endUtc), 200, ct);

    private IQueryable<MeasurementRow> Measurements(Guid patientId, string[] metrics, DateTime asOf) =>
        db.EffectiveClinicalMeasurements(patientId)
            .Where(m => m.ObservedAt <= asOf && metrics.Contains(m.Metric!.Code) && ClinicalSources.Contains(m.Source))
            .Select(m => new MeasurementRow
            {
                Id = m.Id, Code = m.Metric!.Code, Value = m.Value, UnitCode = m.Unit!.Code, UnitSymbol = m.Unit.Symbol,
                ObservedAt = m.ObservedAt, RecordedAt = m.RecordedAt, Source = m.Source,
            });

    private static async Task<VitaVitalsDetails> ReadSeriesAsync(IQueryable<MeasurementRow> measurements, int limit,
        CancellationToken ct)
    {
        // La última de cada métrica se calcula antes de truncar el historial total.
        var latest = await measurements.GroupBy(m => m.Code)
            .Select(g => g.OrderByDescending(m => m.ObservedAt).ThenByDescending(m => m.RecordedAt)
                .ThenByDescending(m => m.Id).First())
            .ToArrayAsync(ct);
        var history = await measurements.OrderByDescending(m => m.ObservedAt).ThenByDescending(m => m.RecordedAt)
            .ThenByDescending(m => m.Id).ThenBy(m => m.Code).Take(limit + 1).ToArrayAsync(ct);
        return new(latest.OrderByDescending(m => m.ObservedAt).ThenByDescending(m => m.RecordedAt)
                .ThenByDescending(m => m.Id).Select(m => m.ToDto()).ToArray(),
            history.Take(limit).Select(m => m.ToDto()).ToArray(), history.Length > limit);
    }

    private async Task<(VitaNutritionGoalsDto? Goals, string[] Restrictions)> GetNutritionAsync(
        Guid patientId, DateOnly today, CancellationToken ct)
    {
        var goals = await NutritionPrescriptionRepository.CurrentPublished(db, patientId, today)
            .Select(p => new VitaNutritionGoalsDto("nutrition_prescription", null, p.CalorieTarget,
                p.ProteinTarget, p.CarbsTarget, p.FatTarget, p.FiberTarget)).FirstOrDefaultAsync(ct);
        if (goals is not null) return (WithRegisteredGoals(goals), []);

        var day = today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var earliest = day.AddDays(-29);
        var nextDay = day.AddDays(1);
        var legacy = await db.NutritionPlanAssignments.AsNoTracking()
            .Where(a => a.PatientId == patientId && a.Status == AssignmentStatus.Active
                && a.StartDate >= earliest && a.StartDate < nextDay && (a.EndDate == null || a.EndDate >= day)
                && a.Plan.PatientId == patientId && !a.Plan.IsTemplate
                && a.Plan.Status == NutritionPlanStatus.Active && a.Plan.ReviewStatus == "approved"
                && a.Plan.ApprovedAt != null && a.Plan.ApprovedBy != null)
            .OrderByDescending(a => a.StartDate).ThenByDescending(a => a.Id)
            .Select(a => new
            {
                Goals = new VitaNutritionGoalsDto("nutrition_plan", a.Plan.TargetCondition,
                    a.Plan.DailyCalorieTarget, (double?)a.Plan.DailyProteinTarget, (double?)a.Plan.DailyCarbsTarget,
                    (double?)a.Plan.DailyFatTarget, (double?)a.Plan.DailyFiberTarget),
                Restrictions = a.Plan.Allergens,
            }).FirstOrDefaultAsync(ct);
        return legacy is null ? (null, [])
            : (WithRegisteredGoals(legacy.Goals), string.IsNullOrWhiteSpace(legacy.Restrictions) ? [] : [legacy.Restrictions]);
    }

    private static VitaNutritionGoalsDto? WithRegisteredGoals(VitaNutritionGoalsDto goals) =>
        string.IsNullOrWhiteSpace(goals.TargetCondition) && goals is
        {
            DailyCalorieTarget: null, DailyProteinTargetG: null, DailyCarbsTargetG: null,
            DailyFatTargetG: null, DailyFiberTargetG: null,
        } ? null : goals;

    private sealed class MeasurementRow
    {
        public Guid Id { get; init; }
        public required string Code { get; init; }
        public decimal Value { get; init; }
        public required string UnitCode { get; init; }
        public required string UnitSymbol { get; init; }
        public DateTime ObservedAt { get; init; }
        public DateTime RecordedAt { get; init; }
        public string? Source { get; init; }

        public VitaMeasurementDto ToDto() => new(Code, Value, UnitCode, UnitSymbol, ObservedAt, Source, Source is null ? null : Id);
    }
}
