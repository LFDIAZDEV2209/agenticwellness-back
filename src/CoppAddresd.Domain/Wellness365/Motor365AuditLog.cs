namespace CoppAddresd.Domain.Wellness365;

/// <summary>Registro de auditoría de solo inserción.</summary>
public sealed class Motor365AuditLog
{
    public Guid Id { get; init; }
    public Guid PersonId { get; init; }
    public Guid? ActorUserId { get; init; }
    public required string Action { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
}
