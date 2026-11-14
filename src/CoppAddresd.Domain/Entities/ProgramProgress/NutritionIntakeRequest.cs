namespace CoppAddresd.Domain.Entities.ProgramProgress;

/// <summary>Clave de operación por paciente; permite varias actualizaciones de hidratación sobre el mismo registro.</summary>
public sealed class NutritionIntakeRequest
{
    public Guid PatientId { get; set; }
    public string ClientRequestId { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public Guid HabitCheckId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
