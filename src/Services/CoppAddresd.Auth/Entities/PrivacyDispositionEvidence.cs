namespace CoppAddresd.Auth.Entities;

/// <summary>Resultado por fuente, sin datos del titular ni copias del registro suprimido.</summary>
public sealed class PrivacyDispositionEvidence
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public string SourceKey { get; set; } = null!;
    public string Outcome { get; set; } = null!;
    public long RemainingCount { get; set; }
    public int? RowsErased { get; set; }
    public string EvidenceReference { get; set; } = null!;
    public string? ActionReference { get; set; }
    public string? PolicyReference { get; set; }
    public string? ArchiveReference { get; set; }
    public string? RestoreReference { get; set; }
    public DateTime? ReviewAt { get; set; }
    public Guid RecordedBy { get; set; }
    public DateTime RecordedAt { get; set; }
}
