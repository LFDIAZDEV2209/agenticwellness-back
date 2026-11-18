using CoppAddresd.Application.Features.Medications;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Entities.ProgramProgress;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

public sealed partial class MedicationControlStore : IMedicationReminderScheduler
{
    internal const string AppReminderChannel = "app";
    internal const string ReminderType = "medication_reminder";
    internal const string ReminderTitle = "Recordatorio de medicación";
    internal const string ReminderMessage = "Tienes una toma pendiente en Mis medicamentos.";
    private static readonly TimeSpan ReminderGrace = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ReminderLookback = TimeSpan.FromMinutes(100);
    private IQueryable<MedicationReminderDelivery> ReminderDeliveries => db.Set<MedicationReminderDelivery>();

    internal Task<List<Guid>> ListReminderPatientsAsync(Guid? cursor, CancellationToken ct)
    {
        var active = MedicationAccessQuery.Resolve(db).Where(row => row.Enabled);
        return db.PatientProfiles.AsNoTracking()
            .Where(patient => patient.UserId != null
                && active.Any(row => row.PatientId == patient.Id)
                && db.MedicationTreatments.Any(t => t.PatientId == patient.Id && t.Status == "active")
                && (!cursor.HasValue || patient.Id.CompareTo(cursor.Value) > 0))
            .OrderBy(patient => patient.Id).Select(patient => patient.Id).Take(50).ToListAsync(ct);
    }

    public async Task<MedicationReminderOccurrence?> GetPendingAsync(
        Guid patientId, Guid doseId, DateTime scheduledAt, CancellationToken ct)
    {
        if (!(await GetAccessAsync(patientId, ct)).Enabled) return null;
        var userId = await db.PatientProfiles.AsNoTracking().Where(p => p.Id == patientId)
            .Select(p => p.UserId).SingleOrDefaultAsync(ct);
        if (userId is not { } user) return null;

        var day = DateOnly.FromDateTime(scheduledAt);
        var planned = (await ProjectDosesAsync(patientId, day, TimeZoneInfo.Utc, ct))
            .FirstOrDefault(row => row.Dto.Id == doseId && row.Dto.Status == "pending");
        if (planned is null || planned.Dto.ScheduledAt != scheduledAt) return null;

        return await (from t in db.MedicationTreatments.AsNoTracking()
            join regimen in db.MedicationRegimens.AsNoTracking() on t.Id equals regimen.TreatmentId
            where t.Id == planned.Dto.TreatmentId && t.PatientId == patientId && t.Status == "active"
                && regimen.Id == planned.RegimenId
                && !db.MedicationRegimens.Any(later => later.TreatmentId == t.Id && later.Version > regimen.Version)
                && !db.MedicationTreatmentStatusEvents.Any(status => status.TreatmentId == t.Id
                    && status.Status != "active" && status.EffectiveAt > scheduledAt)
            select new MedicationReminderOccurrence(patientId, user, planned.Dto, regimen.Instructions))
            .SingleOrDefaultAsync(ct);
    }

