namespace CoppAddresd.Application.Features.Medications;

public sealed record MedicationReminderOccurrence(Guid PatientId, Guid UserId, MedicationDoseDto Dose, string? Instructions = null, MedicationReminderPreferences? Preferences = null);

public interface IMedicationReminderChannel
{
    string Code { get; }
    bool Enabled => true;
    Task DispatchAsync(MedicationReminderOccurrence occurrence, Guid deliveryId, CancellationToken ct);
}

public interface IMedicationReminderScheduler
{
    Task<MedicationReminderOccurrence?> GetPendingAsync(Guid patientId, Guid doseId, DateTime scheduledAt, CancellationToken ct);

    Task<bool> SnoozeAsync(Guid patientId, Guid doseId, DateTime scheduledAt, int minutes, CancellationToken ct, string? channel = null);
}

public sealed record SnoozeMedicationDoseRequest(DateTimeOffset ScheduledAt, int Minutes);


public interface IMedicationWhatsAppReplies
{
    Task<bool> ApplyAsync(Guid patientId, Guid userId, Guid deliveryId, string action, CancellationToken ct);
}
