using CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent;
using CoppAddresd.Domain.Entities.ProgramProgress;

namespace CoppAddresd.Application.Services.ProgramProgress;

/// <summary>Precedencia única de ARQUITECTURA §7.1; descanso explícito es terminal, incluso en snapshots antiguos contradictorios.</summary>
public static class EffectiveRoutineResolver
{
    public static EffectiveRoutine Resolve(EffectiveContentData data, Guid patientId, DateOnly date,
        IReadOnlyList<WeekSnapshotTaskRow> snapshot, IReadOnlyList<WeeklyDayTemplate> template)
    {
        var exercise = snapshot.Where(r => r.TaskCode == "ejercicio").ToArray();
        if (exercise.Any(r => r.Rest == true)) return new(null, true, "rest");
        var personal = exercise.FirstOrDefault(r => r.RoutineId is { } id && data.ActiveRoutines.Contains(id));
        if (personal is not null) return new(personal.RoutineId, false, "snapshot");
        var general = template.FirstOrDefault(r => r.TaskCode.ToString() == "ejercicio" && r.RoutineId is { } id && data.ActiveRoutines.Contains(id));
        if (general is not null) return new(general.RoutineId, false, general.WeekNumber.HasValue ? "template_week" : "template_generic");
        var legacy = data.Legacy.Where(a => a.PatientId == patientId && !a.Nutrition && a.Start <= date
                && (a.End is null || a.End >= date) && data.ActiveRoutines.Contains(a.ContentId))
            .OrderByDescending(a => a.Start).ThenBy(a => a.ContentId).FirstOrDefault();
        return new(legacy?.ContentId, false, legacy is null ? "missing" : "legacy");
    }
}
