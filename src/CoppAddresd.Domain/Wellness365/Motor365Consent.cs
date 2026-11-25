namespace CoppAddresd.Domain.Wellness365;

/// <summary>Referencia al recibo legal exacto del paciente.</summary>
public sealed class Motor365Consent
{
    public Guid PersonId { get; init; }
    public Guid AcceptanceId { get; init; }
    public DateTimeOffset RecordedAt { get; init; }
}
