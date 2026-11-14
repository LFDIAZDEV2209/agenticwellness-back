using CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Domain.Exceptions;

namespace CoppAddresd.UnitTests.Media;

public sealed class BulkPatientRoutineTests
{
    [Fact]
    public void UltimaSemana_SoloSeisDiasIndividuales_Fin83_SinExigirPlantillaActiva()
    {
        var start = new DateOnly(2030, 1, 7); var patient = Guid.NewGuid(); var routine = new ExerciseRoutine { Id = Guid.NewGuid(), Status = NutritionPlanStatus.Active };
        var template = new ProgramTemplate { TotalWeeks = 12, Status = TemplateStatus.Archived,
            DayTemplates = Enumerable.Range(1, 7).Select(d => new WeeklyDayTemplate { Weekday = (short)d, TaskCode = TaskCode.ejercicio, Points = 10 }).ToList() };
        var enrollment = new ProgramEnrollment { Id = Guid.NewGuid(), PatientId = patient, StartLocalDate = start, Template = template, Timezone = "America/Bogota" };
        var now = new DateTimeOffset(2029, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var plan = BulkPatientRoutinePlanner.Build(new([patient], routine.Id, 12, 12), routine, [patient], [enrollment], [], Guid.NewGuid(), now, default);
        var update = Assert.Single(plan.Updates); Assert.Equal(start.AddDays(82), update.Week.WeekEndDateLocal);
        var rows = WeekSnapshotJson.Parse(update.Snapshot); Assert.Equal(6, rows.Count);
        Assert.All(rows, row => { Assert.Equal("individual", row.Origin); Assert.Equal(routine.Id, row.RoutineId); Assert.False(row.Rest); });
        routine.Status = NutritionPlanStatus.Draft;
        Assert.Throws<UnprocessableEntityException>(() => BulkPatientRoutinePlanner.Build(new([patient], routine.Id, 12, 12), routine, [patient], [enrollment], [], Guid.NewGuid(), now, default));
    }

    [Fact]
    public void Validacion_RechazaRangoInvertidoYListaVacia()
    {
        var validator = new BulkAssignPatientRoutinesValidator();
        Assert.False(validator.Validate(new BulkAssignPatientRoutinesCommand([], Guid.NewGuid(), 1, 1)).IsValid);
        Assert.False(validator.Validate(new BulkAssignPatientRoutinesCommand([Guid.NewGuid()], Guid.NewGuid(), 4, 3)).IsValid);
    }
}
