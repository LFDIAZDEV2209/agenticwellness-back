namespace CoppAddresd.Auth.Entities;

public sealed class PaidActivation
{
    public Guid PaymentId { get; set; }
    public string Email { get; set; } = null!;
    public string Plan { get; set; } = null!;
    public int AmountCents { get; set; }
    public string Language { get; set; } = null!;
    public DateTime ConfirmedAt { get; set; }
    public DateTime LastEmailRequestedAt { get; set; }
    public DateTime? NextEmailAttemptAt { get; set; }
    public DateTime? EmailLeaseUntil { get; set; }
    public int EmailRevision { get; set; }
    public int EmailAttempts { get; set; }
    public DateTime? RedeemedAt { get; set; }
    public Guid? LinkedUserId { get; set; }
    public Guid? SetupSessionId { get; set; }
    public DateTime? SetupSessionExpiresAt { get; set; }
}
