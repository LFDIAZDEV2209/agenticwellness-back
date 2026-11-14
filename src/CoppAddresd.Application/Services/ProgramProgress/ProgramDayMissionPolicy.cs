using CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent;

namespace CoppAddresd.Application.Services.ProgramProgress;

/// <summary>Misiones exigibles de un día: descanso elimina ejercicio sin alterar las otras filas.</summary>
public static class ProgramDayMissionPolicy
{
    public static IReadOnlyList<WeekSnapshotTaskRow> ForDay(IReadOnlyList<WeekSnapshotTaskRow> rows, short weekday, bool rest) =>
        rows.Where(t => t.Weekday == weekday && !(rest && t.TaskCode == "ejercicio")).OrderBy(t => t.SortOrder).ToArray();

    /// <summary>Completar todas las misiones exigibles mantiene la racha aunque su número sea menor al umbral configurado.</summary>
    public static bool QualifiesForStreak(int completed, int scheduled, int minimum) =>
        scheduled > 0 && (completed >= scheduled || completed >= Math.Max(1, minimum));
}
