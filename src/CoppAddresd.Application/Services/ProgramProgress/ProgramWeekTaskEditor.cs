using CoppAddresd.Application.Features.ProgramProgress;
using CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Exceptions;

namespace CoppAddresd.Application.Services.ProgramProgress;

/// <summary>Edición por celda: conserva las decisiones omitidas y restablece desde la plantilla vigente.</summary>
public static class ProgramWeekTaskEditor
{
    /// <summary>La inscripción determina el calendario; las fechas denormalizadas no prueban que una semana haya empezado.</summary>
    public static DateOnly EditableWeekStart(DateOnly enrollmentStart, ProgramWeek week, DateOnly today, bool hasHistory)
    {
        var start = enrollmentStart.AddDays((week.WeekNumber - 1) * 7);
        if (start < today || week.Status == CoppAddresd.Domain.Enums.ProgramProgress.ProgramWeekStatus.Completed
            || week.ActivatedAt.HasValue || week.CompletedAt.HasValue || hasHistory)
            throw new BusinessRuleViolationException("PROGRAM_WEEK_STARTED: no se puede editar una semana iniciada.");
        return start;
    }

    public static IReadOnlyList<WeekSnapshotTaskRow> Apply(IReadOnlyList<WeekSnapshotTaskRow> template,
        IReadOnlyList<WeekSnapshotTaskRow> snapshot, IReadOnlyList<WeeklyDayTemplate> changes,
        EnrollmentWeekTaskEdits? edits, Guid? actor, DateTimeOffset now)
    {
        var defaults = template.ToDictionary(t => (t.Weekday, t.TaskCode));
        var rows = ProgramWeekSnapshotMerge.Merge(template, snapshot).ToDictionary(t => (t.Weekday, t.TaskCode));
        foreach (var change in changes)
        {
            var key = (change.Weekday, change.TaskCode.ToString());
            if (edits?.ResetCells?.Contains((change.Weekday, change.TaskCode)) == true)
            {
                if (!defaults.TryGetValue(key, out var inherited))
                    throw new UnprocessableEntityException("TEMPLATE_TASK_MISSING: no existe tarea de plantilla para restablecer.");
                rows[key] = inherited with { Origin = "program", EditedBy = actor, EditedAt = now };
                continue;
            }
            rows.TryGetValue(key, out var prior);
            var rest = key.Item2 == "ejercicio" && edits?.RestDays.Contains(change.Weekday) == true;
            rows[key] = new(change.Weekday, key.Item2, change.Points, change.SortOrder,
                rest ? null : change.RoutineId ?? prior?.RoutineId,
                change.NutritionPlanId ?? prior?.NutritionPlanId, change.MediaId ?? prior?.MediaId,
                "individual", rest, actor, now);
        }
        return rows.Values.OrderBy(t => t.Weekday).ThenBy(t => t.SortOrder).ToArray();
    }
}
