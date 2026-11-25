namespace CoppAddresd.Infrastructure.Persistence;

internal sealed class WellnessJourneyGeneration
{
    public Guid Id { get; init; }
    public Guid SubmissionId { get; init; }
    public Guid PatientId { get; init; }
    public Guid PaymentId { get; init; }
    public string DraftHash { get; init; } = "";
    public string DraftJson { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; set; }
    public string Status { get; set; } = "";
    public int Attempts { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public string? ResultJson { get; set; }
    public string? ErrorCode { get; set; }
}
