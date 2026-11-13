using CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Enums.ProgramProgress;

namespace CoppAddresd.Application.Services.ProgramProgress;

public sealed record TemplateImpactDto(int PacientesQueCambian, int ConPersonalizacionesFuturas, int SemanasOmitidasIniciadas);

/// <summary>Simula la recepción de la plantilla sin escribir snapshots ni incrementar su versión.</summary>
public static class TemplateImpactCalculator
{
    public static bool Started(ProgramWeek week, DateOnly today) => week.Status != ProgramWeekStatus.Locked
        || week.ActivatedAt.HasValue || week.CompletedAt.HasValue || week.WeekStartDateLocal <= today;

    public static TemplateImpactDto Calculate(EffectiveContentData data, IEffectiveDayContentResolver resolver, DateTime utcNow, CancellationToken ct)
    {
        var receivers = new HashSet<Guid>(); var personalized = new HashSet<Guid>(); var omitted = 0;
        var weeks = data.Weeks.ToLookup(w => w.EnrollmentId);
        foreach (var enrollment in data.Enrollments)
        {
            ct.ThrowIfCancellationRequested();
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc),
                TimeZoneInfo.FindSystemTimeZoneById(enrollment.Timezone)));
            var duration = EffectiveDayContentResolver.DurationDays(enrollment.Template!);
            var existing = weeks[enrollment.Id].ToDictionary(w => w.WeekNumber);
            var future = new List<ProgramWeek>();
            for (var n = 1; n <= (duration + 6) / 7; n++)
            {
                var start = enrollment.StartLocalDate.AddDays((n - 1) * 7);
                if (start <= today || existing.TryGetValue(n, out var old) && Started(old, today)) { omitted++; continue; }
                var rows = existing.TryGetValue(n, out var stored) ? WeekSnapshotJson.Parse(stored.TasksSnapshot) : [];
                // La activación conserva los orígenes antiguos desconocidos como individuales.
                var weekdays = Enumerable.Range(0, Math.Min(7, duration - (n - 1) * 7))
                    .Select(offset => start.AddDays(offset).DayOfWeek is DayOfWeek.Sunday ? 7 : (int)start.AddDays(offset).DayOfWeek).ToHashSet();
                var personal = rows.Where(r => r.Origin != "program" && weekdays.Contains(r.Weekday)).ToList();
                if (personal.Count > 0) personalized.Add(enrollment.PatientId);
                future.Add(new() { EnrollmentId = enrollment.Id, WeekNumber = n, WeekStartDateLocal = start,
                    WeekEndDateLocal = start.AddDays(6), TasksSnapshot = WeekSnapshotJson.Serialize(personal) });
            }
            if (future.Count == 0) continue;
            var simulated = data with { Weeks = future };
            var days = resolver.ResolveRange(simulated, enrollment, future[0].WeekStartDateLocal, future[^1].WeekEndDateLocal, ct)
                .Where(d => future.Any(w => w.WeekNumber == d.WeekNumber));
            if (days.Any(d => Inherits(d.Podcast.Source) || Inherits(d.Routine.Source) || d.Nutrition.Source == "engine"))
                receivers.Add(enrollment.PatientId);
        }
        return new(receivers.Count, personalized.Count, omitted);
    }

    private static bool Inherits(string source) => source is "template_week" or "template_generic";
}
