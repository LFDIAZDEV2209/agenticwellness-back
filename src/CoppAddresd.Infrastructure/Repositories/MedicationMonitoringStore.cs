using CoppAddresd.Application.Features.Medications;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

public sealed partial class MedicationControlStore
{
    public async Task<MedicationWeekDto> WeekAsync(Guid patientId, DateOnly from, string timeZone, CancellationToken ct)
    {
        var zone = MedicationControlService.Zone(timeZone);
        var patient = await db.PatientProfiles.AsNoTracking().Where(p => p.Id == patientId && p.DeletedAt == null)
            .Select(p => new MedicationPatientDto(p.Id, p.FirstName + " " + p.LastName, p.MedicalRecordNumber ?? "")).SingleOrDefaultAsync(ct) ?? throw Missing();
        var treatments = await TreatmentDtosAsync(patientId, ct);
        var required = treatments.Where(t => t.IsRequired).Select(t => t.Id).ToHashSet();
        var projected = await ProjectDosesAsync(patientId, from, zone, ct, 7);
        var now = Now;
        var doses = projected.Select(x => {
            var d = x.Dto;
            int? delay = d.Status == "taken" && d.OccurredAt is { } at ? (int)Math.Round((at - d.ScheduledAt).TotalMinutes) : null;
            var timing = d.Status == "cancelled" ? "cancelled" : d.Status == "omitted" ? "omitted"
                : d.Status == "taken" ? Math.Abs(delay ?? 0) <= 60 ? "on_time" : "late"
                : d.ScheduledAt.AddMinutes(60) < now ? "unrecorded" : "pending";
            return new MedicationTrackingDoseDto(d, DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(d.ScheduledAt, zone)), required.Contains(d.TreatmentId), timing, delay);
        }).ToArray();
        var days = Enumerable.Range(0, 7).Select(i => {
            var date = from.AddDays(i);
            var scheduled = doses.Where(d => d.LocalDate == date && d.IsRequired && d.Dose.Status != "cancelled").ToArray();
            var due = scheduled.Where(d => d.Dose.ScheduledAt <= now).ToArray();
            var taken = due.Count(d => d.Dose.Status == "taken");
            return new MedicationTrackingDayDto(date, scheduled.Length, due.Length, taken, due.Count(d => d.Dose.Status == "omitted"),
                due.Count(d => d.Dose.Status == "pending"), due.Length == 0 ? null : (int)Math.Round(100m * taken / due.Length));
        }).ToArray();
        return new(patient, from, from.AddDays(6), zone.Id, treatments, doses, days);
    }

    public async Task<MedicationPage<MedicationSupplyDto>> InventoryAsync(Guid? patientId, string? query, int page, int pageSize,
        Guid? clinicId, Guid? organizationId, bool selfService, CancellationToken ct)
    {
        var patients = ContextPatients(clinicId, organizationId);
        if (patientId is { } patient) patients = patients.Where(p => p.Id == patient);
        if (!selfService) patients = await scope.FilterAsync(patients, p => p.Id, "Medications.View", ct);
        var rows = from t in db.MedicationTreatments.AsNoTracking()
            join p in patients on t.PatientId equals p.Id
            join r in db.MedicationRegimens.AsNoTracking() on t.Id equals r.TreatmentId
            join s in db.MedicationSupplies.AsNoTracking() on t.Id equals s.TreatmentId into supplies
            from s in supplies.DefaultIfEmpty()
            where !db.MedicationRegimens.Any(later => later.TreatmentId == t.Id && later.Version > r.Version)
            select new { Treatment = t, Patient = p, Regimen = r, Supply = s,
                Slots = db.MedicationScheduleSlots.Count(slot => slot.RegimenId == r.Id),
                DaysPerWeek = r.WeekdayMask == null ? 7
                    : ((r.WeekdayMask.Value & 1) == 0 ? 0 : 1) + ((r.WeekdayMask.Value & 2) == 0 ? 0 : 1)
                    + ((r.WeekdayMask.Value & 4) == 0 ? 0 : 1) + ((r.WeekdayMask.Value & 8) == 0 ? 0 : 1)
                    + ((r.WeekdayMask.Value & 16) == 0 ? 0 : 1) + ((r.WeekdayMask.Value & 32) == 0 ? 0 : 1)
                    + ((r.WeekdayMask.Value & 64) == 0 ? 0 : 1) };
        if (!string.IsNullOrWhiteSpace(query)) {
            var pattern = "%" + query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            rows = rows.Where(x => EF.Functions.ILike(x.Treatment.Name, pattern) || EF.Functions.ILike(x.Patient.FirstName + " " + x.Patient.LastName, pattern));
        }
        var total = await rows.CountAsync(ct);
        var values = await rows.OrderBy(x => x.Supply == null ? decimal.MaxValue : x.Treatment.Status != "active" || x.Slots == 0 ? decimal.MaxValue : x.Supply.Balance / (x.Regimen.Dosage * (x.Regimen.IntervalHours != null
                ? 24m / x.Regimen.IntervalHours.Value : x.Slots * x.DaysPerWeek / 7m)))
            .ThenBy(x => x.Patient.FirstName).ThenBy(x => x.Treatment.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var items = values.Select(x => {
            var zone = MedicationControlService.Zone(x.Regimen.TimeZone);
            var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);
            var active = x.Treatment.Status == "active" && date >= x.Regimen.StartDate && (x.Regimen.EndDate is null || date <= x.Regimen.EndDate);
            var daily = active ? x.Regimen.Dosage * TimesPerDay(x.Regimen.IntervalHours, x.Regimen.WeekdayMask, x.Slots) : 0;
            int? days = x.Supply is not null && daily > 0 ? (int)Math.Min(int.MaxValue, decimal.Floor(Math.Max(0, x.Supply.Balance) / daily)) : null;
            return new MedicationSupplyDto(x.Supply?.Id ?? Guid.Empty, x.Patient.Id, x.Patient.FirstName + " " + x.Patient.LastName,
                x.Treatment.Id, x.Treatment.Name, x.Regimen.Unit, x.Supply?.Balance ?? 0, daily, days, x.Supply?.ExpiresOn, x.Supply?.Revision ?? 0, x.Supply is not null);
        }).ToArray();
        return new(items, total, page, pageSize);
    }

    public async Task<IReadOnlyList<MedicationMovementDto>> MovementsAsync(Guid patientId, Guid treatmentId, CancellationToken ct)
        => await (from m in db.MedicationStockMovements.AsNoTracking()
            join s in db.MedicationSupplies.AsNoTracking() on m.SupplyId equals s.Id
            where m.PatientId == patientId && s.TreatmentId == treatmentId
            orderby m.RecordedAt descending, m.Id descending
            select new MedicationMovementDto(m.Id, m.Kind, m.QuantityDelta, m.BalanceAfter, m.Reason, m.RecordedAt, m.Origin)).Take(100).ToListAsync(ct);

    public Task<MedicationSupplyDto> StockAsync(Guid patientId, Guid actorId, string origin, Guid treatmentId, MedicationStockRequest request, CancellationToken ct)
        => InPatientTransactionAsync(patientId, async () => {
            await EnsurePatientOriginAsync(patientId, origin, ct);
            var treatment = await db.MedicationTreatments.AsNoTracking().SingleOrDefaultAsync(t => t.PatientId == patientId && t.Id == treatmentId, ct) ?? throw Missing();
            var replay = await MonitoringReplayAsync(patientId, actorId, "stock", request.ClientRequestId, new { origin, treatmentId, request }, ct);
            if (replay is not null) return await SupplyDtoAsync(patientId, treatmentId, ct);
            var supply = await db.MedicationSupplies.SingleOrDefaultAsync(s => s.PatientId == patientId && s.TreatmentId == treatmentId, ct);
            if (request.ExpectedRevision != (supply?.Revision ?? 0)) throw Conflict("Las existencias cambiaron. Actualiza el inventario.");
            if (request.Kind == "initial" && supply is not null || request.Kind != "initial" && supply is null) throw Conflict("Revisa el tipo de movimiento del inventario.");
            if (supply is null) {
                supply = new() { Id = Guid.NewGuid(), PatientId = patientId, TreatmentId = treatment.Id, CreatedAt = Now };
                db.MedicationSupplies.Add(supply);
            } else supply.Revision++;
            var old = supply.Balance;
            supply.Balance = request.Kind == "refill" ? old + request.Quantity : request.Quantity;
            if (request.Kind != "refill" || old <= 0) supply.ExpiresOn = request.ExpiresOn;
            else if (request.ExpiresOn is { } expiry && (supply.ExpiresOn is null || expiry < supply.ExpiresOn)) supply.ExpiresOn = expiry;
            supply.UpdatedAt = Now;
            db.MedicationStockMovements.Add(new() { Id = Guid.NewGuid(), PatientId = patientId, SupplyId = supply.Id,
                Kind = request.Kind, QuantityDelta = supply.Balance - old, BalanceAfter = supply.Balance, SupplyRevision = supply.Revision,
                ActorId = actorId, Origin = origin, Reason = request.Reason, RecordedAt = Now });
            AddMonitoringOperation(patientId, actorId, "stock", request.ClientRequestId, new { origin, treatmentId, request }, supply.Id);
            AddMedicationAudit(patientId, actorId, origin, "stock_" + request.Kind, "inventory", supply.Id);
            await NotifyWorkspaceChangedAsync(patientId, ct);
            await db.SaveChangesAsync(ct); await RefreshAlertsCoreAsync(patientId, ct);
            return await SupplyDtoAsync(patientId, treatmentId, ct);
        }, ct);

    private async Task<MedicationSupplyDto> SupplyDtoAsync(Guid patientId, Guid treatmentId, CancellationToken ct)
    {
        var row = await (from supply in db.MedicationSupplies.AsNoTracking()
            join treatment in db.MedicationTreatments.AsNoTracking() on supply.TreatmentId equals treatment.Id
            join patient in db.PatientProfiles.AsNoTracking() on supply.PatientId equals patient.Id
            join regimen in db.MedicationRegimens.AsNoTracking() on treatment.Id equals regimen.TreatmentId
            where supply.PatientId == patientId && treatment.Id == treatmentId
                && !db.MedicationRegimens.Any(later => later.TreatmentId == treatment.Id && later.Version > regimen.Version)
            select new { Supply = supply, Treatment = treatment, Regimen = regimen, PatientName = patient.FirstName + " " + patient.LastName,
                Slots = db.MedicationScheduleSlots.Count(s => s.RegimenId == regimen.Id) }).SingleOrDefaultAsync(ct) ?? throw Missing();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), MedicationControlService.Zone(row.Regimen.TimeZone)).DateTime);
        var daily = row.Treatment.Status == "active" && today >= row.Regimen.StartDate && (row.Regimen.EndDate is null || today <= row.Regimen.EndDate)
            ? row.Regimen.Dosage * TimesPerDay(row.Regimen.IntervalHours, row.Regimen.WeekdayMask, row.Slots) : 0;
        return new(row.Supply.Id, patientId, row.PatientName, treatmentId, row.Treatment.Name, row.Regimen.Unit, row.Supply.Balance,
            daily, daily > 0 ? (int)Math.Min(int.MaxValue, decimal.Floor(Math.Max(0, row.Supply.Balance) / daily)) : null,
            row.Supply.ExpiresOn, row.Supply.Revision, true);
    }

    private async Task ApplyIntakeStockAsync(Guid patientId, Guid actorId, string origin, MedicationDose dose, MedicationIntakeEvent intake, decimal quantity, CancellationToken ct)
    {
        var supply = await db.MedicationSupplies.SingleOrDefaultAsync(s => s.PatientId == patientId && s.TreatmentId == dose.TreatmentId, ct);
        if (supply is null) return; // No inventar stock ni descontar tomas anteriores al primer conteo.
        // Un conteo físico cierra los consumos anteriores. Una corrección posterior
        // no puede devolver existencias ya incluidas en ese conteo.
        var lastCount = await db.MedicationStockMovements.Where(m => m.SupplyId == supply.Id && (m.Kind == "initial" || m.Kind == "adjustment"))
            .MaxAsync(m => (int?)m.SupplyRevision, ct) ?? 0;
        var previous = await db.MedicationStockMovements.Where(m => m.SupplyId == supply.Id && m.DoseId == dose.Id && m.SupplyRevision > lastCount).SumAsync(m => (decimal?)m.QuantityDelta, ct) ?? 0;
        var delta = intake.Status == "taken" ? -quantity - previous : -previous;
        if (delta == 0) return;
        supply.Balance += delta; supply.Revision++; supply.UpdatedAt = Now;
        db.MedicationStockMovements.Add(new() { Id = Guid.NewGuid(), PatientId = patientId, SupplyId = supply.Id,
            DoseId = dose.Id, IntakeEventId = intake.Id, Kind = delta < 0 ? "consumption" : "correction",
            QuantityDelta = delta, BalanceAfter = supply.Balance, SupplyRevision = supply.Revision, ActorId = actorId, Origin = origin, RecordedAt = Now });
    }

    public Task<IReadOnlyList<MedicationEffectDto>> EffectsAsync(Guid patientId, CancellationToken ct) => ReadEffectsAsync(patientId, null, ct);
    private async Task<IReadOnlyList<MedicationEffectDto>> ReadEffectsAsync(Guid patientId, Guid? reportId, CancellationToken ct)
    {
        var reports = await db.MedicationEffectReports.AsNoTracking().Where(e => e.PatientId == patientId && (reportId == null || e.Id == reportId))
            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).Take(100).ToListAsync(ct);
        var ids = reports.Select(e => e.Id).ToArray();
        var notes = await db.MedicationEffectFollowUps.AsNoTracking().Where(n => ids.Contains(n.ReportId)).OrderBy(n => n.CreatedAt).ThenBy(n => n.Id).ToListAsync(ct);
        var treatmentIds = reports.Where(e => e.TreatmentId != null).Select(e => e.TreatmentId).ToArray();
        var names = await db.MedicationTreatments.AsNoTracking().Where(t => treatmentIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        return reports.Select(e => new MedicationEffectDto(e.Id, e.PatientId, e.TreatmentId, e.TreatmentId is { } tid ? names.GetValueOrDefault(tid) : null,
            e.Symptom, e.Severity, e.Status, e.Notes, e.OccurredAt, e.CreatedAt, e.Revision,
            notes.Where(n => n.ReportId == e.Id).Select(n => new MedicationEffectFollowUpDto(n.Id, n.ActorId, n.Status, n.Note, n.CreatedAt)).ToArray())).ToArray();
    }

    public Task<MedicationEffectDto> ReportEffectAsync(Guid patientId, Guid actorId, string origin, MedicationEffectRequest request, CancellationToken ct)
        => InPatientTransactionAsync(patientId, async () => {
            await EnsurePatientOriginAsync(patientId, origin, ct);
            var replay = await MonitoringReplayAsync(patientId, actorId, "effect", request.ClientRequestId, new { origin, request }, ct);
            if (replay is { } id) return (await ReadEffectsAsync(patientId, id, ct)).Single();
            if (request.TreatmentId is { } tid && !await db.MedicationTreatments.AnyAsync(t => t.Id == tid && t.PatientId == patientId, ct)) throw Missing();
            var effect = new MedicationEffectReport { Id = Guid.NewGuid(), PatientId = patientId, TreatmentId = request.TreatmentId,
                Symptom = request.Symptom, Severity = request.Severity, Notes = request.Notes, ReportedBy = actorId,
                Origin = origin, OccurredAt = (request.OccurredAt ?? clock.GetUtcNow()).UtcDateTime, CreatedAt = Now };
            db.MedicationEffectReports.Add(effect);
            AddMonitoringOperation(patientId, actorId, "effect", request.ClientRequestId, new { origin, request }, effect.Id);
            AddMedicationAudit(patientId, actorId, origin, "effect_reported", "effect", effect.Id);
            await db.SaveChangesAsync(ct); await RefreshAlertsCoreAsync(patientId, ct);
            return (await ReadEffectsAsync(patientId, effect.Id, ct)).Single();
        }, ct);

    public Task<MedicationEffectDto> ReviewEffectAsync(Guid patientId, Guid actorId, Guid reportId, MedicationFollowUpRequest request, CancellationToken ct)
        => InPatientTransactionAsync(patientId, async () => {
            var report = await db.MedicationEffectReports.SingleOrDefaultAsync(e => e.Id == reportId && e.PatientId == patientId, ct) ?? throw Missing();
            var replay = await MonitoringReplayAsync(patientId, actorId, "effect_review", request.ClientRequestId, new { reportId, request }, ct);
            if (replay is not null) return (await ReadEffectsAsync(patientId, reportId, ct)).Single();
            if (request.ExpectedRevision != report.Revision) throw Conflict("El reporte cambió. Actualiza el seguimiento.");
            report.Status = request.Status; report.Revision++;
            db.MedicationEffectFollowUps.Add(new() { Id = Guid.NewGuid(), PatientId = patientId, ReportId = report.Id,
                ActorId = actorId, Status = request.Status, Note = request.Note, CreatedAt = Now });
            AddMonitoringOperation(patientId, actorId, "effect_review", request.ClientRequestId, new { reportId, request }, reportId);
            AddMedicationAudit(patientId, actorId, "staff_recorded", "effect_reviewed", "effect", reportId);
            await db.SaveChangesAsync(ct); await RefreshAlertsCoreAsync(patientId, ct);
            return (await ReadEffectsAsync(patientId, reportId, ct)).Single();
        }, ct);

    public async Task<MedicationPage<MedicationAlertDto>> AlertsAsync(Guid? patientId, string? status, string? kind, int page, int pageSize,
        Guid? clinicId, Guid? organizationId, CancellationToken ct)
    {
        var patients = await scope.FilterAsync(ContextPatients(clinicId, organizationId), p => p.Id, "Medications.View", ct);
        var rows = db.MedicationMonitoringAlerts.AsNoTracking().Where(a => patients.Any(p => p.Id == a.PatientId));
        if (patientId is { } id) rows = rows.Where(a => a.PatientId == id);
        if (!string.IsNullOrEmpty(status)) rows = rows.Where(a => a.Status == status);
        if (!string.IsNullOrEmpty(kind)) rows = rows.Where(a => a.Kind == kind);
        var total = await rows.CountAsync(ct);
        var ids = await rows.OrderBy(a => a.Status == "resolved").ThenByDescending(a => a.Severity == "high")
            .ThenByDescending(a => a.UpdatedAt).ThenBy(a => a.Id).Skip((page - 1) * pageSize).Take(pageSize).Select(a => a.Id).ToListAsync(ct);
        return new(await AlertDtosAsync(ids, ct), total, page, pageSize);
    }
    private async Task<IReadOnlyList<MedicationAlertDto>> AlertDtosAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var rows = await (from a in db.MedicationMonitoringAlerts.AsNoTracking()
            join p in db.PatientProfiles.AsNoTracking() on a.PatientId equals p.Id
            join t in db.MedicationTreatments.AsNoTracking() on a.TreatmentId equals t.Id into names
            from t in names.DefaultIfEmpty() where ids.Contains(a.Id)
            select new { Alert = a, PatientName = p.FirstName + " " + p.LastName, TreatmentName = t == null ? null : t.Name }).ToListAsync(ct);
        var history = await db.MedicationAlertFollowUps.AsNoTracking().Where(h => ids.Contains(h.AlertId)).OrderBy(h => h.CreatedAt).ThenBy(h => h.Id).ToListAsync(ct);
        return rows.OrderBy(x => Array.IndexOf(ids.ToArray(), x.Alert.Id)).Select(x => new MedicationAlertDto(x.Alert.Id, x.Alert.PatientId, x.PatientName,
            x.Alert.TreatmentId, x.TreatmentName, x.Alert.Kind, x.Alert.Severity, x.Alert.Status, x.Alert.ConditionActive,
            x.Alert.AssignedTo, x.Alert.ResolutionNote, x.Alert.Revision, x.Alert.CreatedAt,
            history.Where(h => h.AlertId == x.Alert.Id).Select(h => new MedicationAlertFollowUpDto(h.Id, h.ActorId, h.Status, h.Note, h.CreatedAt)).ToArray())).ToArray();
    }
    public Task<MedicationAlertDto> FollowAlertAsync(Guid patientId, Guid actorId, Guid alertId, MedicationFollowUpRequest request, CancellationToken ct)
        => InPatientTransactionAsync(patientId, async () => {
            var alert = await db.MedicationMonitoringAlerts.SingleOrDefaultAsync(a => a.Id == alertId && a.PatientId == patientId, ct) ?? throw Missing();
            var replay = await MonitoringReplayAsync(patientId, actorId, "alert_review", request.ClientRequestId, new { alertId, request }, ct);
            if (replay is not null) return (await AlertDtosAsync([alertId], ct)).Single();
            if (request.ExpectedRevision != alert.Revision) throw Conflict("La alerta cambió. Actualiza el seguimiento.");
            alert.Status = request.Status; alert.AssignedTo = actorId; alert.Revision++; alert.UpdatedAt = Now;
            if (request.Status == "resolved") alert.ResolutionNote = request.Note;
            db.MedicationAlertFollowUps.Add(new() { Id = Guid.NewGuid(), PatientId = patientId, AlertId = alertId,
                ActorId = actorId, Status = request.Status, Note = request.Note, CreatedAt = Now });
            AddMonitoringOperation(patientId, actorId, "alert_review", request.ClientRequestId, new { alertId, request }, alertId);
            AddMedicationAudit(patientId, actorId, "staff_recorded", "alert_reviewed", "alert", alertId);
            await db.SaveChangesAsync(ct); return (await AlertDtosAsync([alertId], ct)).Single();
        }, ct);

    public async Task<MedicationPage<MedicationAuditDto>> AuditAsync(MedicationAuditFilter filter, int page, int pageSize,
        Guid? clinicId, Guid? organizationId, CancellationToken ct)
    {
        var patients = await scope.FilterAsync(ContextPatients(clinicId, organizationId), p => p.Id, "Medications.View", ct);
        var rows = from a in db.MedicationAuditEntries.AsNoTracking() join p in patients on a.PatientId equals p.Id select new { Entry = a, Name = p.FirstName + " " + p.LastName };
        if (filter.PatientId is { } patient) rows = rows.Where(x => x.Entry.PatientId == patient);
        if (!string.IsNullOrEmpty(filter.Action)) rows = rows.Where(x => x.Entry.Action == filter.Action);
        if (filter.ActorId is { } actorId) rows = rows.Where(x => x.Entry.ActorId == actorId);
        if (filter.From is { } from) rows = rows.Where(x => x.Entry.CreatedAt >= from.UtcDateTime);
        if (filter.To is { } to) rows = rows.Where(x => x.Entry.CreatedAt <= to.UtcDateTime);
        var total = await rows.CountAsync(ct);
        var items = await rows.OrderByDescending(x => x.Entry.CreatedAt).ThenByDescending(x => x.Entry.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new MedicationAuditDto(x.Entry.Id, x.Entry.PatientId, x.Name, x.Entry.ActorId, x.Entry.Origin, x.Entry.Action,
                x.Entry.Resource, x.Entry.ResourceId, x.Entry.Purpose, x.Entry.CreatedAt, x.Entry.ActorEmail, x.Entry.RequestId)).ToListAsync(ct);
        return new(items, total, page, pageSize);
    }
    public async Task AuditAccessAsync(IEnumerable<Guid> patientIds, Guid actorId, string origin, string action, string resource, string? purpose, CancellationToken ct)
    {
        foreach (var id in patientIds.Distinct()) AddMedicationAudit(id, actorId, origin, action, resource, null, purpose);
        await db.SaveChangesAsync(ct);
    }
    private void AddMedicationAudit(Guid patientId, Guid? actorId, string origin, string action, string resource, Guid? resourceId, string? purpose = null)
        => db.MedicationAuditEntries.Add(new() { Id = Guid.NewGuid(), PatientId = patientId, ActorId = actorId, Origin = origin,
            Action = action, Resource = resource, ResourceId = resourceId, Purpose = purpose, CreatedAt = Now,
            ActorEmail = actorId is not null ? auditContext?.UserEmail : null, RequestId = auditContext?.RequestId });
    private async Task<Guid?> MonitoringReplayAsync<T>(Guid patientId, Guid actorId, string kind, Guid operation, T body, CancellationToken ct)
    {
        var row = await db.MedicationMonitoringOperations.AsNoTracking().SingleOrDefaultAsync(o => o.PatientId == patientId && o.ClientRequestId == operation, ct);
        if (row is null) return null;
        if (row.Kind != kind || row.RequestHash != Hash(new { actorId, body })) throw Conflict("La operación ya fue utilizada con otros datos.");
        return row.ResultId;
    }
    private void AddMonitoringOperation<T>(Guid patientId, Guid actorId, string kind, Guid operation, T body, Guid resultId)
        => db.MedicationMonitoringOperations.Add(new() { Id = Guid.NewGuid(), PatientId = patientId, ClientRequestId = operation,
            Kind = kind, RequestHash = Hash(new { actorId, body }), ResultId = resultId, CreatedAt = Now });
}
