using CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent;
using CoppAddresd.Application.Features.ProgramProgress.Queries.Erp;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    [NutritionPostgresFact]
    public async Task LegadoFuturo_CopiaIndividual_ConflictosSimulacionIdempotenciaReversa_YHuecosCero()
    {
        await using var db = Context(); var seed = await FutureEditingSeedAsync(db);
        var week = await db.ProgramWeeks.SingleAsync(w => w.EnrollmentId == seed.Enrollment && w.WeekNumber == 2);
        var rest = new WeekSnapshotTaskRow(2, "ejercicio", 10, 0, Origin: "individual", Rest: true);
        var podcast = new WeekSnapshotTaskRow(1, "podcast", 99, 1, MediaId: seed.Podcast, Origin: "individual");
        week.TasksSnapshot = WeekSnapshotJson.Serialize([rest, podcast]);
        var legacy = new ExerciseRoutine { Id = Guid.NewGuid(), Name = "Manual anterior", Status = NutritionPlanStatus.Active };
        var conflict = new ExerciseRoutine { Id = Guid.NewGuid(), Name = "Solapada", Status = NutritionPlanStatus.Active };
        db.ExerciseRoutines.AddRange(legacy, conflict);
        db.RoutineAssignments.AddRange(new RoutineAssignment { Id = Guid.NewGuid(), PatientId = seed.Patient, RoutineId = legacy.Id,
            StartDate = week.WeekStartDateLocal.ToDateTime(TimeOnly.MinValue), EndDate = week.WeekEndDateLocal.ToDateTime(TimeOnly.MinValue), Status = AssignmentStatus.Active },
            new RoutineAssignment { Id = Guid.NewGuid(), PatientId = seed.Patient, RoutineId = conflict.Id,
                StartDate = week.WeekStartDateLocal.AddDays(3).ToDateTime(TimeOnly.MinValue), EndDate = week.WeekStartDateLocal.AddDays(3).ToDateTime(TimeOnly.MinValue), Status = AssignmentStatus.Active });
        db.NutritionPlanAssignments.Add(new() { Id = Guid.NewGuid(), PatientId = seed.Patient, PlanId = seed.Plan,
            StartDate = week.WeekStartDateLocal.ToDateTime(TimeOnly.MinValue), EndDate = week.WeekEndDateLocal.ToDateTime(TimeOnly.MinValue), Status = AssignmentStatus.Active });
        await db.SaveChangesAsync(); await db.Entry(week).ReloadAsync(); var before = week.TasksSnapshot.GetRawText();
        var active = await db.ProgramWeeks.AsNoTracking().SingleAsync(w => w.EnrollmentId == seed.Enrollment && w.WeekNumber == 1);
        var activeBefore = active.TasksSnapshot.GetRawText();
        var simulation = await OriginScriptAsync("simulate", fillLegacy: true);
        Assert.Equal(0, simulation.GetProperty("escrituras").GetInt32());
        Assert.Equal(12, simulation.GetProperty("counts").GetProperty("items_legacy_copiados").GetInt32());
        Assert.Equal(1, simulation.GetProperty("counts").GetProperty("conflicto_asignaciones_solapadas").GetInt32());
        await db.Entry(week).ReloadAsync(); Assert.Equal(before, week.TasksSnapshot.GetRawText());
        Assert.Equal(1, (await OriginScriptAsync("apply", fillLegacy: true)).GetProperty("escrituras").GetInt32());
        await db.Entry(week).ReloadAsync(); var applied = WeekSnapshotJson.Parse(week.TasksSnapshot);
        Assert.Equal(12, applied.Count(r => r.BackfillVersion == 2 && r.Origin == "individual"));
        Assert.Equal(rest, Assert.Single(applied, r => r.Weekday == 2 && r.TaskCode == "ejercicio"));
        Assert.Equal(podcast, Assert.Single(applied, r => r.TaskCode == "podcast"));
        Assert.All(applied.Where(r => r.TaskCode == "ejercicio" && r.Rest != true), row => Assert.Equal(legacy.Id, row.RoutineId));
        Assert.Equal(0, (await OriginScriptAsync("apply", fillLegacy: true)).GetProperty("escrituras").GetInt32());
        var actor = Substitute.For<IPatientScopeActor>(); actor.UserId.Returns(Guid.NewGuid()); actor.IsAdmin.Returns(true);
        actor.HasPermissionAsync("Program.View", Arg.Any<CancellationToken>()).Returns(true);
        var store = new EffectiveDayContentStore(db, new PatientScopeGuard(actor, new PatientScopeStore(db)));
        Assert.Equal(0, (await new GetContentGapsQueryHandler(store, new EffectiveDayContentResolver(store), TimeProvider.System).Handle(new(), default)).Total);
        Assert.Equal(0, (await OriginScriptAsync("simulate", reverse: true, fillLegacy: true)).GetProperty("escrituras").GetInt32());
        Assert.Equal(1, (await OriginScriptAsync("reverse", fillLegacy: true)).GetProperty("escrituras").GetInt32());
        await db.Entry(week).ReloadAsync(); Assert.Equal(before, week.TasksSnapshot.GetRawText());
        Assert.Equal(0, (await OriginScriptAsync("reverse", fillLegacy: true)).GetProperty("escrituras").GetInt32());
        await OriginScriptAsync("apply", fillLegacy: true);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE app.program_weeks SET tasks_snapshot=jsonb_set(tasks_snapshot,'{{2,points}}','77') WHERE id={week.Id}");
        Assert.Equal(1, (await OriginScriptAsync("reverse", fillLegacy: true)).GetProperty("counts").GetProperty("conflicto_editado_despues").GetInt32());
        await db.Entry(week).ReloadAsync(); Assert.Equal(77, week.TasksSnapshot[2].GetProperty("points").GetInt32());
        Assert.Equal(activeBefore, (await db.ProgramWeeks.AsNoTracking().SingleAsync(w => w.Id == active.Id)).TasksSnapshot.GetRawText());
        Assert.Equal(2, await db.RoutineAssignments.CountAsync()); Assert.Equal(1, await db.NutritionPlanAssignments.CountAsync());
    }
}
