namespace CoppAddresd.Domain.Entities;

/// <summary>Marca persistida de lectura profesional; no cierra el ciclo ni congela sus comidas.</summary>
public sealed class NutritionIntakeDayReview
{
    public Guid PatientId { get; set; }
    public DateOnly LocalDate { get; set; }
    public Guid CycleId { get; set; }
    public Guid ReviewedBy { get; set; }
    public DateTime ReviewedAt { get; set; }
}
