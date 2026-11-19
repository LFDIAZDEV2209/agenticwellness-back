namespace CoppAddresd.Domain.Entities;

/// <summary>Solicitud durable de bienvenida de app asociada al alta ERP.</summary>
public sealed class PatientWelcomeRequest
{
    public Guid PatientId { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? TransferredAt { get; set; }
}
