namespace CoppAddresd.Application.Features.Wellness365;

public sealed record WellnessLogInput(decimal Adherence, decimal SleepHours, int Energy, int Pain,
    decimal? WeightKg, decimal? WaistCm, bool CompletedExercise, IReadOnlyList<string> Symptoms)
{
    public bool IsValid() =>
        Adherence is >= 0 and <= 1 && SleepHours is >= 0 and <= 24 &&
        Energy is >= 1 and <= 5 && Pain is >= 0 and <= 10 &&
        WeightKg is null or (>= .01m and <= 1000) && WaistCm is null or (>= .01m and <= 500) &&
        Symptoms is not null && Symptoms.Count <= 5 &&
        Symptoms.All(s => s is "mareo" or "hambre" or "fatiga" or "insomnio" or "nauseas");
}
