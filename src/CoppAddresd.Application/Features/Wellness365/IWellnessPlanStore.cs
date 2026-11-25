using CoppAddresd.Domain.Wellness365;

namespace CoppAddresd.Application.Features.Wellness365;

/// <summary>Requiere un paciente y actor resueltos y autorizados por el servidor.</summary>
public interface IWellnessPlanStore
{
    Task<WellnessPlanPerson?> ReadAsync(Guid patientId, WellnessBlock? eligibilityBlock, CancellationToken ct);
    Task<WellnessPlanResult<WellnessPlanClosure>> CloseAsync(Guid patientId, Guid actorUserId, string? day,
        WellnessLogInput input, CancellationToken ct);
    Task<WellnessPlanCalendar?> ChangeTimeZoneAsync(Guid patientId, Guid actorUserId, string timeZoneId,
        CancellationToken ct);
}

public sealed record WellnessLogInput(decimal Adherence, decimal SleepHours, int Energy, int Pain,
    decimal? WeightKg, decimal? WaistCm, bool CompletedExercise, IReadOnlyList<string> Symptoms)
{
    internal bool IsValid() =>
        Adherence is >= 0 and <= 1 && SleepHours is >= 0 and <= 24 &&
        Energy is >= 1 and <= 5 && Pain is >= 0 and <= 10 &&
        WeightKg is null or (>= .01m and <= 1000) && WaistCm is null or (>= .01m and <= 500) &&
        Symptoms is not null && Symptoms.Count <= 4 &&
        Symptoms.All(s => s is "mareo" or "hambre" or "fatiga" or "insomnio");
}
