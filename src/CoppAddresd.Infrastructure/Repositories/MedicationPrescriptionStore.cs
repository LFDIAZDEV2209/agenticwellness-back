using System.Globalization;
using CoppAddresd.Application.Features.Medications;
using CoppAddresd.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

public sealed partial class MedicationControlStore
{
    public async Task<MedicationAdherenceSummaryDto> AdherenceAsync(Guid patientId, DateOnly today,
        string timeZone, CancellationToken ct)
    {
        var zone = MedicationControlService.Zone(timeZone);
        var assigned = await db.MedicationTreatments.AsNoTracking()
            .Where(t => t.PatientId == patientId && t.Origin == "staff_recorded" && t.ProductType != "supplement")
            .Select(t => t.Id).ToArrayAsync(ct);
        var now = Now;
        var doses = (await ProjectDosesAsync(patientId, today.AddDays(-29), zone, ct, 30, assigned))
            .Where(d => d.Dto.ScheduledAt <= now && d.Dto.Status != "cancelled").Select(d => d.Dto).ToArray();

        MedicationAdherenceWindowDto Window(IEnumerable<MedicationDoseDto> window)
        {
            var rows = window.ToArray();
            var taken = rows.Count(d => d.Status == "taken");
            return new(rows.Length, taken, rows.Length == 0 ? null
                : (int)Math.Round(100m * taken / rows.Length, MidpointRounding.AwayFromZero));
        }

        var weekStarts = Bounds(today.AddDays(-6), zone).Start;
        return new(Window(doses.Where(d => d.ScheduledAt >= weekStarts)), Window(doses));
    }

    public async Task<Guid> VersionAsync(Guid patientId, CancellationToken ct)
        => await db.MedicationPatientStates.AsNoTracking().Where(x => x.Id == patientId)
            .Select(x => x.Version).SingleOrDefaultAsync(ct);

