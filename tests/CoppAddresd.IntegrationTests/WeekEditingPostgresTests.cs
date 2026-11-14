using CoppAddresd.Application.DTOs.ProgramProgress;
using CoppAddresd.Application.Features.ProgramProgress;
using CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent;
using CoppAddresd.Application.Features.ProgramProgress.Commands.ReplaceEnrollmentWeekTasks;
using CoppAddresd.Application.Features.ProgramProgress.Commands.SetWeekContent;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using MediatR;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    [NutritionPostgresFact]
    public async Task SemanaContenido_GuardaSoloSnapshot_NoCreaNiModificaAsignacionesAntiguas_YAjenoRechazado()
    {
        await using var db = Context(); var seed = await FutureEditingSeedAsync(db);
        var patient = await db.PatientProfiles.SingleAsync(p => p.Id == seed.Patient);
        var professional = NutritionProfessional(seed.Patient); db.Professionals.Add(professional);
        db.PatientProfessionalAssignments.Add(new() { PatientId = seed.Patient, ProfessionalId = professional.Id });
        var replacement = new ExerciseRoutine { Id = Guid.NewGuid(), Name = "Rutina nueva", Status = NutritionPlanStatus.Active };
        var legacyRoutine = new RoutineAssignment { Id = Guid.NewGuid(), PatientId = seed.Patient, RoutineId = seed.Routine,
            Status = AssignmentStatus.Active, StartDate = seed.Start.ToDateTime(TimeOnly.MinValue) };
        var legacyPlan = new NutritionPlanAssignment { Id = Guid.NewGuid(), PatientId = seed.Patient, PlanId = seed.Plan,
            Status = AssignmentStatus.Active, StartDate = seed.Start.ToDateTime(TimeOnly.MinValue) };
        db.ExerciseRoutines.Add(replacement); db.RoutineAssignments.Add(legacyRoutine); db.NutritionPlanAssignments.Add(legacyPlan); await db.SaveChangesAsync();
        var actor = Substitute.For<IPatientScopeActor>(); actor.UserId.Returns(patient.UserId);
        actor.ProfessionalIdAsync(Arg.Any<CancellationToken>()).Returns(professional.Id);
        actor.HasPermissionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var guard = new PatientScopeGuard(actor, new PatientScopeStore(db)); var repo = EffectiveRepository(db);
        var editor = new ReplaceEnrollmentWeekTasksCommandHandler(repo, NullLogger<ReplaceEnrollmentWeekTasksCommandHandler>.Instance, guard);
        var sender = Substitute.For<ISender>();
        sender.Send(Arg.Any<ReplaceEnrollmentWeekTasksCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => editor.Handle(call.Arg<ReplaceEnrollmentWeekTasksCommand>(), call.Arg<CancellationToken>()));
        var handler = new SetWeekContentHandler(repo, new WellnessRepository(db), sender, guard);
        var saved = await handler.Handle(new(seed.Enrollment, 2, seed.Plan, replacement.Id, patient.UserId), default);
        Assert.Equal(replacement.Id, saved.ExerciseRoutine!.Id);
        var week = await db.ProgramWeeks.AsNoTracking().SingleAsync(w => w.EnrollmentId == seed.Enrollment && w.WeekNumber == 2);
        var rows = WeekSnapshotJson.Parse(week.TasksSnapshot);
        Assert.Equal(7, rows.Count(r => r.TaskCode == "ejercicio" && r.RoutineId == replacement.Id && r.Origin == "individual"));
        Assert.Equal(7, rows.Count(r => r.TaskCode == "nut" && r.NutritionPlanId == seed.Plan && r.Origin == "individual"));
        Assert.Equal(7, rows.Count(r => r.TaskCode == "podcast" && r.MediaId == seed.Podcast));
        await handler.Handle(new(seed.Enrollment, 2, null, null, patient.UserId), default);
        var reset = WeekSnapshotJson.Parse((await db.ProgramWeeks.AsNoTracking().SingleAsync(w => w.Id == week.Id)).TasksSnapshot);
        Assert.All(reset, row => Assert.Equal("program", row.Origin));
        var oldRoutine = Assert.Single(await db.RoutineAssignments.AsNoTracking().ToArrayAsync());
        Assert.Equal(legacyRoutine.Id, oldRoutine.Id); Assert.Equal(AssignmentStatus.Active, oldRoutine.Status); Assert.Null(oldRoutine.EndDate); Assert.Null(oldRoutine.UpdatedAt);
        var oldPlan = Assert.Single(await db.NutritionPlanAssignments.AsNoTracking().ToArrayAsync());
        Assert.Equal(legacyPlan.Id, oldPlan.Id); Assert.Equal(AssignmentStatus.Active, oldPlan.Status); Assert.Null(oldPlan.EndDate); Assert.Null(oldPlan.UpdatedAt);
        var foreign = await PatientAsync(db); var other = new ProgramEnrollment { Id = Guid.NewGuid(), PatientId = foreign.Id, TemplateId = seed.Template, StartLocalDate = seed.Start };
        db.ProgramEnrollments.Add(other); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(new(other.Id, 2, seed.Plan, replacement.Id, patient.UserId), default));
        Assert.False(await db.ProgramWeeks.AnyAsync(w => w.EnrollmentId == other.Id));
    }

    private async Task<EffectiveSeed> FutureEditingSeedAsync(CoppAddresd.Infrastructure.Persistence.AppDbContext db)
    {
        var seed = await EffectiveSeedAsync(db); var start = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(21);
        while (start.DayOfWeek != DayOfWeek.Monday) start = start.AddDays(1);
        (await db.ProgramEnrollments.SingleAsync(e => e.Id == seed.Enrollment)).StartLocalDate = start;
        foreach (var week in await db.ProgramWeeks.Where(w => w.EnrollmentId == seed.Enrollment).ToArrayAsync())
        { week.WeekStartDateLocal = start.AddDays((week.WeekNumber - 1) * 7); week.WeekEndDateLocal = week.WeekStartDateLocal.AddDays(6); }
        await db.SaveChangesAsync(); return seed with { Start = start };
    }

    [NutritionPostgresFact]
    public async Task Descanso_InterruptorRechazaSinEscribir_YEncendidoPersisteDescanso()
    {
        await using var db = Context(); var seed = await FutureEditingSeedAsync(db);
        var week = await db.ProgramWeeks.SingleAsync(w => w.EnrollmentId == seed.Enrollment && w.WeekNumber == 2);
        var before = (await db.ProgramWeeks.AsNoTracking().SingleAsync(w => w.Id == week.Id)).TasksSnapshot.GetRawText();
        var command = new ReplaceEnrollmentWeekTasksCommand(seed.Enrollment, 2,
            [new WeeklyDayTemplateRequest(1, TaskCode.ejercicio, 10, Rest: true)], Guid.NewGuid());
        var disabled = new ReplaceEnrollmentWeekTasksCommandHandler(EffectiveRepository(db), NullLogger<ReplaceEnrollmentWeekTasksCommandHandler>.Instance, Substitute.For<IPatientScopeGuard>());
        await Assert.ThrowsAsync<UnprocessableEntityException>(() => disabled.Handle(command, default));
        Assert.Equal(before, (await db.ProgramWeeks.AsNoTracking().SingleAsync(w => w.Id == week.Id)).TasksSnapshot.GetRawText());
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [ProgramRestDayPolicy.ConfigurationKey] = "true" }).Build();
        var enabled = new ReplaceEnrollmentWeekTasksCommandHandler(new ProgramRepository(db, config), NullLogger<ReplaceEnrollmentWeekTasksCommandHandler>.Instance, Substitute.For<IPatientScopeGuard>());
        await enabled.Handle(command, default);
        var row = Assert.Single(WeekSnapshotJson.Parse((await db.ProgramWeeks.AsNoTracking().SingleAsync(w => w.Id == week.Id)).TasksSnapshot), r => r.Weekday == 1 && r.TaskCode == "ejercicio");
        Assert.True(row.Rest); Assert.Equal("individual", row.Origin); Assert.Equal(command.ActorId, row.EditedBy);
    }

    [NutritionPostgresFact]
    public async Task SemanaEdicion_RutinaDescansoRestablecer_PreservaOmitidos_YAlcanceAjeno()
    {
        await using var db = Context(); var seed = await FutureEditingSeedAsync(db);
        var foreign = await PatientAsync(db); var patient = await db.PatientProfiles.SingleAsync(p => p.Id == seed.Patient);
        var professional = NutritionProfessional(seed.Patient); db.Professionals.Add(professional);
        db.PatientProfessionalAssignments.Add(new() { PatientId = seed.Patient, ProfessionalId = professional.Id });
        var routine = new ExerciseRoutine { Id = Guid.NewGuid(), Name = "Otra rutina", Status = NutritionPlanStatus.Active };
        db.ExerciseRoutines.Add(routine); await db.SaveChangesAsync();
        var actor = Substitute.For<IPatientScopeActor>(); actor.UserId.Returns(patient.UserId);
        actor.ProfessionalIdAsync(Arg.Any<CancellationToken>()).Returns(professional.Id);
        actor.HasPermissionAsync("Program.Edit", Arg.Any<CancellationToken>()).Returns(true);
        var guard = new PatientScopeGuard(actor, new PatientScopeStore(db));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [ProgramRestDayPolicy.ConfigurationKey] = "true" }).Build();
        var handler = new ReplaceEnrollmentWeekTasksCommandHandler(new ProgramRepository(db, config), NullLogger<ReplaceEnrollmentWeekTasksCommandHandler>.Instance, guard);
        async Task Edit(params WeeklyDayTemplateRequest[] tasks) => await handler.Handle(new(seed.Enrollment, 2, tasks, patient.UserId), default);
        async Task<IReadOnlyList<WeekSnapshotTaskRow>> Rows() => WeekSnapshotJson.Parse((await db.ProgramWeeks.AsNoTracking().SingleAsync(w => w.EnrollmentId == seed.Enrollment && w.WeekNumber == 2)).TasksSnapshot);
        await Edit(new WeeklyDayTemplateRequest(1, TaskCode.ejercicio, 25, RoutineId: routine.Id));
        var assigned = Assert.Single(await Rows(), r => r.Weekday == 1 && r.TaskCode == "ejercicio");
        Assert.Equal(routine.Id, assigned.RoutineId); Assert.Equal("individual", assigned.Origin); Assert.Equal(patient.UserId, assigned.EditedBy);
        await Edit(new WeeklyDayTemplateRequest(2, TaskCode.ejercicio, 10, Rest: true));
        Assert.Equal(assigned, Assert.Single(await Rows(), r => r.Weekday == 1 && r.TaskCode == "ejercicio"));
        Assert.Equal(21, (await Rows()).Count); Assert.True(Assert.Single(await Rows(), r => r.Weekday == 2 && r.TaskCode == "ejercicio").Rest);
        await Edit(new(1, TaskCode.ejercicio, 0, Reset: true), new(2, TaskCode.ejercicio, 0, Reset: true));
        foreach (var row in (await Rows()).Where(r => r.TaskCode == "ejercicio" && r.Weekday <= 2))
        { Assert.Equal("program", row.Origin); Assert.Equal(seed.Routine, row.RoutineId); Assert.NotEqual(true, row.Rest); Assert.Equal(10, row.Points); }
        var before = (await Rows()).ToArray();
        await Assert.ThrowsAsync<UnprocessableEntityException>(() => Edit(new WeeklyDayTemplateRequest(3, TaskCode.ejercicio, 10, RoutineId: Guid.NewGuid())));
        Assert.Equal(before, await Rows());
        var otherEnrollment = new ProgramEnrollment { Id = Guid.NewGuid(), PatientId = foreign.Id, TemplateId = seed.Template, StartLocalDate = seed.Start };
        db.ProgramEnrollments.Add(otherEnrollment); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(new(otherEnrollment.Id, 2, [new(1, TaskCode.ejercicio, 10, Rest: true)]), default));
        Assert.False(await db.ProgramWeeks.AnyAsync(w => w.EnrollmentId == otherEnrollment.Id));
        actor.IsAdmin.Returns(true);
        await guard.EnsureResourceAsync(PatientResource.Enrollment, otherEnrollment.Id, "Program.Edit", default);
    }

    [NutritionPostgresFact]
    public async Task SemanaEdicion_IniciadaRechazada_SnapshotCompletacionesYXpIntactos()
    {
        await using var db = Context(); var seed = await EffectiveSeedAsync(db);
        var week = await db.ProgramWeeks.SingleAsync(w => w.EnrollmentId == seed.Enrollment && w.WeekNumber == 1);
        var checkin = new DailyCheckIn { Id = Guid.NewGuid(), EnrollmentId = seed.Enrollment, ProgramWeekId = week.Id,
            LocalDate = seed.Start, Weekday = 1, TotalPoints = 10 };
        var completion = new TaskCompletion { Id = Guid.NewGuid(), EnrollmentId = seed.Enrollment, ProgramWeekId = week.Id,
            DailyCheckinId = checkin.Id, LocalDate = seed.Start, Weekday = 1, TaskCode = TaskCode.ejercicio,
            PointsAwarded = 10, ClientRequestId = Guid.NewGuid().ToString(), ExerciseRoutineId = seed.Routine, SourceRefType = "exercise" };
        db.DailyCheckIns.Add(checkin); db.TaskCompletions.Add(completion); await db.SaveChangesAsync();
        var before = (await db.ProgramWeeks.AsNoTracking().SingleAsync(w => w.Id == week.Id)).TasksSnapshot.GetRawText();
        var history = (completion.Id, completion.LocalDate, completion.PointsAwarded, completion.ClientRequestId, completion.ExerciseRoutineId);
        await Assert.ThrowsAsync<BusinessRuleViolationException>(() => EffectiveRepository(db).ReplaceEnrollmentWeekTasksAsync(seed.Enrollment, 1,
            [new() { Weekday = 1, TaskCode = TaskCode.ejercicio, RoutineId = seed.Routine }]));
        Assert.Equal(before, (await db.ProgramWeeks.AsNoTracking().SingleAsync(w => w.Id == week.Id)).TasksSnapshot.GetRawText());
        Assert.Equal(10, (await db.DailyCheckIns.AsNoTracking().SingleAsync(c => c.Id == checkin.Id)).TotalPoints);
        var after = await db.TaskCompletions.AsNoTracking().SingleAsync(c => c.Id == completion.Id);
        Assert.Equal(history, (after.Id, after.LocalDate, after.PointsAwarded, after.ClientRequestId, after.ExerciseRoutineId));
    }
}
