using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoppAddresd.Application.Features.Nutrition.Queries;
using CoppAddresd.Domain.Entities;

namespace CoppAddresd.Application.Features.Nutrition.Commands;

/// <summary>Contrato compacto 3.12: todos los números son datos; adherence es cobertura, no adherencia clínica D2.</summary>
public sealed record CycleAiAggregates(int DaysWithRecords, decimal? Adherence, decimal? AvgCalories,
    decimal? AvgProteinG, decimal? AvgCarbsG, decimal? AvgFatG, decimal? AvgFiberG, decimal? AvgWaterMl);
public sealed record CycleAiMealCounts(int Des, int Alm, int Mer, int Cen, int Agua);
public sealed record CycleAiMacros(decimal? ProteinG, decimal? CarbsG, decimal? FatG, decimal? FiberG);
public sealed record CycleAiRecord(DateOnly Date, string MealCode, decimal? Calories, CycleAiMacros Macros,
    string Origin, decimal? WaterMl = null);
public sealed record CycleAiMeasurements(decimal? Weight = null, decimal? Waist = null, decimal? Bmi = null, decimal? Glucose = null);
public sealed record NutritionCycleSummaryInput(int CycleNumber, DateOnly StartDate, DateOnly EndDate, string Language,
    CycleAiAggregates Aggregates, CycleAiMealCounts MealCounts, CycleAiAggregates? PreviousCycleAggregates,
    CycleAiMeasurements? Measurements, IReadOnlyList<CycleAiRecord> Records)
{
    /// <summary>Metadato exclusivo del guard de consentimiento; jamás se serializa ni entra en el hash.</summary>
    [JsonIgnore] public Guid? SubjectPatientId { get; init; }
}
public sealed record NutritionCycleSummaryResponse(string Status, string Summary, IReadOnlyList<string> Patterns,
    IReadOnlyList<string> Gaps, CyclePatientMessage Draft, string Model, DateTimeOffset GeneratedAt);
public sealed record NutritionCycleAiDraft(string Status, string Summary, IReadOnlyList<string> Patterns,
    IReadOnlyList<string> Gaps, CyclePatientMessage Draft, string Model, DateTimeOffset GeneratedAt,
    DateTimeOffset CapturedAt, string InputHash, NutritionCycleSummaryInput Inputs);
public sealed record GenerateCycleAiDraftResult(string Status, int Revision, NutritionCycleAiDraft? AiDraft,
    string? WarningCode = null, string? Warning = null);
public interface INutritionCycleSummaryClient
{
    Task<NutritionCycleSummaryResponse> GenerateAsync(NutritionCycleSummaryInput input, CancellationToken ct);
}
/// <summary>Error controlado y sin cuerpo del proveedor; el editor puede continuar manualmente.</summary>
public sealed class NutritionCycleAiUnavailableException(string code) : Exception("Resumen de IA no disponible.")
{
    public string Code { get; } = code;
}
public sealed record CycleAiSample(DateOnly Date, string MealCode, IntakeMacrosDto Recorded, decimal? WaterMl,
    string Source, IntakeMacrosDto? OwnedEstimate);
public sealed record CycleAiPeriodData(int DaysWithRecords, CycleAiMealCounts MealCounts, IReadOnlyList<CycleAiSample> Samples);
public interface INutritionCycleAiReadStore
{
    Task<NutritionCycle?> PreviousAsync(Guid patientId, int number, CancellationToken ct);
    Task<CycleAiPeriodData> PeriodAsync(Guid patientId, DateOnly from, DateOnly to, bool samples, CancellationToken ct);
    Task<CycleAiMeasurements> MeasurementsAsync(Guid patientId, DateTime throughExclusiveUtc, CancellationToken ct);
}
public static class NutritionCycleAiJson
{
    // Null forma parte del contrato: desconocido no equivale a cero ni a campo omitido.
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Hash(NutritionCycleSummaryInput input)
        => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input, Options)));
}
