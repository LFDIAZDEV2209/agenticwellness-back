using CoppAddresd.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

public sealed partial class MedicationControlStore
{
    public Task RefreshAlertsAsync(Guid patientId, CancellationToken ct)
        => InPatientTransactionAsync(patientId, async () => { await RefreshAlertsCoreAsync(patientId, ct); return true; }, ct);

    public async Task<IReadOnlyList<Guid>> MonitoredPatientsAsync(Guid? after, int take, CancellationToken ct)
    {
        var enabled = MedicationAccessQuery.Resolve(db).Where(a => a.Enabled).Select(a => a.PatientId);
        var query = db.PatientProfiles.AsNoTracking().Where(p => p.DeletedAt == null && enabled.Contains(p.Id)
            && (db.MedicationTreatments.Any(t => t.PatientId == p.Id) || db.MedicationEffectReports.Any(e => e.PatientId == p.Id)));
        if (after is { } id) query = query.Where(p => p.Id.CompareTo(id) > 0);
        return await query.OrderBy(p => p.Id).Take(take).Select(p => p.Id).ToListAsync(ct);
    }
    private async Task RefreshAlertsCoreAsync(Guid patientId, CancellationToken ct)
    {
        var patient = await db.PatientProfiles.AsNoTracking().Where(p => p.Id == patientId && p.DeletedAt == null)
            .Select(p => new { p.Timezone, p.Status }).SingleOrDefaultAsync(ct);
        if (patient is null) return;
        var active = patient.Status is not ("Inactivo" or "Inactive") && (await GetAccessAsync(patientId, ct)).Enabled;
        var desired = new Dictionary<string, (Guid? TreatmentId, string Kind, string Severity)>();
        if (active)
        {
            TimeZoneInfo zone;
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(patient.Timezone ?? "America/Bogota"); }
            catch (TimeZoneNotFoundException) { zone = TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"); }
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);
            // Rangos completos en consultas por lote: no ejecutar una consulta por dosis o día.
            var treatments = await TreatmentDtosAsync(patientId, ct);
            var required = treatments.Where(t => t.IsRequired).Select(t => t.Id).ToHashSet();
            var supplies = await db.MedicationSupplies.AsNoTracking().Where(s => s.PatientId == patientId).ToListAsync(ct);
            foreach (var t in treatments.Where(t => t.Status == "active" && today >= t.StartDate && (t.EndDate is null || today <= t.EndDate)))
            {
                var supply = supplies.SingleOrDefault(s => s.TreatmentId == t.Id);
                if (supply is null) continue;
                var dailyUsage = t.Dosage * TimesPerDay(t.IntervalHours, t.WeekdayMask, t.Times.Count);
                if (supply.Balance <= dailyUsage * 7)
                    desired["stock:" + t.Id] = (t.Id, "stock_low", supply.Balance <= dailyUsage * 4 ? "high" : "warning");
                if (supply.Balance > 0 && supply.ExpiresOn < today)
                    desired["expiry:" + t.Id] = (t.Id, "expired", "high");
            }
            var week = await ProjectDosesAsync(patientId, today.AddDays(-6), zone, ct, 7);
            var due = week.Select(x => x.Dto).Where(d => required.Contains(d.TreatmentId) && d.Status != "cancelled" && d.ScheduledAt.AddMinutes(60) < Now).ToArray();
            foreach (var d in due.Where(d => d.Status == "pending"))
                desired["dose:" + d.Id] = (d.TreatmentId, "unrecorded", "warning");
            if (due.Length >= 3 && due.Count(d => d.Status == "taken") * 100m / due.Length < 70)
                desired["adherence"] = (null, "low_adherence", "warning");
            var effects = await db.MedicationEffectReports.AsNoTracking().Where(e => e.PatientId == patientId && e.Status != "resolved").ToListAsync(ct);
            foreach (var e in effects) desired["effect:" + e.Id] = (e.TreatmentId, "reported_effect", e.Severity == "severe" ? "high" : "warning");
        }
        var existing = await db.MedicationMonitoringAlerts.Where(a => a.PatientId == patientId).ToListAsync(ct);
        foreach (var pair in desired)
        {
            var row = existing.SingleOrDefault(a => a.DedupKey == pair.Key);
            if (row is null)
            {
                row = new() { Id = Guid.NewGuid(), PatientId = patientId, TreatmentId = pair.Value.TreatmentId,
                    DedupKey = pair.Key, Kind = pair.Value.Kind, Severity = pair.Value.Severity, CreatedAt = Now, UpdatedAt = Now };
                db.MedicationMonitoringAlerts.Add(row);
                AddMedicationAudit(patientId, null, "system", "alert_created", "alert", row.Id);
            }
            else if (!row.ConditionActive)
            {
                row.ConditionActive = true; row.Status = "open"; row.AssignedTo = null; row.ResolutionNote = null;
                row.Severity = pair.Value.Severity; row.Revision++; row.UpdatedAt = Now;
                AddAlertSystemFollowUp(row, "open", "La condición volvió a presentarse.");
            }
            else if (row.Severity != pair.Value.Severity) { row.Severity = pair.Value.Severity; row.Revision++; row.UpdatedAt = Now; }
        }
        foreach (var row in existing.Where(a => a.ConditionActive && !desired.ContainsKey(a.DedupKey)))
        {
            row.ConditionActive = false; row.Revision++; row.UpdatedAt = Now;
            if (row.Status != "resolved") {
                row.Status = "resolved"; row.ResolutionNote = "La condición ya no está presente en el seguimiento.";
                AddAlertSystemFollowUp(row, "resolved", row.ResolutionNote);
            }
        }
        await db.SaveChangesAsync(ct);
    }
    private void AddAlertSystemFollowUp(MedicationMonitoringAlert alert, string status, string note)
    {
        db.MedicationAlertFollowUps.Add(new() { Id = Guid.NewGuid(), PatientId = alert.PatientId, AlertId = alert.Id,
            Status = status, Note = note, CreatedAt = Now });
        AddMedicationAudit(alert.PatientId, null, "system", "alert_" + status, "alert", alert.Id);
    }
}
