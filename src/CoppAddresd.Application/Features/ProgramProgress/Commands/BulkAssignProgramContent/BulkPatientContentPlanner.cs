using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Domain.Exceptions;

namespace CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent;

/// <summary>Política compartida de asignación por paciente y semana; solo cambia la clase solicitada.</summary>
public static class BulkPatientContentPlanner
{
    public static PatientPodcastPlan Build(IReadOnlyList<Guid> requested, Guid contentId, int from, int to, string code,
        IReadOnlyCollection<Guid> visiblePatients, IReadOnlyList<ProgramEnrollment> enrollments,
        IReadOnlyList<ProgramWeek> weeks, Guid actorId, DateTimeOffset now, CancellationToken ct)
    {
        if (visiblePatients.Any(p => !enrollments.Any(e => e.PatientId == p)))
            throw new UnprocessableEntityException("ACTIVE_ENROLLMENT_REQUIRED: todas las personas a tu cargo deben tener inscripción activa.");
        var applied = new HashSet<Guid>(); var started = new HashSet<Guid>(); var updates = new List<PatientPodcastWeekUpdate>();
        var byEnrollment = weeks.ToLookup(w => w.EnrollmentId);
        foreach (var enrollment in enrollments)
        {
            ct.ThrowIfCancellationRequested(); var template = enrollment.Template!;
            if (to > template.TotalWeeks) throw new UnprocessableEntityException("PROGRAM_WEEK_OUT_OF_RANGE: rango fuera del programa de una persona a tu cargo.");
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now.UtcDateTime, TimeZoneInfo.FindSystemTimeZoneById(enrollment.Timezone)));
            var last = enrollment.StartLocalDate.AddDays(EffectiveDayContentResolver.DurationDays(template) - 1);
            var existing = byEnrollment[enrollment.Id].ToDictionary(w => w.WeekNumber);
            for (var number = from; number <= to; number++)
            {
                ct.ThrowIfCancellationRequested(); var start = enrollment.StartLocalDate.AddDays((number - 1) * 7);
                existing.TryGetValue(number, out var old);
                if (start <= today || old is not null && (old.Status != ProgramWeekStatus.Locked || old.ActivatedAt.HasValue || old.CompletedAt.HasValue))
                { started.Add(enrollment.PatientId); continue; }
                applied.Add(enrollment.PatientId);
                var rows = old is null ? [] : WeekSnapshotJson.Parse(old.TasksSnapshot).ToList();
                var weekdays = Enumerable.Range(0, Math.Min(7, last.DayNumber - start.DayNumber + 1))
                    .Select(d => (short)(start.AddDays(d).DayOfWeek == DayOfWeek.Sunday ? 7 : (int)start.AddDays(d).DayOfWeek)).ToArray();
                foreach (var weekday in weekdays)
                {
                    if (rows.Any(r => r.Weekday == weekday && r.TaskCode == code)) continue;
                    var source = template.DayTemplates.Where(d => d.Weekday == weekday && d.TaskCode.ToString() == code
                        && (d.WeekNumber is null || d.WeekNumber == number)).OrderByDescending(d => d.WeekNumber.HasValue).ThenBy(d => d.SortOrder).FirstOrDefault()
                        ?? throw new UnprocessableEntityException(code == "podcast" ? "TEMPLATE_PODCAST_DAY_MISSING" : "TEMPLATE_ROUTINE_DAY_MISSING");
                    rows.Add(new(weekday, code, source.Points, source.SortOrder, source.RoutineId, source.NutritionPlanId, source.MediaId, "program"));
                }
                var assigned = rows.Select(row =>
                {
                    if (row.TaskCode != code || !weekdays.Contains(row.Weekday)) return row;
                    var same = row.Origin == "individual" && (code == "podcast" ? row.MediaId == contentId : row.RoutineId == contentId && row.Rest != true);
                    if (same) return row;
                    return code == "podcast" ? row with { MediaId = contentId, Origin = "individual", EditedBy = actorId, EditedAt = now }
                        : row with { RoutineId = contentId, Rest = false, Origin = "individual", EditedBy = actorId, EditedAt = now };
                }).ToArray();
                if (old is not null && assigned.SequenceEqual(WeekSnapshotJson.Parse(old.TasksSnapshot))) continue;
                var week = old ?? new ProgramWeek { Id = Guid.NewGuid(), EnrollmentId = enrollment.Id, WeekNumber = number,
                    WeekStartDateLocal = start, WeekEndDateLocal = start.AddDays(6) < last ? start.AddDays(6) : last, CreatedAt = now.UtcDateTime };
                updates.Add(new(week, WeekSnapshotJson.Serialize(assigned), old is null));
            }
        }
        return new(new(applied.Count, started.Count, requested.Distinct().Except(visiblePatients).ToArray()), updates);
    }
}
