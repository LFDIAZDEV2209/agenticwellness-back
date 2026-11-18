using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoppAddresd.Application.Features.Medications;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

/// <summary>Persistencia del circuito inicial. Cada mutación serializa solo el paciente afectado.</summary>
public sealed partial class MedicationControlStore(AppDbContext db, IPatientScopeGuard scope, TimeProvider clock, IPatientScopeActor? auditActor = null, IAuditActorContext? auditContext = null) : IMedicationControlStore, IMedicationMonitoringStore
{
    public async Task<MedicationAccessDto> GetAccessAsync(Guid patientId, CancellationToken ct)
        => await MedicationAccessQuery.Resolve(db).Where(a => a.PatientId == patientId)
            .Select(a => new MedicationAccessDto(a.Enabled, a.Revision)).SingleOrDefaultAsync(ct) ?? new(false, 0);

    public Task<bool> IsActivePatientAsync(Guid patientId, CancellationToken ct)
        => db.PatientProfiles.AsNoTracking().AnyAsync(p => p.Id == patientId && p.DeletedAt == null
            && p.Status != "Inactivo" && p.Status != "Inactive", ct);

    public async Task EnsureContextAsync(Guid patientId, Guid? clinicId, Guid? organizationId, CancellationToken ct)
    {
        if (!await ContextPatients(clinicId, organizationId).AnyAsync(p => p.Id == patientId, ct)) throw Missing();
    }

    private IQueryable<PatientProfile> ContextPatients(Guid? clinicId, Guid? organizationId)
    {
        var query = db.PatientProfiles.AsNoTracking().Where(p => p.DeletedAt == null);
        if (clinicId is { } clinic) query = query.Where(p => p.ClinicId == clinic);
        if (organizationId is { } organization)
            query = query.Where(p => p.ClinicId != null && db.Clinics.Any(c => c.Id == p.ClinicId && c.OrganizationId == organization));
        return query;
    }

