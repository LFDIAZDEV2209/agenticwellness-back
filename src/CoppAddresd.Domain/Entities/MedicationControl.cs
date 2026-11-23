namespace CoppAddresd.Domain.Entities;

/// <summary>Tratamiento longitudinal; su identidad no depende de la lista editable antigua.</summary>
public sealed class MedicationTreatment
{
    public Guid Id { get; set; }
    public Guid PatientId { get; set; }
    public Guid? MedicationId { get; set; }
    public string? RxCui { get; set; }
    public string Name { get; set; } = "";
    public string ProductType { get; set; } = "medication";
    public string Origin { get; set; } = "patient_reported";
    public string Status { get; set; } = "active";
    public int Revision { get; set; } = 1;
    public Guid ClientRequestId { get; set; }
    public string RequestHash { get; set; } = "";
    public string? PhotoStorageKey { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Copia versionada de la pauta registrada, sin reinterpretar eventos anteriores.</summary>
public sealed class MedicationRegimen
{
    public Guid Id { get; set; }
    public Guid TreatmentId { get; set; }
    public int Version { get; set; } = 1;
    public DateTime? EffectiveFrom { get; set; }
    public DateTime? EffectiveUntil { get; set; }
    public int? WeekdayMask { get; set; }
    public int? IntervalHours { get; set; }
    public bool? PushReminder { get; set; }
    public bool? InAppReminder { get; set; }
    public bool? WhatsAppReminder { get; set; }
    public int? LeadMinutes { get; set; }
    public int? FollowUpMinutes { get; set; }
    public decimal Dosage { get; set; }
    public string Unit { get; set; } = "";
    public string Route { get; set; } = "";
    public string Instructions { get; set; } = "";
    public string TimeZone { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class MedicationScheduleSlot
{
    public Guid Id { get; set; }
    public Guid RegimenId { get; set; }
    public TimeOnly LocalTime { get; set; }
}

public sealed class MedicationDose
{
    public Guid Id { get; set; }
    public Guid PatientId { get; set; }
    public Guid TreatmentId { get; set; }
    public Guid RegimenId { get; set; }
    public Guid SlotId { get; set; }
    public DateTime ScheduledAt { get; set; }
    public string TimeZone { get; set; } = "";
    public string Status { get; set; } = "pending";
    public DateTime? OccurredAt { get; set; }
    public int Revision { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Registro atribuible; las correcciones añaden eventos en vez de borrar el pasado.</summary>
public sealed class MedicationIntakeEvent
{
    public Guid Id { get; set; }
    public Guid PatientId { get; set; }
    public Guid TreatmentId { get; set; }
    public Guid DoseId { get; set; }
    public Guid ClientRequestId { get; set; }
    public string RequestHash { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime OccurredAt { get; set; }
    public DateTime RecordedAt { get; set; }
    public Guid ActorId { get; set; }
    public string Origin { get; set; } = "";
    public string? Reason { get; set; }
    public string? Channel { get; set; }
    public int? DoseRevision { get; set; }
    public Guid? CorrectsEventId { get; set; }
    public string? PhotoStorageKey { get; set; }
}

/// <summary>Habilitación explícita del módulo por paciente, independiente de permisos del personal.</summary>
public sealed class PatientModuleAccess
{
    public Guid Id { get; set; }
    public Guid PatientId { get; set; }
    public string ModuleCode { get; set; } = "medications";
    public bool Enabled { get; set; } = true;
    public int Revision { get; set; }
    public Guid UpdatedBy { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Estado efectivo por instante; una reactivación no reescribe periodos pausados.</summary>
public sealed class MedicationTreatmentStatusEvent
{
    public Guid Id { get; set; }
    public Guid TreatmentId { get; set; }
    public string Status { get; set; } = "active";
    public int Revision { get; set; }
    public string? Reason { get; set; }
    public DateTime EffectiveAt { get; set; }
}

/// <summary>Reserva también las operaciones aceptadas sin un nuevo evento clínico.</summary>
public sealed class MedicationDoseOperation
{
    public Guid Id { get; set; }
    public Guid PatientId { get; set; }
    public Guid DoseId { get; set; }
    public Guid ClientRequestId { get; set; }
    public string RequestHash { get; set; } = "";
    public DateTime AcceptedAt { get; set; }
}

/// <summary>Preferencias de avisos y versión observable de la agenda en todas las instancias.</summary>
public sealed class MedicationPatientState
{
    public Guid Id { get; set; }
    public Guid Version { get; set; }
    public bool PushEnabled { get; set; } = true;
    public bool InAppEnabled { get; set; } = true;
    public bool WhatsAppEnabled { get; set; }
    public int LeadMinutes { get; set; }
    public int? FollowUpMinutes { get; set; }
}
