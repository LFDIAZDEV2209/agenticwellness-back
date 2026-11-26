namespace CoppAddresd.Domain.Wellness365;

/// <summary>Decisión efectiva del motor para una fecha civil del programa.</summary>
public sealed class Motor365PauseDay
{
    public Guid PersonId { get; init; }
    public int Day { get; init; }
    public DateOnly Date { get; init; }
    public bool Scheduled { get; set; }
    public bool SafetyExercise { get; set; }
    public int PlanVersion { get; set; }
}
