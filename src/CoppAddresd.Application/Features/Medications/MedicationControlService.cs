using System.Globalization;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Exceptions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Options;

namespace CoppAddresd.Application.Features.Medications;

/// <summary>Política del módulo y validación; persistencia/atomicidad detrás del store.</summary>
public sealed class MedicationControlService(IMedicationControlStore store, IPatientScopeGuard scope,
    IOptionsSnapshot<MedicationControlSettings> settings, TimeProvider clock)
{
    public bool Enabled => settings.Value.Enabled;

    /// <summary>Lectura acotada para el paciente ya resuelto y autorizado por Vita.</summary>
    public async Task<MedicationCurrentDayDto> ReadCurrentDayAsync(Guid patientId, DateOnly date,
        TimeZoneInfo viewZone, DateTimeOffset asOf, CancellationToken ct)
    {
        EnsureEnabled();
        if (!(await store.GetAccessAsync(patientId, ct)).Enabled)
            throw new ForbiddenException("VITA_MEDICATIONS_FORBIDDEN");
        return await store.ReadCurrentDayAsync(patientId, date, viewZone, asOf, ct);
    }

    public void EnsureEnabled()
    {
        if (!Enabled) throw new NotFoundException("Módulo no disponible.");
    }

    public async Task<MedicationAccessDto> SelfAccessAsync(Guid userId, CancellationToken ct)
    {
        if (!Enabled) return new(false, 0);
        try
        {
            var patientId = await scope.EnsureSelfAsync(userId, ct);
            return await store.IsActivePatientAsync(patientId, ct)
                ? await store.GetAccessAsync(patientId, ct) : new(false, 0);
        }
        catch (NotFoundException) { return new(false, 0); }
    }

    public async Task<Guid> EnsureSelfAsync(Guid userId, CancellationToken ct)
    {
        EnsureEnabled();
        var patientId = await scope.EnsureSelfAsync(userId, ct);
        if (!await store.IsActivePatientAsync(patientId, ct) || !(await store.GetAccessAsync(patientId, ct)).Enabled)
            throw new ForbiddenException("Control de medicamentos no está habilitado para tu cuenta.");
        return patientId;
    }

    public async Task EnsureStaffAsync(Guid patientId, string permission, Guid? clinicId, Guid? organizationId, CancellationToken ct)
    {
        EnsureEnabled();
        await scope.EnsureAsync(patientId, permission, ct);
        await store.EnsureContextAsync(patientId, clinicId, organizationId, ct);
    }

    public Task<MedicationOverviewDto> OverviewAsync(string? timeZone, Guid? clinicId, Guid? organizationId, CancellationToken ct)
    {
        EnsureEnabled();
        var zone = Zone(timeZone ?? "America/Bogota");
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);
        return store.OverviewAsync(today, zone.Id, clinicId, organizationId, ct);
    }

    public Task<MedicationPatientPage> ListPatientsAsync(string? query, int page, int pageSize,
        Guid? clinicId, Guid? organizationId, CancellationToken ct)
    {
        EnsureEnabled();
        if (page < 1 || pageSize is < 1 or > 100 || (query?.Length ?? 0) > 200)
            throw Invalid("page", "La búsqueda o paginación no es válida.");
        return store.ListPatientsAsync(query?.Trim(), page, pageSize, clinicId, organizationId, ct);
    }

    public Task<MedicationAccessDto> SetAccessAsync(Guid patientId, Guid actorId, ChangeMedicationAccessRequest request, CancellationToken ct)
        => store.SetAccessAsync(patientId, actorId, request, ct);

    public Task<MedicationWorkspaceDto> WorkspaceAsync(Guid patientId, DateOnly? date, string? timeZone, bool selfService, CancellationToken ct)
    {
        var zone = Zone(timeZone ?? "America/Bogota");
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);
        var day = date ?? today;
        if (Math.Abs(day.DayNumber - today.DayNumber) > 366)
            throw Invalid("date", "Selecciona una fecha dentro del último o próximo año.");
        return store.WorkspaceAsync(patientId, day, zone.Id, selfService, ct);
    }

    public Task<MedicationCreateResult> CreateAsync(Guid patientId, Guid actorId, string origin,
        CreateMedicationTreatmentRequest request, CancellationToken ct)
        => store.CreateAsync(patientId, actorId, origin, ValidateTreatment(request, origin), ct);

    public Task<MedicationAdherenceSummaryDto> AdherenceAsync(Guid patientId, string? timeZone, CancellationToken ct)
    {
        var zone = Zone(timeZone ?? "America/Bogota");
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);
        return store.AdherenceAsync(patientId, today, zone.Id, ct);
    }

    public Task<Guid> VersionAsync(Guid patientId, CancellationToken ct) => store.VersionAsync(patientId, ct);

    public Task<MedicationReminderPreferences> GetReminderPreferencesAsync(Guid patientId, CancellationToken ct)
        => store.GetReminderPreferencesAsync(patientId, ct);

    public Task<MedicationReminderPreferences> SetReminderPreferencesAsync(Guid patientId, Guid actorId,
        MedicationReminderPreferences request, CancellationToken ct)
    {
        ValidateReminderMinutes(request.LeadMinutes, request.FollowUpMinutes);
        return store.SetReminderPreferencesAsync(patientId, actorId, request, ct);
    }

    public Task<MedicationTreatmentDto> UpdateRegimenAsync(Guid patientId, Guid treatmentId, Guid actorId,
        UpdateMedicationRegimenRequest request, CancellationToken ct)
    {
        if (request.ExpectedRevision < 1) throw Invalid("expectedRevision", "Revisa la revisión del tratamiento.");
        var validated = ValidateTreatment(new(Guid.NewGuid(), null, "Pauta", "medication", request.Dosage,
            request.Unit, request.Route, request.Instructions, request.Times, request.TimeZone,
            request.StartDate, request.EndDate, WeekdayMask: request.WeekdayMask,
            IntervalHours: request.IntervalHours, Reminders: request.Reminders), "staff_recorded");
        return store.UpdateRegimenAsync(patientId, treatmentId, actorId, request with
        {
            Unit = validated.Unit, Route = validated.Route, Instructions = validated.Instructions,
            Times = validated.Times, TimeZone = validated.TimeZone,
        }, ct);
    }

    private static CreateMedicationTreatmentRequest ValidateTreatment(CreateMedicationTreatmentRequest request, string origin)
    {
        var errors = new List<ValidationFailure>();
        if (request.ClientRequestId == Guid.Empty) errors.Add(new("clientRequestId", "La operación debe tener un identificador."));
        if (request.MedicationId == Guid.Empty) errors.Add(new("medicationId", "Medicamento no válido."));
        if (request.RxCui is { } code && (code.Length is < 1 or > 18 || code.Any(c => !char.IsAsciiDigit(c))
            || request.MedicationId is not null || request.ProductType != "medication"
            || origin is not ("staff_recorded" or "patient_reported")))
            errors.Add(new("rxCui", "Selecciona un medicamento válido del catálogo de EE. UU."));
        if (request.RxCui is null && (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200)) errors.Add(new("name", "Escribe un nombre de hasta 200 caracteres."));
        if (request.ProductType is not ("medication" or "vitamin" or "supplement")) errors.Add(new("productType", "Selecciona el tipo de producto."));
        if (request.Dosage <= 0 || request.Dosage > 1000000 || decimal.Round(request.Dosage, 4) != request.Dosage) errors.Add(new("dosage", "Escribe una cantidad positiva con hasta cuatro decimales."));
        if (string.IsNullOrWhiteSpace(request.Unit) || request.Unit.Trim().Length > 40) errors.Add(new("unit", "Escribe una unidad de hasta 40 caracteres."));
        if (request.RxCui is not null && string.IsNullOrWhiteSpace(request.Route)) errors.Add(new("route", "Registra la vía del tratamiento."));
        if ((request.Route?.Length ?? 0) > 80) errors.Add(new("route", "La vía admite hasta 80 caracteres."));
        if ((request.Instructions?.Length ?? 0) > 2000) errors.Add(new("instructions", "Las indicaciones admiten hasta 2000 caracteres."));
        if (request.StartDate.Year is < 1900 or > 2100 || request.EndDate < request.StartDate) errors.Add(new("startDate", "Revisa las fechas de vigencia."));
        var times = request.Times?.Select(t => t?.Trim() ?? "").ToArray() ?? [];
        if (times.Length is < 1 or > 8 || times.Distinct().Count() != times.Length || times.Any(t => !TimeOnly.TryParseExact(t, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
            errors.Add(new("times", "Añade entre uno y ocho horarios distintos en formato HH:mm."));
        if (request.WeekdayMask is < 1 or > 127 || request.IntervalHours is < 3 or > 168
            || (request.IntervalHours.HasValue && (times.Length != 1 || request.WeekdayMask.HasValue)))
            errors.Add(new("frequency", "Revisa la frecuencia y el primer horario."));
        ValidateReminderMinutes(request.Reminders?.LeadMinutes, request.Reminders?.FollowUpMinutes);
        TimeZoneInfo? zone = null;
        try { zone = Zone(request.TimeZone); } catch (ValidationException ex) { errors.AddRange(ex.Errors); }
        if (errors.Count > 0) throw new ValidationException(errors);
        return request with
        {
            Name = request.RxCui is null ? request.Name.Trim() : "", Unit = request.Unit.Trim(), Route = request.Route?.Trim() ?? "",
            Instructions = request.Instructions?.Trim() ?? "", Times = times.Order().ToArray(), TimeZone = zone!.Id,
        };
    }

    public Task<MedicationDoseDto> RecordAsync(Guid patientId, Guid actorId, string origin, Guid doseId,
        RecordMedicationDoseRequest request, CancellationToken ct)
        => store.RecordAsync(patientId, actorId, origin, doseId, ValidateRecord(request, clock.GetUtcNow()), ct);

    public static RecordMedicationDoseRequest ValidateRecord(RecordMedicationDoseRequest request, DateTimeOffset now)
    {
        if (request.ClientRequestId == Guid.Empty || request.Status is not ("taken" or "omitted" or "pending")
            || (request.Reason?.Length ?? 0) > 500 || request.ExpectedRevision < 0)
            throw Invalid("dose", "Revisa el estado, la operación y el motivo de la toma.");
        if (request.ScheduledAt is { } scheduled
            && Math.Abs(DateOnly.FromDateTime(scheduled.UtcDateTime).DayNumber
                - DateOnly.FromDateTime(now.UtcDateTime).DayNumber) > 366)
            throw Invalid("scheduledAt", "Selecciona una fecha dentro del último o próximo año.");
        var occurred = request.OccurredAt ?? now;
        if (occurred > now.AddMinutes(1)) throw Invalid("occurredAt", "La toma no puede tener una fecha futura.");
        return request with { Reason = request.Reason?.Trim() };
    }

    public Task<MedicationTreatmentDto> ChangeStatusAsync(Guid patientId, Guid treatmentId,
        ChangeMedicationStatusRequest request, bool selfService, CancellationToken ct)
    {
        if (request.Status is not ("active" or "paused" or "completed") || (request.ExpectedRevision < 1 || request.Reason?.Length > 500))
            throw Invalid("status", "Revisa el estado y la revisión del tratamiento.");
        return store.ChangeStatusAsync(patientId, treatmentId, request with { Reason = request.Reason?.Trim() }, selfService, ct);
    }

    private static void ValidateReminderMinutes(int? lead, int? followUp)
    {
        if (lead is < 0 or > 30 || followUp is < 15 or > 90)
            throw Invalid("reminders", "Revisa la anticipación y el intervalo de recordatorio.");
    }

    public static TimeZoneInfo Zone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 100) throw Invalid("timeZone", "Selecciona una zona horaria válida.");
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { throw Invalid("timeZone", "Selecciona una zona horaria válida."); }
        catch (InvalidTimeZoneException) { throw Invalid("timeZone", "Selecciona una zona horaria válida."); }
    }

    private static ValidationException Invalid(string field, string message) => new([new ValidationFailure(field, message)]);
}
