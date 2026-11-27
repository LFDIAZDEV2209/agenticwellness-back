namespace CoppAddresd.Application.Features.Motor365;

public sealed record MotorTimelineFilter(DateOnly? From, DateOnly? To, string? Kind)
{
    public bool IsValid => !(From > To) && Kind is null or "ajuste" or "sin" or "meta" or "modo";
}

public sealed record MotorTimelineEvent(Guid Id, int Day, string Kind, DateTimeOffset OccurredAt,
    int PlanVersion, int ParametersVersion, string EngineVersion, decimal? CalorieDelta, decimal? RatePercent,
    string MessageEs, string MessageEn);

public sealed record MotorTimelinePage(IReadOnlyList<MotorTimelineEvent> Events, int Page, bool HasNext);

public interface IMotorTimelineStore
{
    Task<MotorTimelinePage> ReadAsync(Guid patientId, MotorTimelineFilter filter, int page, CancellationToken ct);
    Task<IReadOnlyList<MotorTimelineEvent>> ReadForExportAsync(Guid patientId, MotorTimelineFilter filter, CancellationToken ct);
    Task AuditExportAsync(Guid patientId, Guid actorId, DateTimeOffset occurredAt, CancellationToken ct);
}