    public async Task<MedicationOverviewDto> OverviewAsync(DateOnly today, string timeZone,
        Guid? clinicId, Guid? organizationId, CancellationToken ct)
    {
        // El mismo ámbito del directorio se aplica ANTES de todos los conteos.
        var patients = await scope.FilterAsync(ContextPatients(clinicId, organizationId), p => p.Id, "Medications.View", ct);
        var ids = patients.Select(p => p.Id);
        var total = await patients.CountAsync(ct);
        var enabled = await MedicationAccessQuery.Resolve(db)
            .CountAsync(a => ids.Contains(a.PatientId) && a.Enabled, ct);
        var treatments = db.MedicationTreatments.AsNoTracking().Where(t => ids.Contains(t.PatientId) && t.Status == "active");
        var active = await treatments.CountAsync(ct);
        var inTreatment = await treatments.Select(t => t.PatientId).Distinct().CountAsync(ct);
        var zone = MedicationControlService.Zone(timeZone);
        var from = today.AddDays(-6);
        var start = Bounds(from, zone).Start;
        var end = Bounds(today, zone).End;
        // Dosis distintas en su último estado: una corrección no duplica actividad ni inventa adherencia.
        var records = await db.MedicationDoses.AsNoTracking()
            .Where(d => ids.Contains(d.PatientId) && (d.Status == "taken" || d.Status == "omitted")
                && d.OccurredAt >= start && d.OccurredAt < end)
            .Select(d => new { d.OccurredAt, d.Status }).ToListAsync(ct);
        var counts = records.GroupBy(d => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(d.OccurredAt!.Value, zone)))
            .ToDictionary(g => g.Key, g => (Taken: g.Count(d => d.Status == "taken"), Omitted: g.Count(d => d.Status == "omitted")));
        var days = Enumerable.Range(0, 7).Select(i =>
        {
            var date = from.AddDays(i); var value = counts.GetValueOrDefault(date);
            return new MedicationActivityDayDto(date, value.Taken, value.Omitted);
        }).ToArray();
        return new(total, enabled, inTreatment, active, days[^1].Taken, from, today, days);
    }

    public async Task<MedicationPatientPage> ListPatientsAsync(string? query, int page, int pageSize,
        Guid? clinicId, Guid? organizationId, CancellationToken ct)
    {
        var patients = await scope.FilterAsync(ContextPatients(clinicId, organizationId), p => p.Id, "Medications.View", ct);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var pattern = "%" + query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            patients = patients.Where(p => EF.Functions.ILike(p.FirstName + " " + p.LastName, pattern)
                || EF.Functions.ILike(p.MedicalRecordNumber ?? "", pattern));
        }
        var total = await patients.CountAsync(ct);
        var access = MedicationAccessQuery.Resolve(db);
        var items = await patients.OrderBy(p => p.FirstName).ThenBy(p => p.LastName).ThenBy(p => p.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(p => new MedicationPatientRow(p.Id, p.FirstName + " " + p.LastName, p.MedicalRecordNumber ?? "",
                access.Any(a => a.PatientId == p.Id && a.Enabled),
                db.MedicationTreatments.Count(t => t.PatientId == p.Id && t.Status == "active")))
            .ToListAsync(ct);
        return new(items, total, page, pageSize);
    }

    public Task<MedicationAccessDto> SetAccessAsync(Guid patientId, Guid actorId, ChangeMedicationAccessRequest request, CancellationToken ct)
        => InPatientTransactionAsync(patientId, async () =>
        {
            var row = await db.PatientModuleAccess.SingleOrDefaultAsync(a => a.PatientId == patientId && a.ModuleCode == "medications", ct);
            if (request.ExpectedRevision is { } expected && expected != (row?.Revision ?? 0))
                throw Conflict("La disponibilidad cambió. Actualiza la ficha.");
            if (row is not null && row.Enabled == request.Enabled) return new MedicationAccessDto(row.Enabled, row.Revision);
            if (row is null)
            {
                row = new PatientModuleAccess { Id = Guid.NewGuid(), PatientId = patientId };
                db.PatientModuleAccess.Add(row);
            }
            row.Enabled = request.Enabled;
            row.Revision++;
            row.UpdatedBy = actorId;
            row.UpdatedAt = Now;
            AddMedicationAudit(patientId, actorId, "staff_recorded", "access_changed", "access", row.Id);
            await db.SaveChangesAsync(ct);
            return new MedicationAccessDto(row.Enabled, row.Revision);
        }, ct);

    public Task<MedicationCreateResult> CreateAsync(Guid patientId, Guid actorId, string origin,
        CreateMedicationTreatmentRequest request, CancellationToken ct)
        => InPatientTransactionAsync(patientId, async () =>
        {
            await EnsurePatientOriginAsync(patientId, origin, ct);
            var hash = Hash(new { actorId, origin, request });
            var existing = await db.MedicationTreatments.AsNoTracking()
                .SingleOrDefaultAsync(t => t.PatientId == patientId && t.ClientRequestId == request.ClientRequestId, ct);
            if (existing is not null)
            {
                if (existing.RequestHash != hash) throw Conflict("La operación ya fue utilizada con otros datos.");
                return new MedicationCreateResult(await TreatmentDtoAsync(existing, ct), false);
            }
            var name = request.Name;
            if (request.MedicationId is { } catalogId)
                name = await db.Medications.Where(m => m.Id == catalogId).Select(m => m.Name).SingleOrDefaultAsync(ct)
                    ?? throw new UnprocessableEntityException("El medicamento seleccionado no existe.");
            var treatment = new MedicationTreatment
            {
                Id = Guid.NewGuid(), PatientId = patientId, MedicationId = request.MedicationId,
                Name = name, ProductType = request.ProductType, Origin = origin, CreatedBy = actorId,
                ClientRequestId = request.ClientRequestId, RequestHash = hash, CreatedAt = Now, UpdatedAt = Now,
            };
            var regimen = new MedicationRegimen
            {
                Id = Guid.NewGuid(), TreatmentId = treatment.Id, Dosage = request.Dosage, Unit = request.Unit,
                Route = request.Route ?? "", Instructions = request.Instructions ?? "", TimeZone = request.TimeZone,
                StartDate = request.StartDate, EndDate = request.EndDate, CreatedAt = Now,
            };
            db.MedicationTreatments.Add(treatment);
            db.MedicationRegimens.Add(regimen);
            db.MedicationScheduleSlots.AddRange(request.Times!.Select(t => new MedicationScheduleSlot
            { Id = Guid.NewGuid(), RegimenId = regimen.Id, LocalTime = TimeOnly.ParseExact(t, "HH:mm", CultureInfo.InvariantCulture) }));
            var zone = MedicationControlService.Zone(request.TimeZone);
            var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);
            db.MedicationTreatmentStatusEvents.Add(new MedicationTreatmentStatusEvent
            {
                Id = Guid.NewGuid(), TreatmentId = treatment.Id, Status = "active", Revision = 1,
                EffectiveAt = Bounds(date, zone).Start,
            });
            AddMedicationAudit(patientId, actorId, origin, "treatment_created", "treatment", treatment.Id);
            await db.SaveChangesAsync(ct);
            await RefreshAlertsCoreAsync(patientId, ct);
            return new MedicationCreateResult(await TreatmentDtoAsync(treatment, ct), true);
        }, ct);

    public Task<MedicationWorkspaceDto> WorkspaceAsync(Guid patientId, DateOnly date, string timeZone, bool selfService, CancellationToken ct)
        => InPatientTransactionAsync(patientId, async () =>
        {
            if (selfService) await EnsurePatientOriginAsync(patientId, "patient_reported", ct);
            var patient = await db.PatientProfiles.AsNoTracking().Where(p => p.Id == patientId && p.DeletedAt == null)
                .Select(p => new MedicationPatientDto(p.Id, p.FirstName + " " + p.LastName, p.MedicalRecordNumber ?? ""))
                .SingleOrDefaultAsync(ct) ?? throw Missing();
            var zone = MedicationControlService.Zone(timeZone);
            var treatments = await TreatmentDtosAsync(patientId, ct);
            var occurrences = await ProjectDosesAsync(patientId, date, zone, ct);
            var doses = occurrences.Select(x => x.Dto).ToArray();
            var history = await (from e in db.MedicationIntakeEvents.AsNoTracking()
                join t in db.MedicationTreatments.AsNoTracking() on e.TreatmentId equals t.Id
                where e.PatientId == patientId
                orderby e.RecordedAt descending, e.Id descending
                select new MedicationEventDto(e.Id, e.DoseId, e.TreatmentId, t.Name, e.Status,
                    e.OccurredAt, e.RecordedAt, e.Reason, e.Origin)).Take(100).ToListAsync(ct);
            var access = await GetAccessAsync(patientId, ct);
            // Solo las tomas asignadas y ya programadas para este instante forman la adherencia.
            // Futuras, canceladas, adicionales y nutracéuticos no inflan el denominador.
            var required = treatments.Where(t => t.IsRequired).Select(t => t.Id).ToHashSet();
            var assigned = doses.Where(d => required.Contains(d.TreatmentId) && d.Status != "cancelled").ToArray();
            var due = assigned.Where(d => d.ScheduledAt <= Now).ToArray();
            var taken = due.Count(d => d.Status == "taken");
            var offPlan = treatments.Where(t => !t.IsRequired && t.ProductType != "supplement").Select(t => t.Id).ToHashSet();
            var adherence = new MedicationAdherenceDto(assigned.Length, due.Length, taken,
                due.Count(d => d.Status == "omitted"), due.Count(d => d.Status == "pending"),
                due.Length == 0 ? null : (int)Math.Round(100m * taken / due.Length, MidpointRounding.AwayFromZero),
                doses.Count(d => offPlan.Contains(d.TreatmentId) && d.Status == "taken"));
            return new MedicationWorkspaceDto(patient, access, treatments, doses, history,
                new(treatments.Count(t => t.Status == "active"), doses.Count(d => d.Status != "cancelled"),
                    doses.Count(d => d.Status == "taken"), doses.Count(d => d.Status == "pending")), adherence);
        }, ct);

    public Task<MedicationDoseDto> RecordAsync(Guid patientId, Guid actorId, string origin, Guid doseId,
        RecordMedicationDoseRequest request, CancellationToken ct)
        => InPatientTransactionAsync(patientId, async () =>
        {
            await EnsurePatientOriginAsync(patientId, origin, ct);
            var hash = Hash(new { actorId, origin, doseId, request });
            var existing = await db.MedicationDoseOperations.AsNoTracking()
                .SingleOrDefaultAsync(e => e.PatientId == patientId && e.ClientRequestId == request.ClientRequestId, ct);
            if (existing is not null)
            {
                if (existing.DoseId != doseId || existing.RequestHash != hash) throw Conflict("La operación ya fue utilizada con otros datos.");
                return await DoseQuery(patientId, doseId).SingleAsync(ct);
            }
            var dose = await db.MedicationDoses.SingleOrDefaultAsync(d => d.Id == doseId && d.PatientId == patientId, ct);
            var scheduled = dose?.ScheduledAt ?? request.ScheduledAt?.UtcDateTime ?? throw Missing();
            var day = DateOnly.FromDateTime(scheduled);
            var occurrence = (await ProjectDosesAsync(patientId, day, TimeZoneInfo.Utc, ct))
                .SingleOrDefault(d => d.Dto.Id == doseId) ?? throw Missing();
            if (occurrence.Dto.Status == "cancelled" || scheduled > Now)
                throw Conflict("Esta dosis todavía no está disponible para registrar.");
            if (dose is null)
            {
                dose = new MedicationDose
                {
                    Id = doseId, PatientId = patientId, TreatmentId = occurrence.Dto.TreatmentId,
                    RegimenId = occurrence.RegimenId, SlotId = occurrence.SlotId, ScheduledAt = scheduled,
                    TimeZone = occurrence.Dto.TimeZone, CreatedAt = Now,
                };
                db.MedicationDoses.Add(dose);
            }
            if (dose.Status is "pending" or "cancelled") dose.Status = occurrence.Dto.Status;
            db.MedicationDoseOperations.Add(new MedicationDoseOperation
            {
                Id = Guid.NewGuid(), PatientId = patientId, DoseId = doseId,
                ClientRequestId = request.ClientRequestId, RequestHash = hash, AcceptedAt = Now,
            });
            if (dose.Status == request.Status)
            {
                await db.SaveChangesAsync(ct);
                return await DoseQuery(patientId, doseId).SingleAsync(ct);
            }
            if (request.ExpectedRevision is { } expected && expected != dose.Revision)
                throw Conflict("La toma cambió. Actualiza el seguimiento.");
            if (dose.Status != "pending" && (request.ExpectedRevision is null || string.IsNullOrWhiteSpace(request.Reason)))
                throw Conflict("Para corregir una toma, actualiza la ficha y escribe el motivo.");
            var occurred = (request.OccurredAt ?? clock.GetUtcNow()).UtcDateTime;
            var regimen = await db.MedicationRegimens.AsNoTracking().SingleAsync(r => r.Id == dose.RegimenId, ct);
            var earliest = Bounds(regimen.StartDate, MedicationControlService.Zone(regimen.TimeZone)).Start;
            if (occurred < earliest) throw new UnprocessableEntityException("La fecha de toma es anterior al tratamiento.");
            var previousEvent = await db.MedicationIntakeEvents.AsNoTracking().Where(e => e.DoseId == doseId)
                .OrderByDescending(e => e.RecordedAt).ThenByDescending(e => e.Id).Select(e => (Guid?)e.Id).FirstOrDefaultAsync(ct);
            var intakeEvent = new MedicationIntakeEvent
            {
                Id = Guid.NewGuid(), PatientId = patientId, TreatmentId = dose.TreatmentId, DoseId = doseId,
                ClientRequestId = request.ClientRequestId, RequestHash = hash, Status = request.Status,
                OccurredAt = occurred, RecordedAt = Now, ActorId = actorId, Origin = origin,
                Reason = request.Reason, CorrectsEventId = previousEvent,
            };
            db.MedicationIntakeEvents.Add(intakeEvent);
            await ApplyIntakeStockAsync(patientId, actorId, origin, dose, intakeEvent, regimen.Dosage, ct);
            AddMedicationAudit(patientId, actorId, origin, previousEvent is null ? "dose_recorded" : "dose_corrected", "dose", dose.Id);
            dose.Status = request.Status;
            dose.OccurredAt = occurred;
            dose.Revision++;
            await db.SaveChangesAsync(ct);
            await RefreshAlertsCoreAsync(patientId, ct);
            return await DoseQuery(patientId, doseId).SingleAsync(ct);
        }, ct);

    public Task<MedicationTreatmentDto> ChangeStatusAsync(Guid patientId, Guid treatmentId,
        ChangeMedicationStatusRequest request, bool selfService, CancellationToken ct)
        => InPatientTransactionAsync(patientId, async () =>
        {
            if (selfService) await EnsurePatientOriginAsync(patientId, "patient_reported", ct);
            var treatment = await db.MedicationTreatments.SingleOrDefaultAsync(t => t.Id == treatmentId && t.PatientId == patientId, ct) ?? throw Missing();
            // El paciente registra tomas/omisiones, pero nunca altera el plan del equipo.
            // Se deniega incluso el no-op para no crear una vía de actualización ambigua.
            if (selfService && treatment.Origin != "patient_reported")
                throw new ForbiddenException("Solo tu equipo puede cambiar un tratamiento asignado.");
            if (treatment.Status == request.Status) return await TreatmentDtoAsync(treatment, ct);
            if (request.ExpectedRevision != treatment.Revision) throw Conflict("El tratamiento cambió. Actualiza la ficha.");
            if (treatment.Status == "completed")
                throw Conflict("Registra un nuevo tratamiento para conservar la vigencia del tratamiento finalizado.");
            treatment.Status = request.Status;
            treatment.Revision++;
            treatment.UpdatedAt = Now;
            db.MedicationTreatmentStatusEvents.Add(new MedicationTreatmentStatusEvent
            {
                Id = Guid.NewGuid(), TreatmentId = treatment.Id, Status = request.Status,
                Revision = treatment.Revision, EffectiveAt = Now,
            });
            AddMedicationAudit(patientId, auditActor?.UserId, selfService ? "patient_reported" : "staff_recorded", "treatment_status_changed", "treatment", treatment.Id);
            await db.SaveChangesAsync(ct);
            await RefreshAlertsCoreAsync(patientId, ct);
            return await TreatmentDtoAsync(treatment, ct);
        }, ct);

    private async Task EnsurePatientOriginAsync(Guid patientId, string origin, CancellationToken ct)
    {
        // La revocación y las escrituras usan el mismo lock, por lo que no se escribe con un grant obsoleto.
        if (origin == "patient_reported" && (!(await GetAccessAsync(patientId, ct)).Enabled || !await IsActivePatientAsync(patientId, ct)))
            throw new ForbiddenException("Control de medicamentos no está habilitado para tu cuenta.");
    }

    private async Task<T> InPatientTransactionAsync<T>(Guid patientId, Func<Task<T>> operation, CancellationToken ct)
    {
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM app.patient_profiles WHERE id = {patientId} AND deleted_at IS NULL FOR UPDATE", ct);
            if (!await db.PatientProfiles.AsNoTracking().AnyAsync(p => p.Id == patientId && p.DeletedAt == null, ct)) throw Missing();
            var result = await operation();
            await transaction.CommitAsync(ct);
            return result;
        });
    }

    private sealed record Occurrence(MedicationDoseDto Dto, Guid RegimenId, Guid SlotId);

    public async Task<MedicationCurrentDayDto> ReadCurrentDayAsync(Guid patientId, DateOnly date,
        TimeZoneInfo viewZone, DateTimeOffset asOf, CancellationToken ct)
    {
        var treatments = (await TreatmentDtosAsync(patientId, ct, activeOnly: true)).Where(t =>
        {
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(asOf, MedicationControlService.Zone(t.TimeZone)).DateTime);
            return t.StartDate <= today && (t.EndDate is null || t.EndDate >= today);
        }).ToArray();
        if (treatments.Length == 0) return new(treatments, []);
        var ids = treatments.Select(t => t.Id).ToArray();
        var occurrences = await ProjectDosesAsync(patientId, date, viewZone, ct, treatmentFilter: ids);
        return new(treatments, occurrences.Select(o => o.Dto).ToArray());
    }

    // Lecturas puras: las ocurrencias programadas se proyectan; solo una confirmación autorizada las persiste.
    private async Task<IReadOnlyList<Occurrence>> ProjectDosesAsync(Guid patientId, DateOnly date,
        TimeZoneInfo viewZone, CancellationToken ct, int days = 1, Guid[]? treatmentFilter = null)
    {
        var start = Bounds(date, viewZone).Start;
        var end = Bounds(date.AddDays(days - 1), viewZone).End;
        var treatments = db.MedicationTreatments.AsNoTracking();
        if (treatmentFilter is not null) treatments = treatments.Where(t => treatmentFilter.Contains(t.Id));
        var regimens = await (from r in db.MedicationRegimens.AsNoTracking()
            join t in treatments on r.TreatmentId equals t.Id
            where t.PatientId == patientId
                && !db.MedicationRegimens.Any(later => later.TreatmentId == t.Id && later.Version > r.Version)
            select new { Regimen = r, Treatment = t }).ToListAsync(ct);
        var regimenIds = regimens.Select(r => r.Regimen.Id).ToArray();
        var treatmentIds = regimens.Select(r => r.Treatment.Id).ToArray();
        var slots = (await db.MedicationScheduleSlots.AsNoTracking().Where(s => regimenIds.Contains(s.RegimenId))
            .ToListAsync(ct)).ToLookup(s => s.RegimenId);
        var statuses = (await db.MedicationTreatmentStatusEvents.AsNoTracking()
            .Where(e => treatmentIds.Contains(e.TreatmentId) && e.EffectiveAt < end)
            .OrderBy(e => e.EffectiveAt).ThenBy(e => e.Revision).ToListAsync(ct)).ToLookup(e => e.TreatmentId);
        var persisted = await db.MedicationDoses.AsNoTracking().Where(d => d.PatientId == patientId
                && treatmentIds.Contains(d.TreatmentId) && d.ScheduledAt >= start && d.ScheduledAt < end)
            .ToDictionaryAsync(d => (d.SlotId, d.ScheduledAt), ct);
        var result = new List<Occurrence>();
        foreach (var entry in regimens)
        {
            var regimen = entry.Regimen;
            var zone = MedicationControlService.Zone(regimen.TimeZone);
            var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(start, zone));
            var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(end.AddTicks(-1), zone));
            var registeredOn = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(entry.Treatment.CreatedAt, zone));
            foreach (var slot in slots[regimen.Id])
            for (var day = first; day <= last; day = day.AddDays(1))
            {
                if (day < regimen.StartDate || day < registeredOn || regimen.EndDate is { } until && day > until) continue;
                var local = day.ToDateTime(slot.LocalTime, DateTimeKind.Unspecified);
                if (zone.IsInvalidTime(local)) continue;
                var scheduled = ToUtc(local, zone);
                if (scheduled < start || scheduled >= end) continue;
                var effective = statuses[entry.Treatment.Id].LastOrDefault(e => e.EffectiveAt <= scheduled)?.Status;
                var projectedStatus = effective == "active" ? "pending" : "cancelled";
                persisted.TryGetValue((slot.Id, scheduled), out var saved);
                // Un evento ya registrado conserva su estado y fecha; el ciclo de vida rige las pendientes.
                var status = saved?.Status is "taken" or "omitted" ? saved.Status : projectedStatus;
                var dto = new MedicationDoseDto(saved?.Id ?? OccurrenceId(slot.Id, scheduled), regimen.TreatmentId,
                    entry.Treatment.Name, regimen.Dosage, regimen.Unit, scheduled, regimen.TimeZone,
                    status, saved?.OccurredAt, saved?.Revision ?? 0);
                result.Add(new(dto, regimen.Id, slot.Id));
            }
        }
        return result.OrderBy(d => d.Dto.ScheduledAt).ThenBy(d => d.Dto.Id).ToArray();
    }

    private static Guid OccurrenceId(Guid slotId, DateTime scheduledAt)
        => new(SHA256.HashData(Encoding.UTF8.GetBytes($"med-dose-v1:{slotId:D}:{scheduledAt.Ticks}"))[..16]);

    private async Task<IReadOnlyList<MedicationTreatmentDto>> TreatmentDtosAsync(Guid patientId, CancellationToken ct,
        bool activeOnly = false)
    {
        var rows = await (from t in db.MedicationTreatments.AsNoTracking()
            join r in db.MedicationRegimens.AsNoTracking() on t.Id equals r.TreatmentId
            where t.PatientId == patientId && (!activeOnly || t.Status == "active")
                && !db.MedicationRegimens.Any(later => later.TreatmentId == t.Id && later.Version > r.Version)
            orderby t.CreatedAt descending, t.Id
            select new { Treatment = t, Regimen = r }).ToListAsync(ct);
        var ids = rows.Select(x => x.Regimen.Id).ToArray();
        var times = (await db.MedicationScheduleSlots.AsNoTracking().Where(s => ids.Contains(s.RegimenId))
            .OrderBy(s => s.LocalTime).ToListAsync(ct)).ToLookup(s => s.RegimenId);
        return rows.Select(row => ToTreatment(row.Treatment, row.Regimen,
            times[row.Regimen.Id].Select(s => s.LocalTime.ToString("HH:mm", CultureInfo.InvariantCulture)).ToArray())).ToArray();
    }

    private async Task<MedicationTreatmentDto> TreatmentDtoAsync(MedicationTreatment treatment, CancellationToken ct)
    {
        var regimen = await db.MedicationRegimens.AsNoTracking().Where(r => r.TreatmentId == treatment.Id).OrderByDescending(r => r.Version).FirstAsync(ct);
        var times = await db.MedicationScheduleSlots.AsNoTracking().Where(s => s.RegimenId == regimen.Id).OrderBy(s => s.LocalTime).Select(s => s.LocalTime).ToListAsync(ct);
        return ToTreatment(treatment, regimen, times.Select(t => t.ToString("HH:mm", CultureInfo.InvariantCulture)).ToArray());
    }

    private static MedicationTreatmentDto ToTreatment(MedicationTreatment t, MedicationRegimen r, IReadOnlyList<string> times)
        => new(t.Id, t.MedicationId, t.Name, t.ProductType, t.Status, t.Origin, t.Revision, t.CreatedAt,
            r.Dosage, r.Unit, r.Route, r.Instructions, times, r.TimeZone, r.StartDate, r.EndDate);

    private IQueryable<MedicationDoseDto> DoseQuery(Guid patientId, Guid? doseId = null, DateTime? start = null, DateTime? end = null)
    {
        var query = db.MedicationDoses.AsNoTracking().Where(d => d.PatientId == patientId);
        if (doseId is { } id) query = query.Where(d => d.Id == id);
        if (start is { } from) query = query.Where(d => d.ScheduledAt >= from);
        if (end is { } until) query = query.Where(d => d.ScheduledAt < until);
        // Filtrar/ordenar entidades antes del constructor del DTO: EF no puede recomponer sus miembros después.
        return from d in query
           join t in db.MedicationTreatments.AsNoTracking() on d.TreatmentId equals t.Id
           join r in db.MedicationRegimens.AsNoTracking() on d.RegimenId equals r.Id
           orderby d.ScheduledAt, d.Id
           select new MedicationDoseDto(d.Id, d.TreatmentId, t.Name, r.Dosage, r.Unit, d.ScheduledAt, d.TimeZone, d.Status, d.OccurredAt, d.Revision);
    }

    private static (DateTime Start, DateTime End) Bounds(DateOnly date, TimeZoneInfo zone)
    {
        DateTime Boundary(DateOnly day)
        {
            var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
            return ToUtc(local, zone);
        }
        return (Boundary(date), Boundary(date.AddDays(1)));
    }

    private static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
        => zone.IsAmbiguousTime(local) ? new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max()).UtcDateTime
            : TimeZoneInfo.ConvertTimeToUtc(local, zone);
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    private static NotFoundException Missing() => new("Recurso no encontrado.");
    private static BusinessRuleViolationException Conflict(string message) => new(message);
}
