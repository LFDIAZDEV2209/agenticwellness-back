using CoppAddresd.Application.Features.Wellness.Year365;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

public sealed class Year365ReportStore(AppDbContext db) : IYear365ReportStore
{
    public Task<Year365Program?> FindProgramAsync(Guid patientId, CancellationToken ct) =>
        (from person in db.Motor365People.AsNoTracking()
         join patient in db.PatientProfiles.AsNoTracking() on person.PatientId equals patient.Id
         where person.PatientId == patientId
         select new Year365Program(person.StartDate, patient.Timezone, person.CalendarTimeZoneId,
             person.CalendarAnchorDate, person.CalendarAnchorDay)).SingleOrDefaultAsync(ct);

    public async Task<IReadOnlyList<Year365DailyLog>> ListDailyLogsAsync(Guid patientId, CancellationToken ct) =>
        await db.Motor365DailyLogs.AsNoTracking()
            .Where(log => log.PersonId == patientId)
            .Select(log => new Year365DailyLog(log.Day, log.LogDate, log.Adherence, log.WeightKg, log.WaistCm))
            .ToArrayAsync(ct);

    public async Task<IReadOnlyList<Year365StoredEvent>> ListEventsAsync(
        Guid patientId, int firstDay, int lastDay, CancellationToken ct)
    {
        var events = db.Motor365EngineEvents.AsNoTracking()
            .Where(item => item.PersonId == patientId && item.Day >= firstDay && item.Day <= lastDay);

        // Cada revisión republica decisiones anteriores. Mostrar la última versión del evento fechado.
        return await events.Where(item => item.Id == events
                .Where(revision => revision.Day == item.Day && revision.Kind == item.Kind)
                .OrderByDescending(revision => revision.PlanVersion).ThenByDescending(revision => revision.ParamsVersion)
                .ThenByDescending(revision => revision.OccurredAt).ThenByDescending(revision => revision.Id)
                .Select(revision => revision.Id)
                .First())
            .OrderBy(item => item.Day).ThenBy(item => item.OccurredAt).ThenBy(item => item.Id)
            .Select(item => new Year365StoredEvent(item.Id, item.Day, item.Kind))
            .ToArrayAsync(ct);
    }

    public async Task<Year365PauseCounts> CountPauseDaysAsync(
        Guid patientId, DateOnly firstDate, DateOnly lastDate, CancellationToken ct) =>
        await db.Motor365PauseDays.AsNoTracking()
            .Where(item => item.PersonId == patientId && item.Date >= firstDate && item.Date <= lastDate)
            .GroupBy(_ => 1)
            .Select(days => new Year365PauseCounts(days.Count(day => day.Scheduled), days.Count(day => day.SafetyExercise)))
            .SingleOrDefaultAsync(ct) ?? new(null, null);
}
