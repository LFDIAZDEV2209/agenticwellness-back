namespace CoppAddresd.Auth.Entities;

/// <summary>Comprobante de recepción, no de ejecución. No contiene datos de la exportación.</summary>
public sealed class PrivacyRequest
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Kind { get; set; } = null!;
    public string? Format { get; set; }
    public DateTime ReceivedAt { get; set; }
    public DateTime? ExportPreparedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public PrivacyDeletionCase? DeletionCase { get; set; }
}
