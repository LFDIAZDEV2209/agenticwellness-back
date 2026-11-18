using FluentValidation;
using FluentValidation.Results;

namespace CoppAddresd.Application.Features.Medications;

/// <summary>Validación común de los contratos de seguimiento; autorización en la política existente.</summary>
public sealed class MedicationMonitoringService(IMedicationMonitoringStore store, TimeProvider clock)
{
    public IMedicationMonitoringStore Store => store;
    public static void Page(int page, int pageSize)
    {
        if (page < 1 || page > 100000 || pageSize is < 1 or > 100) throw Invalid("page", "Paginación no válida.");
    }
    public DateOnly Date(DateOnly? value, string timeZone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), MedicationControlService.Zone(timeZone)).DateTime);
        if (value is { } date && Math.Abs(date.DayNumber - today.DayNumber) > 366) throw Invalid("date", "Fecha fuera de rango.");
        return value ?? today;
    }
    public Task<MedicationSupplyDto> StockAsync(Guid patientId, Guid actorId, string origin, Guid treatmentId, MedicationStockRequest input, CancellationToken ct)
    {
        if (input.ClientRequestId == Guid.Empty || input.Kind is not ("initial" or "refill" or "adjustment")
            || input.Quantity < 0 || input.Quantity > 1000000 || decimal.Round(input.Quantity, 4) != input.Quantity
            || input.Kind == "refill" && input.Quantity == 0 || input.ExpectedRevision < 0
            || (input.Reason?.Length ?? 0) > 500 || input.Kind == "adjustment" && string.IsNullOrWhiteSpace(input.Reason)
            || input.ExpiresOn is { Year: < 1900 or > 2100 }) throw Invalid("inventory", "Revisa la cantidad, vigencia y motivo.");
        return store.StockAsync(patientId, actorId, origin, treatmentId, input with { Reason = input.Reason?.Trim() }, ct);
    }
    public Task<MedicationEffectDto> ReportAsync(Guid patientId, Guid actorId, string origin, MedicationEffectRequest input, CancellationToken ct)
    {
        if (input.ClientRequestId == Guid.Empty || string.IsNullOrWhiteSpace(input.Symptom) || input.Symptom.Trim().Length > 200
            || input.Severity is not ("mild" or "moderate" or "severe") || (input.Notes?.Length ?? 0) > 1000
            || input.OccurredAt > clock.GetUtcNow().AddMinutes(1) || input.OccurredAt < clock.GetUtcNow().AddDays(-366))
            throw Invalid("effect", "Revisa el síntoma, intensidad y fecha.");
        return store.ReportEffectAsync(patientId, actorId, origin, input with { Symptom = input.Symptom.Trim(), Notes = input.Notes?.Trim() }, ct);
    }
    public Task<MedicationEffectDto> ReviewAsync(Guid patientId, Guid actorId, Guid reportId, MedicationFollowUpRequest input, CancellationToken ct)
    {
        FollowUp(input, ["follow_up", "resolved"]);
        return store.ReviewEffectAsync(patientId, actorId, reportId, input with { Note = input.Note.Trim() }, ct);
    }
    public Task<MedicationAlertDto> AlertAsync(Guid patientId, Guid actorId, Guid alertId, MedicationFollowUpRequest input, CancellationToken ct)
    {
        FollowUp(input, ["in_progress", "resolved"]);
        return store.FollowAlertAsync(patientId, actorId, alertId, input with { Note = input.Note.Trim() }, ct);
    }
    private static void FollowUp(MedicationFollowUpRequest input, string[] statuses)
    {
        if (input.ClientRequestId == Guid.Empty || !statuses.Contains(input.Status) || input.ExpectedRevision < 1
            || string.IsNullOrWhiteSpace(input.Note) || input.Note.Trim().Length > 1000)
            throw Invalid("followUp", "Selecciona un estado y escribe el motivo.");
    }
    private static ValidationException Invalid(string field, string message) => new([new ValidationFailure(field, message)]);
}
