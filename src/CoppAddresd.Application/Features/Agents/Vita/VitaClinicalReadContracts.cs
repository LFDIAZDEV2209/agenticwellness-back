namespace CoppAddresd.Application.Features.Agents.Vita;

public sealed record VitaMeasurementDto(string MetricCode, decimal Value, string UnitCode, string UnitSymbol,
    DateTime ObservedAt, string? Source);

public sealed record VitaAnthropometryDto(VitaMeasurementDto? Weight, VitaMeasurementDto? Height, VitaMeasurementDto? Bmi,
    IReadOnlyList<VitaMeasurementDto> History, bool HasMore);

public sealed record VitaNutritionGoalsDto(string Source, string? TargetCondition, double? DailyCalorieTarget,
    double? DailyProteinTargetG, double? DailyCarbsTargetG, double? DailyFatTargetG, double? DailyFiberTargetG);

public sealed record VitaProfileIdentityDto(string FirstName, int? AgeYears, string? Gender, string TimeZone);

public sealed record VitaProfileDto(DateTime AsOf, VitaProfileIdentityDto Profile, VitaNutritionGoalsDto? Goals,
    IReadOnlyList<string> Allergies, IReadOnlyList<string> Restrictions, VitaAnthropometryDto Anthropometry);

public sealed record VitaVitalsDto(DateTime AsOf, string TimeZone, DateOnly From, DateOnly To,
    IReadOnlyList<VitaMeasurementDto> Latest, IReadOnlyList<VitaMeasurementDto> History, bool HasMore);

public sealed record VitaMedicationDoseDto(DateTime ScheduledAt, string Status, DateTime? OccurredAt);

public sealed record VitaMedicationTreatmentDto(Guid Id, string Name, string ProductType, decimal Dosage, string Unit,
    string Route, string TimeZone, IReadOnlyList<string> Times, DateOnly StartDate, DateOnly? EndDate,
    IReadOnlyList<VitaMedicationDoseDto> Doses);

public sealed record VitaMedicationsDto(DateTime AsOf, string TimeZone, DateOnly LocalDate,
    IReadOnlyList<VitaMedicationTreatmentDto> Treatments);

public sealed record VitaProfileDetails(VitaNutritionGoalsDto? Goals, IReadOnlyList<string> Allergies,
    IReadOnlyList<string> Restrictions, VitaAnthropometryDto Anthropometry);

public sealed record VitaVitalsDetails(IReadOnlyList<VitaMeasurementDto> Latest, IReadOnlyList<VitaMeasurementDto> History,
    bool HasMore);

/// <summary>Proyecciones clínicas limitadas al identificador resuelto por VitaPatientAccess.</summary>
public interface IVitaClinicalReader
{
    Task<VitaProfileDetails> GetProfileAsync(Guid patientId, DateOnly today, DateTime asOf, CancellationToken ct);
    Task<VitaVitalsDetails> GetVitalsAsync(Guid patientId, DateTime fromUtc, DateTime endUtc, DateTime asOf,
        CancellationToken ct);
}