    public Task<bool> SnoozeAsync(Guid patientId, Guid doseId, DateTime scheduledAt, int minutes,
        CancellationToken ct, string? channel = null)
    {
        var code = channel ?? AppReminderChannel;
        if (minutes is not (15 or 30) || scheduledAt > Now || scheduledAt < Now - ReminderLookback
            || code != AppReminderChannel)
            return Task.FromResult(false);

        return InPatientTransactionAsync(patientId, async () =>
        {
            var occurrence = await GetPendingAsync(patientId, doseId, scheduledAt, ct);
            if (occurrence is null) return false;

            // Entre peticiones concurrentes, solo hay una fase de seguimiento pendiente.
            var existing = await ReminderDeliveries.SingleOrDefaultAsync(
                row => row.PatientId == patientId && row.DoseId == doseId
                    && row.Channel == code && row.Phase == 1, ct);
            if (existing?.ClaimedAt is not null) return false;
            var dueAt = Now.AddMinutes(minutes);
            if (existing is null)
            {
                db.Add(new MedicationReminderDelivery
                {
                    Id = Guid.NewGuid(), PatientId = patientId, DoseId = doseId, Channel = code,
                    Phase = 1, DueAt = dueAt,
                });
            }
            else
            {
                // Una repetición durante el plazo vigente conserva el instante original.
                if (existing.DueAt > Now) return true;
                existing.DueAt = dueAt;
            }
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);
    }

    internal Task<IReadOnlyList<(Guid DeliveryId, MedicationReminderOccurrence Occurrence)>> ClaimRemindersAsync(
        Guid patientId, string channel, TimeSpan followUp, CancellationToken ct)
        => InPatientTransactionAsync<IReadOnlyList<(Guid, MedicationReminderOccurrence)>>(patientId, async () =>
        {
            if (!(await GetAccessAsync(patientId, ct)).Enabled) return [];
            var userId = await db.PatientProfiles.AsNoTracking().Where(p => p.Id == patientId)
                .Select(p => p.UserId).SingleOrDefaultAsync(ct);
            if (userId is not { } user) return [];

            var now = Now;
            var oldest = now - ReminderLookback - TimeSpan.FromMinutes(30) - ReminderGrace;
            var starts = DateOnly.FromDateTime(oldest);
            var ends = DateOnly.FromDateTime(now);
            var projected = await ProjectDosesAsync(patientId, starts, TimeZoneInfo.Utc, ct,
                ends.DayNumber - starts.DayNumber + 1);
            var active = (await db.MedicationTreatments.AsNoTracking()
                .Where(t => t.PatientId == patientId && t.Status == "active")
                .Select(t => t.Id).ToListAsync(ct)).ToHashSet();
            var interruptions = await db.MedicationTreatmentStatusEvents.AsNoTracking()
                .Where(status => active.Contains(status.TreatmentId) && status.Status != "active"
                    && status.EffectiveAt > oldest)
                .GroupBy(status => status.TreatmentId)
                .Select(group => new { TreatmentId = group.Key, Last = group.Max(status => status.EffectiveAt) })
                .ToDictionaryAsync(row => row.TreatmentId, row => row.Last, ct);
            var doses = projected.Select(row => row.Dto)
                .Where(dose => dose.Status == "pending" && dose.ScheduledAt >= oldest
                    && dose.ScheduledAt <= now && active.Contains(dose.TreatmentId)
                    && interruptions.GetValueOrDefault(dose.TreatmentId) <= dose.ScheduledAt).ToArray();
            var doseIds = doses.Select(d => d.Id).ToArray();
            var reserved = await ReminderDeliveries
                .Where(row => row.PatientId == patientId && row.Channel == channel && doseIds.Contains(row.DoseId))
                .ToDictionaryAsync(row => (row.DoseId, row.Phase), ct);

            var claimed = new List<(Guid, MedicationReminderOccurrence)>();
            foreach (var dose in doses)
            {
                var initial = reserved.GetValueOrDefault((dose.Id, 0));
                var later = reserved.GetValueOrDefault((dose.Id, 1));
                // Un posponer explícito reemplaza la hora inicial y la de seguimiento para el canal.
                var phase = later is not null || now >= dose.ScheduledAt + followUp ? 1 : 0;
                var delivery = phase == 0 ? initial : later;
                var dueAt = delivery?.DueAt ?? (phase == 0 ? dose.ScheduledAt : dose.ScheduledAt + followUp);
                if (delivery?.ClaimedAt is not null || dueAt > now || dueAt < now - ReminderGrace) continue;

                if (delivery is null)
                {
                    delivery = new MedicationReminderDelivery
                    {
                        Id = Guid.NewGuid(), PatientId = patientId, DoseId = dose.Id, Channel = channel,
                        Phase = phase, DueAt = dueAt,
                    };
                    db.Add(delivery);
                }
                delivery.ClaimedAt = now;
                if (channel == AppReminderChannel)
                {
                    db.AppNotifications.Add(new AppNotification
                    {
                        Id = delivery.Id, PatientId = patientId, Type = ReminderType, Title = ReminderTitle,
                        Message = ReminderMessage, SentAt = now, Channel = "push",
                    });
                }
                claimed.Add((delivery.Id, new MedicationReminderOccurrence(patientId, user, dose)));
            }
            await db.SaveChangesAsync(ct);
            return claimed;
        }, ct);
}
