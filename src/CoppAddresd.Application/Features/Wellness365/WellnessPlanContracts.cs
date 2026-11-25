using System.Text.Json.Serialization;
using CoppAddresd.Domain.Wellness365;

namespace CoppAddresd.Application.Features.Wellness365;

/// <summary>
/// Entrada de servidor, ya autorizada. La compuerta legal/de cuestionario se resuelve
/// contra los datos persistidos antes de llamar al servicio, nunca con el cuerpo HTTP.
/// </summary>
public sealed record WellnessPlanPerson(WellnessProfile Profile, WellnessPlanCalendar? Calendar,
    WellnessBlock? EligibilityBlock);

public sealed record WellnessPlanStatus(string State, string? Code = null, WellnessText? Message = null);
public sealed record WellnessPlanDayView(int Day, int Today, DateOnly StartDate, string TimeZoneId, bool Real,
    WellnessPlanStatus Status, string LogStatus, double? Adherence, double? MeasuredWeightKg, int Streak,
    WellnessExerciseDay? Exercise, WellnessMenu Menu, IReadOnlyList<WellnessText> Reasons,
    [property: JsonPropertyName("engine_ver")] string EngineVersion,
    [property: JsonPropertyName("params_version")] int ParametersVersion);

public sealed record WellnessPlanClosure(int Day, DateOnly LogDate);
public enum WellnessPlanErrorCode { PersonNotFound, BadDay, PlanLocked, FutureDay, BadLog }
public sealed record WellnessPlanError(WellnessPlanErrorCode Code, WellnessPlanStatus? Status = null);

public sealed record WellnessPlanResult<T> where T : class
{
    public T? Value { get; }
    public WellnessPlanError? Error { get; }

    internal WellnessPlanResult(T value) => Value = value;
    internal WellnessPlanResult(WellnessPlanError error) => Error = error;
}
