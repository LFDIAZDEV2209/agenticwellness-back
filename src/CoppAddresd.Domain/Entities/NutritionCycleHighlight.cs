namespace CoppAddresd.Domain.Entities;

/// <summary>Ancla al registro vivo: corregir la comida no elimina el ejemplo ni congela sus valores.</summary>
public sealed class NutritionCycleHighlight
{
    public Guid CycleId { get; set; }
    public Guid IntakeLogId { get; set; }
    public Guid PinnedBy { get; set; }
    public DateTime PinnedAt { get; set; }
    public string? Note { get; set; }
}
