namespace CoppAddresd.Auth.Entities;

/// <summary>Outbox de bienvenida: no almacena contraseñas, cuerpos ni tokens en claro.</summary>
public sealed class WelcomeEmailDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string DeduplicationKey { get; set; } = string.Empty;
    public Guid? InvitationId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReadyAt { get; set; }
    public string Status { get; set; } = "Pending";
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;
    public DateTime? LeaseUntil { get; set; }
    public DateTime? SentAt { get; set; }
    public string? LastErrorCode { get; set; }
}
