namespace CoppAddresd.Domain.Wellness365;

/// <summary>Observaciones registradas para un día del programa.</summary>
public sealed class Motor365DailyLog
{
    public Guid PersonId { get; init; }
    public int Day { get; init; }
    public DateOnly LogDate { get; init; }
    public decimal Adherence { get; set; }
    public decimal SleepHours { get; set; }
    public int Energy { get; set; }
    public int Pain { get; set; }
    public string[] Symptoms { get; set; } = [];
    public decimal? WeightKg { get; set; }
    public decimal? WaistCm { get; set; }
    public bool CompletedExercise { get; set; }
}
