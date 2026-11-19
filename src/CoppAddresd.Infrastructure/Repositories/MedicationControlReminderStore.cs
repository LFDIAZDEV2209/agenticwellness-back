using CoppAddresd.Application.Features.Medications;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Entities.ProgramProgress;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

public sealed partial class MedicationControlStore : IMedicationReminderScheduler, IMedicationWhatsAppReplies
{
    internal const string AppReminderChannel = "app";
    internal const string WhatsAppReminderChannel = "whatsapp";
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

        var reminder = await (from t in db.MedicationTreatments.AsNoTracking()
            join regimen in db.MedicationRegimens.AsNoTracking() on t.Id equals regimen.TreatmentId
            where t.Id == planned.Dto.TreatmentId && t.PatientId == patientId && t.Status == "active"
                && regimen.Id == planned.RegimenId
                && !db.MedicationRegimens.Any(later => later.TreatmentId == t.Id && later.Version > regimen.Version)
                && !db.MedicationTreatmentStatusEvents.Any(status => status.TreatmentId == t.Id
                    && status.Status != "active" && status.EffectiveAt > scheduledAt)
            select regimen).SingleOrDefaultAsync(ct);
        return reminder is null ? null : new(patientId, user, planned.Dto, reminder.Instructions,
            ApplyReminderOverrides(await GetReminderPreferencesAsync(patientId, ct), reminder));
    }

    public Task<bool> SnoozeAsync(Guid patientId, Guid doseId, DateTime scheduledAt, int minutes,
        CancellationToken ct, string? channel = null)
    {
        var code = channel ?? AppReminderChannel;
        if (minutes is not (15 or 30) || scheduledAt > Now || scheduledAt < Now - ReminderLookback
            || code is not (AppReminderChannel or WhatsAppReminderChannel))
            return Task.FromResult(false);

        return InPatientTransactionAsync(patientId, async () =>
        {
            var occurrence = await GetPendingAsync(patientId, doseId, scheduledAt, ct);
            if (occurrence is null || !ChannelEnabled(occurrence.Preferences!, code)) return false;

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
                    Phase = 1, DueAt = dueAt, ScheduledAt = scheduledAt, Snoozed = true,
                });
            }
            else
            {
                // Una repetición durante el plazo vigente conserva el instante original.
                if (existing.DueAt > Now) return true;
                existing.DueAt = dueAt;
                existing.Snoozed = true;
            }
            await NotifyWorkspaceChangedAsync(patientId, ct);
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
            var horizon = now.AddMinutes(30);
            var ends = DateOnly.FromDateTime(horizon);
            var projected = await ProjectDosesAsync(patientId, starts, TimeZoneInfo.Utc, ct,
                ends.DayNumber - starts.DayNumber + 1);
            var preferences = await GetReminderPreferencesAsync(patientId, ct);
            if (!ChannelEnabled(preferences, channel)) return [];
            var active = await (from treatment in db.MedicationTreatments.AsNoTracking()
                join regimen in db.MedicationRegimens.AsNoTracking() on treatment.Id equals regimen.TreatmentId
                where treatment.PatientId == patientId && treatment.Status == "active" && regimen.EffectiveUntil == null
                select regimen).ToDictionaryAsync(r => r.Id, ct);
            var treatmentIds = active.Values.Select(r => r.TreatmentId).ToArray();
            var interruptions = await db.MedicationTreatmentStatusEvents.AsNoTracking()
                .Where(status => treatmentIds.Contains(status.TreatmentId) && status.Status != "active"
                    && status.EffectiveAt > oldest)
                .GroupBy(status => status.TreatmentId)
                .Select(group => new { TreatmentId = group.Key, Last = group.Max(status => status.EffectiveAt) })
                .ToDictionaryAsync(row => row.TreatmentId, row => row.Last, ct);
            var doses = projected.Where(row => row.Dto.Status == "pending" && row.Dto.ScheduledAt >= oldest
                && row.Dto.ScheduledAt <= horizon && active.ContainsKey(row.RegimenId)
                && interruptions.GetValueOrDefault(row.Dto.TreatmentId) <= row.Dto.ScheduledAt).ToArray();
            var doseIds = doses.Select(d => d.Dto.Id).ToArray();
            var reserved = await ReminderDeliveries
                .Where(row => row.PatientId == patientId && row.Channel == channel && doseIds.Contains(row.DoseId))
                .ToDictionaryAsync(row => (row.DoseId, row.Phase), ct);

            var claimed = new List<(Guid, MedicationReminderOccurrence)>();
            foreach (var row in doses)
            {
                var dose = row.Dto;
                var regimen = active[row.RegimenId];
                var effective = ApplyReminderOverrides(preferences, regimen);
                if (!ChannelEnabled(effective, channel)) continue;
                var firstAt = dose.ScheduledAt.AddMinutes(-effective.LeadMinutes);
                var followUpAt = dose.ScheduledAt + (effective.FollowUpMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : followUp);
                var initial = reserved.GetValueOrDefault((dose.Id, 0));
                var later = reserved.GetValueOrDefault((dose.Id, 1));
                // Un posponer explícito reemplaza la hora inicial y la de seguimiento para el canal.
                var phase = later is not null || now >= followUpAt ? 1 : 0;
                var delivery = phase == 0 ? initial : later;
                var dueAt = delivery?.DueAt ?? (phase == 0 ? firstAt : followUpAt);
                if (delivery?.ClaimedAt is not null || dueAt > now || dueAt < now - ReminderGrace) continue;

                if (delivery is null)
                {
                    delivery = new MedicationReminderDelivery
                    {
                        Id = Guid.NewGuid(), PatientId = patientId, DoseId = dose.Id, Channel = channel,
                        Phase = phase, DueAt = dueAt, ScheduledAt = dose.ScheduledAt,
                    };
                    db.Add(delivery);
                }
                delivery.ClaimedAt = now;
                if (channel == AppReminderChannel && effective.InApp)
                {
                    db.AppNotifications.Add(new AppNotification
                    {
                        Id = delivery.Id, PatientId = patientId, Type = ReminderType, Title = ReminderTitle,
                        Message = ReminderMessage, SentAt = now, Channel = "push",
                    });
                }
                claimed.Add((delivery.Id, new MedicationReminderOccurrence(patientId, user, dose, regimen.Instructions, effective)));
            }
            await db.SaveChangesAsync(ct);
            return claimed;
        }, ct);
    public async Task<bool> ApplyAsync(Guid patientId, Guid userId, Guid deliveryId, string action, CancellationToken ct)
    {
        var delivery = await ReminderDeliveries.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == deliveryId && row.PatientId == patientId
                && row.Channel == WhatsAppReminderChannel && row.ClaimedAt != null, ct);
        if (delivery?.ScheduledAt is not { } scheduledAt || scheduledAt > Now || scheduledAt < Now.AddHours(-24))
            return false;
        if (action == "snooze")
            return await SnoozeAsync(patientId, delivery.DoseId, scheduledAt, 30, ct, WhatsAppReminderChannel);
        if (action is not ("taken" or "omitted")) return false;
        try
        {
            return await InPatientTransactionAsync(patientId, async () =>
            {
                if (!await db.PatientProfiles.AsNoTracking().AnyAsync(p => p.Id == patientId && p.UserId == userId, ct))
                    return false;
                var operationId = OccurrenceId(delivery.Id, scheduledAt.AddTicks(action == "taken" ? 1 : 2));
                if (await db.MedicationDoseOperations.AsNoTracking()
                    .AnyAsync(o => o.PatientId == patientId && o.ClientRequestId == operationId, ct)) return true;
                var pending = await GetPendingAsync(patientId, delivery.DoseId, scheduledAt, ct);
                if (pending?.Preferences?.WhatsApp != true) return false;
                await RecordWithinPatientTransactionAsync(patientId, userId, "patient_reported", delivery.DoseId,
                    new(operationId, action, ScheduledAt: new(scheduledAt)), ct, WhatsAppReminderChannel);
                return true;
            }, ct);
        }
        catch (CoppAddresd.Domain.Exceptions.BusinessRuleViolationException)
        {
            return false;
        }
    }

    private static MedicationReminderPreferences ApplyReminderOverrides(MedicationReminderPreferences preferences,
        MedicationRegimen regimen) => new(
            preferences.Push && regimen.PushReminder != false,
            preferences.InApp && regimen.InAppReminder != false,
            preferences.WhatsApp && regimen.WhatsAppReminder != false,
            regimen.LeadMinutes ?? preferences.LeadMinutes,
            regimen.FollowUpMinutes ?? preferences.FollowUpMinutes);

    private static bool ChannelEnabled(MedicationReminderPreferences preferences, string channel)
        => channel == AppReminderChannel ? preferences.Push || preferences.InApp : preferences.WhatsApp;
}
