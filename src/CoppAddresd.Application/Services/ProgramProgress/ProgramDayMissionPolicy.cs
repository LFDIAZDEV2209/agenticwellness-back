using CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent;

namespace CoppAddresd.Application.Services.ProgramProgress;

/// <summary>Misiones exigibles de un día: descanso elimina ejercicio sin alterar las otras filas.</summary>
public static class ProgramDayMissionPolicy
{
    public static IReadOnlyList<WeekSnapshotTaskRow> ForDay(IReadOnlyList<WeekSnapshotTaskRow> rows, short weekday, bool rest) =>
        rows.Where(t => t.Weekday == weekday && !(rest && t.TaskCode == "ejercicio")).OrderBy(t => t.SortOrder).ToArray();
}
