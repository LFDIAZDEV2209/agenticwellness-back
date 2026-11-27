namespace CoppAddresd.Auth.Entities;

/// <summary>Fechas y evidencia de verificación y ejecución. No contiene las preferencias suprimidas.</summary>
public sealed class PrivacyDeletionCase
{
    public Guid RequestId { get; set; }
    public DateTime VerifiedAt { get; set; }
    public Guid VerifiedBy { get; set; }
    public string IdentityEvidence { get; set; } = null!;
    public DateTime DueAt { get; set; }
    public string PolicyReference { get; set; } = null!;
    public DateTime? DeadlineChangedAt { get; set; }
    public Guid? DeadlineChangedBy { get; set; }
    public DateTime? PreferencesErasedAt { get; set; }
    public Guid? PreferencesErasedBy { get; set; }
    public string? PreferencesErasureEvidence { get; set; }
    public int? PreferencesRowsErased { get; set; }
    public Guid? ClosedBy { get; set; }
    public int? CompletionSourceCount { get; set; }
    public string? InventoryReference { get; set; }
    public string? RestoreReference { get; set; }
}
