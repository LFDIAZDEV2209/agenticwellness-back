using CoppAddresd.Application.DTOs.ProgramProgress;
using CoppAddresd.Application.Features.ProgramProgress;
using CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent;
using CoppAddresd.Application.Features.ProgramProgress.Commands.ReplaceEnrollmentWeekTasks;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    [NutritionPostgresFact]
    public async Task Descanso_InterruptorRechazaSinEscribir_YEncendidoPersisteDescanso()
    {
        await using var db = Context(); var seed = await EffectiveSeedAsync(db);
        var week = await db.ProgramWeeks.SingleAsync(w => w.EnrollmentId == seed.Enrollment && w.WeekNumber == 2);
        var before = week.TasksSnapshot.GetRawText();
        var command = new ReplaceEnrollmentWeekTasksCommand(seed.Enrollment, 2,
            [new WeeklyDayTemplateRequest(1, TaskCode.ejercicio, 10, Rest: true)], Guid.NewGuid());
        var disabled = new ReplaceEnrollmentWeekTasksCommandHandler(EffectiveRepository(db), NullLogger<ReplaceEnrollmentWeekTasksCommandHandler>.Instance);
        await Assert.ThrowsAsync<UnprocessableEntityException>(() => disabled.Handle(command, default));
        Assert.Equal(before, (await db.ProgramWeeks.AsNoTracking().SingleAsync(w => w.Id == week.Id)).TasksSnapshot.GetRawText());
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [ProgramRestDayPolicy.ConfigurationKey] = "true" }).Build();
        var enabled = new ReplaceEnrollmentWeekTasksCommandHandler(new ProgramRepository(db, config), NullLogger<ReplaceEnrollmentWeekTasksCommandHandler>.Instance);
        await enabled.Handle(command, default);
        var row = Assert.Single(WeekSnapshotJson.Parse((await db.ProgramWeeks.AsNoTracking().SingleAsync(w => w.Id == week.Id)).TasksSnapshot));
        Assert.True(row.Rest); Assert.Equal("individual", row.Origin); Assert.Equal(command.ActorId, row.EditedBy);
    }
}
