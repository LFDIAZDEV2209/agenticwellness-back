namespace CoppAddresd.Domain.Wellness365;

/// <summary>Revisión profesional de un envío de cuestionario.</summary>
public sealed class Motor365ProfessionalReview
{
    public Guid Id { get; init; }
    public Guid PersonId { get; init; }
    public Guid SubmissionId { get; init; }
    public Guid ProfessionalId { get; init; }
    public Motor365ReviewDecision Decision { get; init; }
    public string? Notes { get; init; }
    public DateTimeOffset ReviewedAt { get; init; }
}

public enum Motor365ReviewDecision { Approved, Rejected, NeedsChanges }
