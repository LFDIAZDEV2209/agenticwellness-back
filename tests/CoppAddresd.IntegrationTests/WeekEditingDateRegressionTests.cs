using CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    [NutritionPostgresTheory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public async Task SemanaEdicion_FechaDerivadaZonaPaciente_IgnoraFechaDenormalizada(int offset, bool editable)
    {
        await using var db = Context(); var seed = await EffectiveSeedAsync(db);
        // UTC ya es 4 de noviembre; en Bogotá aún es 3. Hoy también es editable si no se activó.
        var clock = new EffectiveClock(new DateTime(2026, 11, 4, 1, 0, 0, DateTimeKind.Utc));
        var today = new DateOnly(2026, 11, 3);
        var enrollment = await db.ProgramEnrollments.SingleAsync(e => e.Id == seed.Enrollment);
        enrollment.StartLocalDate = today.AddDays(offset - 7); enrollment.Timezone = "America/Bogota";
        var week = await db.ProgramWeeks.SingleAsync(w => w.EnrollmentId == seed.Enrollment && w.WeekNumber == 2);
        week.WeekStartDateLocal = offset < 0 ? today.AddYears(5) : today.AddYears(-5);
        week.WeekEndDateLocal = week.WeekStartDateLocal.AddDays(6);
        await db.SaveChangesAsync(); await db.Entry(week).ReloadAsync(); var before = week.TasksSnapshot.GetRawText();
        var repository = new ProgramRepository(db, new ConfigurationBuilder().Build(), clock: clock);
        var edit = () => repository.ReplaceEnrollmentWeekTasksAsync(seed.Enrollment, 2,
            [new() { Weekday = 4, TaskCode = TaskCode.ejercicio, RoutineId = seed.Routine, Points = 77 }]);
        if (editable)
        {
            await edit(); await db.Entry(week).ReloadAsync();
            var row = Assert.Single(WeekSnapshotJson.Parse(week.TasksSnapshot), r => r.Weekday == 4 && r.TaskCode == "ejercicio");
            Assert.Equal(77, row.Points); Assert.Equal(seed.Routine, row.RoutineId); Assert.Equal("individual", row.Origin);
        }
        else
        {
            await Assert.ThrowsAsync<BusinessRuleViolationException>(edit);
            await db.Entry(week).ReloadAsync(); Assert.Equal(before, week.TasksSnapshot.GetRawText());
        }
    }
}
