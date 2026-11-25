namespace CoppAddresd.Domain.Wellness365;

/// <summary>Caché reconstruible de un plan calculado.</summary>
public sealed class Motor365PlanSnapshot
{
    public Guid PersonId { get; init; }
    public int PlanVersion { get; init; }
    public int ParamsVersion { get; init; }
    public Guid SubmissionId { get; init; }
    public string EngineVer { get; init; } = WellnessEngine.ReferenceVersion;
    public required string PlanJson { get; init; }
    public DateTimeOffset GeneratedAt { get; init; }
}
