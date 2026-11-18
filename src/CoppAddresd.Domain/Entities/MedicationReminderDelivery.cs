namespace CoppAddresd.Domain.Entities;

/// <summary>Reserva durable de notificación. La toma puede ser una proyección sin fila materializada.</summary>
public sealed class MedicationReminderDelivery
{
    public Guid Id { get; set; }
    public Guid PatientId { get; set; }
    public Guid DoseId { get; set; }
    public string Channel { get; set; } = "";
    public int Phase { get; set; }
    public DateTime DueAt { get; set; }
    public DateTime? ClaimedAt { get; set; }
}
