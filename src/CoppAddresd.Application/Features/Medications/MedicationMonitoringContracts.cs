namespace CoppAddresd.Application.Features.Medications;

public sealed record MedicationPage<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);
public sealed record MedicationTrackingDoseDto(MedicationDoseDto Dose, DateOnly LocalDate, bool IsRequired, string Timing, int? DelayMinutes);
public sealed record MedicationTrackingDayDto(DateOnly Date, int Scheduled, int Due, int Taken, int Omitted, int Unrecorded, int? Percent);
public sealed record MedicationWeekDto(MedicationPatientDto Patient, DateOnly From, DateOnly To, string TimeZone,
    IReadOnlyList<MedicationTreatmentDto> Treatments, IReadOnlyList<MedicationTrackingDoseDto> Doses,
    IReadOnlyList<MedicationTrackingDayDto> Days);
public sealed record MedicationSupplyDto(Guid Id, Guid PatientId, string PatientName, Guid TreatmentId, string Name,
    string Unit, decimal Balance, decimal DailyUsage, int? DaysRemaining, DateOnly? ExpiresOn, int Revision, bool Tracked);
public sealed record MedicationMovementDto(Guid Id, string Kind, decimal QuantityDelta, decimal BalanceAfter,
    string? Reason, DateTime RecordedAt, string Origin);
public sealed record MedicationStockRequest(Guid ClientRequestId, string Kind, decimal Quantity,
    int ExpectedRevision, DateOnly? ExpiresOn = null, string? Reason = null);
public sealed record MedicationEffectRequest(Guid ClientRequestId, Guid? TreatmentId, string Symptom,
    string Severity, string? Notes = null, DateTimeOffset? OccurredAt = null);
public sealed record MedicationFollowUpRequest(Guid ClientRequestId, string Status, int ExpectedRevision, string Note);
public sealed record MedicationEffectFollowUpDto(Guid Id, Guid ActorId, string Status, string Note, DateTime CreatedAt);
public sealed record MedicationEffectDto(Guid Id, Guid PatientId, Guid? TreatmentId, string? TreatmentName,
    string Symptom, string Severity, string Status, string? Notes, DateTime OccurredAt, DateTime CreatedAt,
    int Revision, IReadOnlyList<MedicationEffectFollowUpDto> FollowUps);
public sealed record MedicationAlertFollowUpDto(Guid Id, Guid? ActorId, string Status, string Note, DateTime CreatedAt);
public sealed record MedicationAlertDto(Guid Id, Guid PatientId, string PatientName, Guid? TreatmentId, string? TreatmentName,
    string Kind, string Severity, string Status, bool ConditionActive, Guid? AssignedTo, string? ResolutionNote,
    int Revision, DateTime CreatedAt, IReadOnlyList<MedicationAlertFollowUpDto> FollowUps);
public sealed record MedicationAuditDto(Guid Id, Guid PatientId, string PatientName, Guid? ActorId, string Origin,
    string Action, string Resource, Guid? ResourceId, string? Purpose, DateTime CreatedAt, string? ActorEmail, string? RequestId);
public sealed record MedicationAuditFilter(Guid? PatientId, string? Action, DateTimeOffset? From, DateTimeOffset? To, Guid? ActorId = null);

public interface IMedicationMonitoringStore
{
    Task<MedicationWeekDto> WeekAsync(Guid patientId, DateOnly from, string timeZone, CancellationToken ct);
    Task<MedicationPage<MedicationSupplyDto>> InventoryAsync(Guid? patientId, string? query, int page, int pageSize, Guid? clinicId, Guid? organizationId, bool selfService, CancellationToken ct);
    Task<IReadOnlyList<MedicationMovementDto>> MovementsAsync(Guid patientId, Guid treatmentId, CancellationToken ct);
    Task<MedicationSupplyDto> StockAsync(Guid patientId, Guid actorId, string origin, Guid treatmentId, MedicationStockRequest request, CancellationToken ct);
    Task<IReadOnlyList<MedicationEffectDto>> EffectsAsync(Guid patientId, CancellationToken ct);
    Task<MedicationEffectDto> ReportEffectAsync(Guid patientId, Guid actorId, string origin, MedicationEffectRequest request, CancellationToken ct);
    Task<MedicationEffectDto> ReviewEffectAsync(Guid patientId, Guid actorId, Guid reportId, MedicationFollowUpRequest request, CancellationToken ct);
    Task<MedicationPage<MedicationAlertDto>> AlertsAsync(Guid? patientId, string? status, string? kind, int page, int pageSize, Guid? clinicId, Guid? organizationId, CancellationToken ct);
    Task<MedicationAlertDto> FollowAlertAsync(Guid patientId, Guid actorId, Guid alertId, MedicationFollowUpRequest request, CancellationToken ct);
    Task<MedicationPage<MedicationAuditDto>> AuditAsync(MedicationAuditFilter filter, int page, int pageSize, Guid? clinicId, Guid? organizationId, CancellationToken ct);
    Task AuditAccessAsync(IEnumerable<Guid> patientIds, Guid actorId, string origin, string action, string resource, string? purpose, CancellationToken ct);
    Task RefreshAlertsAsync(Guid patientId, CancellationToken ct);
    Task<IReadOnlyList<Guid>> MonitoredPatientsAsync(Guid? after, int take, CancellationToken ct);
}
