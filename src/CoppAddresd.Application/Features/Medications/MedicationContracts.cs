namespace CoppAddresd.Application.Features.Medications;

public sealed class MedicationControlSettings
{
    public const string SectionName = "Medications";
    public bool Enabled { get; set; }
}

public sealed record MedicationAccessDto(bool Enabled, int Revision);
public sealed record MedicationPatientDto(Guid Id, string Name, string Mrn);
public sealed record MedicationPatientRow(Guid PatientId, string Name, string Mrn, bool Enabled, int ActiveTreatments);
public sealed record MedicationPatientPage(IReadOnlyList<MedicationPatientRow> Items, int Total, int Page, int PageSize);
public sealed record MedicationActivityDayDto(DateOnly Date, int Taken, int Omitted);
public sealed record MedicationOverviewDto(int TotalPatients, int EnabledPatients, int PatientsWithActiveTreatments,
    int ActiveTreatments, int TakenToday, DateOnly PeriodStart, DateOnly PeriodEnd, IReadOnlyList<MedicationActivityDayDto> Activity);
public sealed record MedicationSummaryDto(int ActiveTreatments, int ScheduledDoses, int TakenDoses, int PendingDoses);
public sealed record MedicationTreatmentDto(Guid Id, Guid? MedicationId, string Name, string ProductType,
    string Status, string Origin, int Revision, DateTime CreatedAt, decimal Dosage, string Unit,
    string Route, string Instructions, IReadOnlyList<string> Times, string TimeZone, DateOnly StartDate, DateOnly? EndDate)
{
    // El origen lo fija el endpoint, nunca un campo enviado por el paciente.
    // Nutracéuticos permanecen adicionales al plan obligatorio.
    public bool IsRequired => Origin == "staff_recorded" && ProductType != "supplement";
};
public sealed record MedicationDoseDto(Guid Id, Guid TreatmentId, string Name, decimal Dosage, string Unit,
    DateTime ScheduledAt, string TimeZone, string Status, DateTime? OccurredAt, int Revision);
public sealed record MedicationEventDto(Guid Id, Guid DoseId, Guid TreatmentId, string Name, string Status,
    DateTime OccurredAt, DateTime RecordedAt, string? Reason, string Origin);
public sealed record MedicationAdherenceDto(int ScheduledDoses, int DueDoses, int TakenDoses,
    int OmittedDoses, int UnrecordedDoses, int? Percent, int OffPlanTakenDoses);
public sealed record MedicationWorkspaceDto(MedicationPatientDto Patient, MedicationAccessDto Access,
    IReadOnlyList<MedicationTreatmentDto> Treatments, IReadOnlyList<MedicationDoseDto> Doses,
    IReadOnlyList<MedicationEventDto> History, MedicationSummaryDto Summary, MedicationAdherenceDto? Adherence = null);
public sealed record MedicationCurrentDayDto(IReadOnlyList<MedicationTreatmentDto> Treatments,
    IReadOnlyList<MedicationDoseDto> Doses);

public sealed record CreateMedicationTreatmentRequest(Guid ClientRequestId, Guid? MedicationId, string Name,
    string ProductType, decimal Dosage, string Unit, string? Route, string? Instructions,
    IReadOnlyList<string>? Times, string TimeZone, DateOnly StartDate, DateOnly? EndDate);
public sealed record RecordMedicationDoseRequest(Guid ClientRequestId, string Status,
    DateTimeOffset? OccurredAt = null, string? Reason = null, int? ExpectedRevision = null, DateTimeOffset? ScheduledAt = null);
public sealed record ChangeMedicationStatusRequest(string Status, int ExpectedRevision);
public sealed record ChangeMedicationAccessRequest(bool Enabled, int? ExpectedRevision = null);
public sealed record MedicationCreateResult(MedicationTreatmentDto Treatment, bool Created);

public interface IMedicationControlStore
{
    Task<MedicationOverviewDto> OverviewAsync(DateOnly today, string timeZone, Guid? clinicId, Guid? organizationId, CancellationToken ct);
    Task<MedicationAccessDto> GetAccessAsync(Guid patientId, CancellationToken ct);
    Task<bool> IsActivePatientAsync(Guid patientId, CancellationToken ct);
    Task EnsureContextAsync(Guid patientId, Guid? clinicId, Guid? organizationId, CancellationToken ct);
    Task<MedicationPatientPage> ListPatientsAsync(string? query, int page, int pageSize,
        Guid? clinicId, Guid? organizationId, CancellationToken ct);
    Task<MedicationAccessDto> SetAccessAsync(Guid patientId, Guid actorId, ChangeMedicationAccessRequest request, CancellationToken ct);
    Task<MedicationWorkspaceDto> WorkspaceAsync(Guid patientId, DateOnly date, string timeZone, bool selfService, CancellationToken ct);
    Task<MedicationCurrentDayDto> ReadCurrentDayAsync(Guid patientId, DateOnly date, TimeZoneInfo viewZone,
        DateTimeOffset asOf, CancellationToken ct);
    Task<MedicationCreateResult> CreateAsync(Guid patientId, Guid actorId, string origin,
        CreateMedicationTreatmentRequest request, CancellationToken ct);
    Task<MedicationDoseDto> RecordAsync(Guid patientId, Guid actorId, string origin, Guid doseId,
        RecordMedicationDoseRequest request, CancellationToken ct);
    Task<MedicationTreatmentDto> ChangeStatusAsync(Guid patientId, Guid treatmentId,
        ChangeMedicationStatusRequest request, bool selfService, CancellationToken ct);
}
