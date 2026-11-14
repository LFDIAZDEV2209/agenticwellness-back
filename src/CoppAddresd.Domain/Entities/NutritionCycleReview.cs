namespace CoppAddresd.Domain.Entities;

/// <summary>Cierre profesional del ciclo revisado; el borrador IA nunca es el mensaje publicado.</summary>
public sealed class NutritionCycleReview
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CycleId { get; set; }
    public int Revision { get; set; }
    public Guid ReviewerId { get; set; }
    public DateTime ReviewedAt { get; set; }
    public string ProfessionalName { get; set; } = "";
    public string SummaryJson { get; set; } = "{}";
    public string? AiDraftJson { get; set; }
    public bool AiDraftConfirmed { get; set; }
    public string? ProfessionalNotes { get; set; }
    public string PatientMessageJson { get; set; } = "{}";
    public bool CriticalAlertsConfirmed { get; set; }
    public DateTime? PublishedAt { get; set; }
    public Guid? NextPrescriptionId { get; set; }
    public ICollection<NutritionCycleAttachment> Attachments { get; set; } = [];
}

/// <summary>Original privado e inmutable; solo los adjuntos de imagen expresamente visibles llegan a la app.</summary>
public sealed class NutritionCycleAttachment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ReviewId { get; set; }
    public string StorageKey { get; set; } = "";
    public string Kind { get; set; } = "image";
    public string ContentType { get; set; } = "";
    public long Length { get; set; }
    public string? Caption { get; set; }
    public bool VisibleToPatient { get; set; }
    public NutritionCycleReview Review { get; set; } = default!;
}
