using CoppAddresd.Application.DTOs.ProgramProgress;
using CoppAddresd.Application.Features.ProgramProgress.Commands.ReplaceEnrollmentWeekTasks;
using CoppAddresd.Application.Features.ProgramProgress.Queries.Erp;
using CoppAddresd.Application.Features.ProgramProgress;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    [NutritionPostgresFact]
    public async Task BitacoraSemanal_TresOperacionesActorAntesDespues_AlcancePaginacionYRollbackAtomico()
    {
        await using var db = Context(); var seed = await FutureEditingSeedAsync(db);
        var patient = await db.PatientProfiles.SingleAsync(p => p.Id == seed.Patient);
        var professional = NutritionProfessional(seed.Patient); db.Professionals.Add(professional);
        db.PatientProfessionalAssignments.Add(new() { PatientId = seed.Patient, ProfessionalId = professional.Id }); await db.SaveChangesAsync();
        var actor = Substitute.For<IPatientScopeActor>(); actor.UserId.Returns(patient.UserId);
        actor.ProfessionalIdAsync(Arg.Any<CancellationToken>()).Returns(professional.Id);
        actor.HasPermissionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var guard = new PatientScopeGuard(actor, new PatientScopeStore(db));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [ProgramRestDayPolicy.ConfigurationKey] = "true" }).Build();
        var repo = new ProgramRepository(db, config); var writer = new ReplaceEnrollmentWeekTasksCommandHandler(repo, NullLogger<ReplaceEnrollmentWeekTasksCommandHandler>.Instance, guard);
        async Task Edit(WeeklyDayTemplateRequest task) => await writer.Handle(new(seed.Enrollment, 2, [task], patient.UserId), default);
        await Edit(new(1, TaskCode.ejercicio, 10, RoutineId: seed.Routine));
        await Edit(new(1, TaskCode.ejercicio, 10, Rest: true));
        await Edit(new(1, TaskCode.ejercicio, 0, Reset: true));
        var reader = new GetWeekPlanChangesHandler(repo, guard);
        var history = await reader.Handle(new(seed.Enrollment, 2), default);
        Assert.Equal(3, history.Total); Assert.Equal(3, history.Items.Count);
        Assert.All(history.Items, row => { Assert.Equal(patient.UserId, row.ActorId); Assert.NotEqual(default, row.OccurredAt); Assert.True(row.Before.HasValue); Assert.True(row.After.HasValue); });
        Assert.Equal(new[] { "restablecer", "descanso", "asignar" }, history.Items.Select(row => row.Metadata!.Value.GetProperty("operations")[0].GetProperty("operation").GetString()));
        Assert.Equal(0, history.Items[^1].Before!.Value.GetProperty("tasks_snapshot").GetArrayLength());
        Assert.Equal(history.Items[0].Before!.Value.GetRawText(), history.Items[1].After!.Value.GetRawText());
        Assert.Single((await reader.Handle(new(seed.Enrollment, 2, 3, 1), default)).Items);
        Assert.Empty((await reader.Handle(new(seed.Enrollment, 2, int.MaxValue, 1), default)).Items);
        Assert.Equal(0, (await reader.Handle(new(seed.Enrollment, 3), default)).Total);
        var foreign = await PatientAsync(db); var other = new ProgramEnrollment { Id = Guid.NewGuid(), PatientId = foreign.Id, TemplateId = seed.Template, StartLocalDate = seed.Start };
        db.ProgramEnrollments.Add(other); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<NotFoundException>(() => reader.Handle(new(other.Id, 2), default));
        actor.IsAdmin.Returns(true); await guard.EnsureResourceAsync(PatientResource.Enrollment, other.Id, "Program.Profile360View", default); actor.IsAdmin.Returns(false);
        var week = await db.ProgramWeeks.AsNoTracking().SingleAsync(w => w.EnrollmentId == seed.Enrollment && w.WeekNumber == 2);
        var before = week.TasksSnapshot.GetRawText();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION audit.reject_week_plan_change() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
              IF NEW.metadata->>'event' = 'ProgramWeekTasksChanged' THEN RAISE EXCEPTION 'Fallo controlado de auditoría'; END IF;
              RETURN NEW; END $$;
            CREATE TRIGGER reject_week_plan_change BEFORE INSERT ON audit.activity_logs FOR EACH ROW EXECUTE FUNCTION audit.reject_week_plan_change();
            """);
        await Assert.ThrowsAsync<PostgresException>(() => Edit(new(2, TaskCode.ejercicio, 10, Rest: true)));
        Assert.Equal(before, (await db.ProgramWeeks.AsNoTracking().SingleAsync(w => w.Id == week.Id)).TasksSnapshot.GetRawText());
        Assert.Equal(3, (await reader.Handle(new(seed.Enrollment, 2), default)).Total);
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_week_plan_change ON audit.activity_logs; DROP FUNCTION audit.reject_week_plan_change();");
        await Edit(new(3, TaskCode.ejercicio, 10, Rest: true));
        var after = (await db.ProgramWeeks.AsNoTracking().SingleAsync(w => w.Id == week.Id)).TasksSnapshot;
        Assert.DoesNotContain(CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent.WeekSnapshotJson.Parse(after), r => r.Weekday == 2 && r.Rest == true);
        Assert.Equal(4, (await reader.Handle(new(seed.Enrollment, 2), default)).Total);
    }
}
