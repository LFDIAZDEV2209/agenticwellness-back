using CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent;
using CoppAddresd.Domain.Entities.ProgramProgress;

namespace CoppAddresd.Application.Services.ProgramProgress;

public sealed record ProgramUpcomingDayPlan(int Day, EffectiveDayContent Content, IReadOnlyList<WeekSnapshotTaskRow> Missions);

/// <summary>Mezcla común de activación y vista previa: conserva decisiones humanas por día/tarea.</summary>
public static class ProgramWeekSnapshotMerge
{
    public static IReadOnlyList<WeekSnapshotTaskRow> Merge(IReadOnlyList<WeekSnapshotTaskRow> template, IReadOnlyList<WeekSnapshotTaskRow> snapshot)
    {
        var merged = template.ToDictionary(t => (t.Weekday, t.TaskCode));
        foreach (var item in snapshot.Where(t => t.Origin != "program"))
        {
            var code = item.TaskCode == "nutribiotico" ? "nutraceutico" : item.TaskCode;
            merged[(item.Weekday, code)] = item with { TaskCode = code, Origin = "individual" };
        }
        return merged.Values.OrderBy(t => t.Weekday).ThenBy(t => t.SortOrder).ToArray();
    }
}

/// <summary>Calendario de mañana a hoy+6, limitado al programa y sin ejecutar consultas ni activar semanas.</summary>
public static class ProgramUpcomingPlanner
{
    public static IReadOnlyList<ProgramUpcomingDayPlan> Build(EffectiveContentData data, ProgramEnrollment enrollment,
        IEffectiveDayContentResolver resolver, DateOnly today, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<WeekSnapshotTaskRow> Defaults(int number) => enrollment.Template!.DayTemplates
            .Where(t => t.WeekNumber is null || t.WeekNumber == number).GroupBy(t => (t.Weekday, t.TaskCode))
            .Select(g => g.OrderByDescending(t => t.WeekNumber.HasValue).First())
            .Select(t => new WeekSnapshotTaskRow(t.Weekday, t.TaskCode.ToString(), t.Points, t.SortOrder,
                t.RoutineId, t.NutritionPlanId, t.MediaId, "program")).ToArray();
        var projectedWeeks = data.Weeks.Where(w => w.EnrollmentId == enrollment.Id).Select(w =>
            TemplateImpactCalculator.Started(w, today) ? w : new ProgramWeek { Id = w.Id, EnrollmentId = w.EnrollmentId,
                WeekNumber = w.WeekNumber, WeekStartDateLocal = w.WeekStartDateLocal, WeekEndDateLocal = w.WeekEndDateLocal,
                TasksSnapshot = WeekSnapshotJson.Serialize(ProgramWeekSnapshotMerge.Merge(Defaults(w.WeekNumber), WeekSnapshotJson.Parse(w.TasksSnapshot))) }).ToArray();
        var projected = data with { Weeks = projectedWeeks };
        var snapshots = projectedWeeks.ToDictionary(w => w.WeekNumber,
            w => (Week: w, Rows: WeekSnapshotJson.Parse(w.TasksSnapshot)));
        var result = new List<ProgramUpcomingDayPlan>();
        foreach (var day in resolver.ResolveRange(projected, enrollment, today.AddDays(1), today.AddDays(6), ct))
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<WeekSnapshotTaskRow> scheduled = Defaults(day.WeekNumber);
            if (snapshots.TryGetValue(day.WeekNumber, out var stored))
            {
                if (stored.Rows.Count > 0) scheduled = stored.Rows;
            }
            result.Add(new(day.Date.DayNumber - enrollment.StartLocalDate.DayNumber + 1, day,
                ProgramDayMissionPolicy.ForDay(scheduled, day.Weekday, day.Routine.Rest)));
        }
        return result;
    }
}
