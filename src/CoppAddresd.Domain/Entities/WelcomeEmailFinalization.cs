namespace CoppAddresd.Domain.Entities;

/// <summary>Confirmación durable del alta ERP, pendiente de entregar al servicio Auth.</summary>
public sealed class WelcomeEmailFinalization
{
    public Guid UserId { get; set; }
    public Guid? InvitationId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;
    public int Attempts { get; set; }
}
