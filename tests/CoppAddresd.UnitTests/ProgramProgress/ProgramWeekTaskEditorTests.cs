using CoppAddresd.Application.Features.ProgramProgress;
using CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Domain.Exceptions;

namespace CoppAddresd.UnitTests.ProgramProgress;

public sealed class ProgramWeekTaskEditorTests
{
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void Guardia_UsaInicioDeInscripcion_YPermiteHoyOFuturo(int offset, bool editable)
    {
        var today = new DateOnly(2026, 11, 3);
        var week = new ProgramWeek { WeekNumber = 2, Status = ProgramWeekStatus.Locked,
            WeekStartDateLocal = offset < 0 ? today.AddYears(5) : today.AddYears(-5) };
        var enrollmentStart = today.AddDays(offset - 7);
        if (editable) Assert.Equal(today.AddDays(offset), ProgramWeekTaskEditor.EditableWeekStart(enrollmentStart, week, today, false));
        else Assert.Throws<BusinessRuleViolationException>(() => ProgramWeekTaskEditor.EditableWeekStart(enrollmentStart, week, today, false));
    }

    [Theory]
    [InlineData("activada")]
    [InlineData("finalizada")]
    [InlineData("estado_finalizado")]
    [InlineData("historial")]
    public void Guardia_ActividadRealBloqueaAunqueElCalendarioSeaFuturo(string evidence)
    {
        var today = new DateOnly(2026, 11, 3);
        var week = new ProgramWeek { WeekNumber = 2, Status = evidence == "estado_finalizado" ? ProgramWeekStatus.Completed : ProgramWeekStatus.Locked,
            ActivatedAt = evidence == "activada" ? DateTime.UtcNow : null, CompletedAt = evidence == "finalizada" ? DateTime.UtcNow : null };
        Assert.Throws<BusinessRuleViolationException>(() => ProgramWeekTaskEditor.EditableWeekStart(today, week, today, evidence == "historial"));
    }

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
