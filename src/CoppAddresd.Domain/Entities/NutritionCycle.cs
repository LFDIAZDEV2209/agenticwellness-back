namespace CoppAddresd.Domain.Entities;

/// <summary>Cabecera de ciclo; D0013 reserva número y vigencia a la primera publicación.</summary>
public sealed class NutritionCycle
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PatientId { get; set; }
    public int? Number { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public string State { get; set; } = "borrador";
}
