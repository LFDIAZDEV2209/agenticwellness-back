namespace CoppAddresd.Domain.Wellness365;

/// <summary>Decisión del motor con día y versiones de cálculo.</summary>
public sealed class Motor365EngineEvent
{
    public Guid Id { get; init; }
    public Guid PersonId { get; init; }
    public int Day { get; init; }
    public int PlanVersion { get; init; }
    public int ParamsVersion { get; init; }
    public string EngineVer { get; init; } = WellnessEngine.ReferenceVersion;
    public required string Kind { get; init; }
    public decimal? CalorieDelta { get; init; }
    public decimal? RatePercent { get; init; }
    public required string MessageEs { get; init; }
    public required string MessageEn { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
}
