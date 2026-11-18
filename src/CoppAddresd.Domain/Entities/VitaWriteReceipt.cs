namespace CoppAddresd.Domain.Entities;

/// <summary>Recibo idempotente de la entidad canónica creada por un turno de Vita.</summary>
public sealed class VitaWriteReceipt
{
    public Guid Id { get; set; }
    public Guid PatientId { get; set; }
    public string Channel { get; set; } = default!;
    public string TurnId { get; set; } = default!;
    public string ToolName { get; set; } = default!;
    public string RequestHash { get; set; } = default!;
    public string EntityType { get; set; } = default!;
    public Guid EntityId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
