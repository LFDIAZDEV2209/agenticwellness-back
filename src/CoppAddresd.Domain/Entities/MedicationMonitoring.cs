namespace CoppAddresd.Domain.Entities;

/// <summary>Existencias del paciente en la misma unidad que la pauta registrada.</summary>
public sealed class MedicationSupply
{
    public Guid Id { get; set; }
    public Guid PatientId { get; set; }
    public Guid TreatmentId { get; set; }
    public decimal Balance { get; set; }
    public DateOnly? ExpiresOn { get; set; }
    public int Revision { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
public sealed class MedicationStockMovement
{
    public Guid Id { get; set; }
    public Guid PatientId { get; set; }
    public Guid SupplyId { get; set; }
    public Guid? DoseId { get; set; }
    public Guid? IntakeEventId { get; set; }
    public string Kind { get; set; } = "";
    public decimal QuantityDelta { get; set; }
    public decimal BalanceAfter { get; set; }
    public int SupplyRevision { get; set; }
    public Guid ActorId { get; set; }
    public string Origin { get; set; } = "";
    public string? Reason { get; set; }
    public DateTime RecordedAt { get; set; }
}
public sealed class MedicationEffectReport
{
    public Guid Id { get; set; }
    public Guid PatientId { get; set; }
    public Guid? TreatmentId { get; set; }
    public string Symptom { get; set; } = "";
    public string Severity { get; set; } = "mild";
    public string Status { get; set; } = "open";
    public string? Notes { get; set; }
    public Guid ReportedBy { get; set; }
    public string Origin { get; set; } = "patient_reported";
    public DateTime OccurredAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public int Revision { get; set; } = 1;
}
public sealed class MedicationEffectFollowUp
{
    public Guid Id { get; set; }
    public Guid PatientId { get; set; }
    public Guid ReportId { get; set; }
    public Guid ActorId { get; set; }
    public string Status { get; set; } = "";
    public string Note { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
public sealed class MedicationMonitoringAlert
{
    public Guid Id { get; set; }
    public Guid PatientId { get; set; }
    public Guid? TreatmentId { get; set; }
    public string DedupKey { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Severity { get; set; } = "warning";
    public string Status { get; set; } = "open";
    public bool ConditionActive { get; set; } = true;
    public Guid? AssignedTo { get; set; }
    public string? ResolutionNote { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int Revision { get; set; } = 1;
}
public sealed class MedicationAlertFollowUp
{
    public Guid Id { get; set; }
    public Guid PatientId { get; set; }
    public Guid AlertId { get; set; }
    public Guid? ActorId { get; set; }
    public string Status { get; set; } = "";
    public string Note { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
/// <summary>Reserva idempotente de altas y acciones aceptadas, incluso no-op.</summary>
public sealed class MedicationMonitoringOperation
{
    public Guid Id { get; set; }
    public Guid PatientId { get; set; }
    public Guid ClientRequestId { get; set; }
    public string RequestHash { get; set; } = "";
    public Guid ResultId { get; set; }
    public string Kind { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
/// <summary>Bitácora de aplicación append-only; sin copiar contenido clínico al acceso.</summary>
public sealed class MedicationAuditEntry
{
    public Guid Id { get; set; }
    public Guid PatientId { get; set; }
    public Guid? ActorId { get; set; }
    public string Origin { get; set; } = "";
    public string Action { get; set; } = "";
    public string Resource { get; set; } = "";
    public string? ActorEmail { get; set; }
    public string? RequestId { get; set; }
    public Guid? ResourceId { get; set; }
    public string? Purpose { get; set; }
    public DateTime CreatedAt { get; set; }
}
