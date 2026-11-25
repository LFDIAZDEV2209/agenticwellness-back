namespace CoppAddresd.Domain.Wellness365;

/// <summary>Versión de los parámetros del motor.</summary>
public sealed class Motor365EngineParams
{
    public int Version { get; init; }
    public string EngineVer { get; init; } = WellnessEngine.ReferenceVersion;
    public required string ParametersJson { get; init; }
    public Guid ChangedBy { get; init; }
    public required string Reason { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
