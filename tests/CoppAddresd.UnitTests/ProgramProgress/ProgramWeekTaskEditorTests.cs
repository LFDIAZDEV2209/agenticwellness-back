using CoppAddresd.Application.Features.ProgramProgress;
using CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Enums.ProgramProgress;

namespace CoppAddresd.UnitTests.ProgramProgress;

public sealed class ProgramWeekTaskEditorTests
{
    [Fact]
    public void EditarUnaCelda_ConservaIndividuales_RestableceContraPlantillaActual()
    {
        var original = Guid.NewGuid(); var updated = Guid.NewGuid(); var individual = Guid.NewGuid();
        WeekSnapshotTaskRow[] template = [new(1, "ejercicio", 20, 1, updated, Origin: "program")];
        WeekSnapshotTaskRow[] snapshot = [new(1, "ejercicio", 10, 1, original, Origin: "program"),
            new(2, "ejercicio", 30, 1, individual, Origin: "individual")];
        var result = ProgramWeekTaskEditor.Apply(template, snapshot,
            [new WeeklyDayTemplate { Weekday = 1, TaskCode = TaskCode.ejercicio }],
            new(new HashSet<short>(), new HashSet<(short, TaskCode)> { (1, TaskCode.ejercicio) }), Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.Equal(updated, Assert.Single(result, r => r.Weekday == 1).RoutineId);
        Assert.Equal(snapshot[1], Assert.Single(result, r => r.Weekday == 2));
        Assert.Equal("program", Assert.Single(result, r => r.Weekday == 1).Origin);
    }
}