    private Task NotifyWorkspaceChangedAsync(Guid patientId, CancellationToken ct)
        => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO app.medication_patient_state (id, version, push_enabled, in_app_enabled, whats_app_enabled, lead_minutes)
            VALUES ({patientId}, {Guid.NewGuid()}, TRUE, TRUE, FALSE, 0)
            ON CONFLICT (id) DO UPDATE SET version = EXCLUDED.version
            """, ct);

    public async Task<MedicationReminderPreferences> GetReminderPreferencesAsync(Guid patientId, CancellationToken ct)
        => await db.MedicationPatientStates.AsNoTracking().Where(x => x.Id == patientId)
            .Select(x => new MedicationReminderPreferences(x.PushEnabled, x.InAppEnabled, x.WhatsAppEnabled,
                x.LeadMinutes, x.FollowUpMinutes)).SingleOrDefaultAsync(ct)
            ?? new(true, true, false, 0, null);

    public Task<MedicationReminderPreferences> SetReminderPreferencesAsync(Guid patientId, Guid actorId,
        MedicationReminderPreferences request, CancellationToken ct)
        => InPatientTransactionAsync(patientId, async () =>
        {
            await EnsurePatientOriginAsync(patientId, "patient_reported", ct);
            var state = await db.MedicationPatientStates.SingleOrDefaultAsync(x => x.Id == patientId, ct);
            if (state is null)
            {
                state = new MedicationPatientState { Id = patientId };
                db.Add(state);
            }
            state.Version = Guid.NewGuid();
            state.PushEnabled = request.Push;
            state.InAppEnabled = request.InApp;
            state.WhatsAppEnabled = request.WhatsApp;
            state.LeadMinutes = request.LeadMinutes;
            state.FollowUpMinutes = request.FollowUpMinutes;
            AddMedicationAudit(patientId, actorId, "patient_reported", "reminder_preferences_changed", "patient", patientId);
            await db.SaveChangesAsync(ct);
            return request;
        }, ct);

    public Task<MedicationTreatmentDto> UpdateRegimenAsync(Guid patientId, Guid treatmentId, Guid actorId,
        UpdateMedicationRegimenRequest request, CancellationToken ct)
        => InPatientTransactionAsync(patientId, async () =>
        {
            var treatment = await db.MedicationTreatments.SingleOrDefaultAsync(
                t => t.Id == treatmentId && t.PatientId == patientId, ct) ?? throw Missing();
            if (treatment.Origin != "staff_recorded" || treatment.Status == "completed")
                throw Conflict("Solo se pueden editar pautas asignadas y vigentes.");
            if (treatment.Revision != request.ExpectedRevision)
                throw Conflict("El tratamiento cambió. Actualiza la ficha.");

            var effectiveAt = Now;
            var current = await db.MedicationRegimens.Where(x => x.TreatmentId == treatmentId)
                .OrderByDescending(x => x.Version).FirstAsync(ct);
            if (current.Unit != request.Unit && await db.MedicationSupplies.AsNoTracking()
                .AnyAsync(s => s.TreatmentId == treatmentId, ct))
                throw Conflict("La unidad de la pauta debe coincidir con la del inventario registrado.");
            current.EffectiveUntil = effectiveAt;
            var regimen = new MedicationRegimen
            {
                Id = Guid.NewGuid(), TreatmentId = treatmentId, Version = current.Version + 1,
                EffectiveFrom = effectiveAt, CreatedAt = effectiveAt,
                Dosage = request.Dosage, Unit = request.Unit, Route = request.Route ?? "",
                Instructions = request.Instructions ?? "", TimeZone = request.TimeZone,
                StartDate = request.StartDate, EndDate = request.EndDate,
                WeekdayMask = request.WeekdayMask, IntervalHours = request.IntervalHours,
                PushReminder = request.Reminders?.Push, InAppReminder = request.Reminders?.InApp,
                WhatsAppReminder = request.Reminders?.WhatsApp, LeadMinutes = request.Reminders?.LeadMinutes,
                FollowUpMinutes = request.Reminders?.FollowUpMinutes,
            };
            db.Add(regimen);
            db.AddRange(request.Times!.Select(time => new MedicationScheduleSlot
            {
                Id = Guid.NewGuid(), RegimenId = regimen.Id,
                LocalTime = TimeOnly.ParseExact(time, "HH:mm", CultureInfo.InvariantCulture),
            }));
            treatment.Revision++;
            treatment.UpdatedAt = effectiveAt;
            AddMedicationAudit(patientId, actorId, "staff_recorded", "treatment_edited", "treatment", treatmentId);
            await NotifyWorkspaceChangedAsync(patientId, ct);
            await db.SaveChangesAsync(ct);
            await RefreshAlertsCoreAsync(patientId, ct);
            return await TreatmentDtoAsync(treatment, ct);
        }, ct);

    private sealed record MedicationPrescriber(Guid Id, string Name);

    private async Task<IReadOnlyList<MedicationTreatmentDto>> WithPrescribersAsync(Guid patientId,
        IReadOnlyList<MedicationTreatmentDto> treatments, CancellationToken ct)
    {
        var authors = await db.MedicationTreatments.AsNoTracking()
            .Where(t => t.PatientId == patientId && t.Origin == "staff_recorded")
            .ToDictionaryAsync(t => t.Id, t => t.CreatedBy, ct);
        if (authors.Count == 0) return treatments;
        var ids = authors.Values.Distinct().ToArray();
        var names = await db.Database.SqlQuery<MedicationPrescriber>($"""
            SELECT "Id", trim(concat_ws(' ', "FirstName", "LastName")) AS "Name"
            FROM auth."Users" WHERE "Id" = ANY({ids})
            """).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        return treatments.Select(t => authors.TryGetValue(t.Id, out var id)
            ? t with { Prescriber = names.GetValueOrDefault(id) } : t).ToArray();
    }

    private static decimal TimesPerDay(int? intervalHours, int? weekdayMask, int slots)
        => intervalHours is { } hours ? 24m / hours
            : weekdayMask is { } days ? slots * (decimal)System.Numerics.BitOperations.PopCount((uint)days) / 7
            : slots;

    private static IEnumerable<DateTime> ScheduleTimes(MedicationRegimen regimen, MedicationScheduleSlot slot,
        TimeZoneInfo zone, DateOnly first, DateOnly last, DateTime start)
    {
        if (regimen.IntervalHours is { } hours)
        {
            var anchor = regimen.StartDate.ToDateTime(slot.LocalTime);
            while (zone.IsInvalidTime(anchor)) anchor = anchor.AddMinutes(1);
            var origin = ToUtc(anchor, zone);
            var step = TimeSpan.FromHours(hours).Ticks;
            var count = Math.Max(0, (start.Ticks - origin.Ticks + step - 1) / step);
            var limit = Bounds(last, zone).End;
            for (var at = origin.AddTicks(count * step); at < limit; at = at.AddTicks(step))
                yield return at;
            yield break;
        }

        for (var day = first; day <= last; day = day.AddDays(1))
        {
            var bit = 1 << (((int)day.DayOfWeek + 6) % 7);
            if (regimen.WeekdayMask is { } mask && (mask & bit) == 0) continue;
            var local = day.ToDateTime(slot.LocalTime);
            if (!zone.IsInvalidTime(local)) yield return ToUtc(local, zone);
        }
    }
}
