namespace CoppAddresd.Domain.Wellness365;

/// <summary>Respuestas de un envío de cuestionario.</summary>
public sealed class Motor365Screening
{
    public Guid PersonId { get; init; }
    public Guid SubmissionId { get; init; }
    public DateTimeOffset RecordedAt { get; init; }
    public short? ParQ1 { get; set; }
    public short? ParQ2 { get; set; }
    public short? ParQ3 { get; set; }
    public short? ParQ4 { get; set; }
    public short? ParQ5 { get; set; }
    public short? ParQ6 { get; set; }
    public short? ParQ7 { get; set; }
    public short? Nqs1 { get; set; }
    public short? Nqs2 { get; set; }
    public short? Nqs3 { get; set; }
    public short? Nqs4 { get; set; }
    public short? Nqs5 { get; set; }
    public short? Nqs6 { get; set; }

    // La base calcula la misma compuerta de completitud para proteger la caché.
    public bool HasCompleteAnswers =>
        ParQ1.HasValue && ParQ2.HasValue && ParQ3.HasValue && ParQ4.HasValue &&
        ParQ5.HasValue && ParQ6.HasValue && ParQ7.HasValue &&
        Nqs1.HasValue && Nqs2.HasValue && Nqs3.HasValue && Nqs4.HasValue &&
        Nqs5.HasValue && Nqs6.HasValue;
}
