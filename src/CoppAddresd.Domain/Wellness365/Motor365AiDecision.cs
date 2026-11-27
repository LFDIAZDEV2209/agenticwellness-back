namespace CoppAddresd.Domain.Wellness365;

/// <summary>Decisión inmutable, separada del perfil, de la caché y del cálculo del motor.</summary>
public sealed class Motor365AiDecision
{
    public Guid Id { get; init; }
    public long Sequence { get; init; }
    public Guid PersonId { get; init; }
    public Guid ActorUserId { get; init; }
    public int Day { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public required string EngineVersion { get; init; }
    public int ParametersVersion { get; init; }
    public required string BaseFingerprint { get; init; }
    public required string Status { get; init; }
    public string? Code { get; init; }
    public required string ReasonEs { get; init; }
    public required string ReasonEn { get; init; }
    public string? ProposedJson { get; init; }
    public Guid? Reverts { get; init; }
}
