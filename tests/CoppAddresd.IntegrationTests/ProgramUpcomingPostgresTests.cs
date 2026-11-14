using CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using CoppAddresd.Infrastructure.Persistence;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    [NutritionPostgresFact]
    public async Task ProximosDias_SeisConDescansoIndividualesSinEscriturasNiContenidoAjeno()
    {
        await using var seedDb = Context(); var seed = await EffectiveSeedAsync(seedDb); var foreign = await PatientAsync(seedDb);
        var personal = new ExerciseRoutine { Id = Guid.NewGuid(), Name = "Rutina individual", Status = NutritionPlanStatus.Active };
        var foreignRoutine = new ExerciseRoutine { Id = Guid.NewGuid(), Name = "Rutina ajena", Status = NutritionPlanStatus.Active };
        seedDb.ExerciseRoutines.AddRange(personal, foreignRoutine);
        var foreignEnrollment = new ProgramEnrollment { Id = Guid.NewGuid(), PatientId = foreign.Id, TemplateId = seed.Template, StartLocalDate = seed.Start };
        seedDb.ProgramEnrollments.Add(foreignEnrollment);
        seedDb.ProgramWeeks.Add(new() { Id = Guid.NewGuid(), EnrollmentId = foreignEnrollment.Id, WeekNumber = 2,
            WeekStartDateLocal = seed.Start.AddDays(7), WeekEndDateLocal = seed.Start.AddDays(13),
            TasksSnapshot = WeekSnapshotJson.Serialize([new(1, "ejercicio", 10, 1, RoutineId: foreignRoutine.Id, Origin: "individual", Rest: true)]) });
        var current = await seedDb.ProgramWeeks.SingleAsync(w => w.EnrollmentId == seed.Enrollment && w.WeekNumber == 1);
        current.TasksSnapshot = WeekSnapshotJson.Serialize(WeekSnapshotJson.Parse(current.TasksSnapshot).Select(t => t.TaskCode == "ejercicio"
            ? t with { Rest = t.Weekday is 3 or 4, RoutineId = t.Weekday == 5 ? personal.Id : t.RoutineId, Origin = "individual" } : t).ToArray());
        var future = await seedDb.ProgramWeeks.SingleAsync(w => w.EnrollmentId == seed.Enrollment && w.WeekNumber == 2);
        future.TasksSnapshot = WeekSnapshotJson.Serialize([new(1, "ejercicio", 10, 1, RoutineId: personal.Id, Origin: "individual"),
            new(2, "ejercicio", 10, 1, RoutineId: personal.Id, Origin: "program")]);
        await seedDb.SaveChangesAsync();
        var before = (await seedDb.ProgramWeeks.AsNoTracking().OrderBy(w => w.Id).Select(w => w.TasksSnapshot).ToArrayAsync()).Select(s => s.GetRawText()).ToArray();
        var counter = new EffectiveSqlCounter();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).AddInterceptors(counter).Options);
        var repo = EffectiveRepository(db); var today = seed.Start.AddDays(2);
        var snapshot = await repo.GetSnapshotAsync(seed.Enrollment, today, CancellationToken.None);
        var reads = counter.Reads; counter.Reads = 0;
        var second = await repo.GetSnapshotAsync(seed.Enrollment, today.AddDays(1), CancellationToken.None);
        Assert.Equal(reads, counter.Reads); Assert.NotNull(second!.Upcoming);
        Assert.True(snapshot!.Rest); Assert.DoesNotContain(snapshot.TodayTasks, t => t.TaskCode == CoppAddresd.Domain.Enums.ProgramProgress.TaskCode.ejercicio);
        var upcoming = snapshot.Upcoming!; Assert.Equal(6, upcoming.Count);
        Assert.Equal(today.AddDays(1), upcoming[0].Date); Assert.Equal(today.AddDays(6), upcoming[^1].Date);
        Assert.True(upcoming[0].Rest); Assert.DoesNotContain(upcoming[0].Missions, m => m.Type == "ejercicio");
        Assert.Equal(personal.Id, Assert.Single(upcoming[1].Missions, m => m.Type == "ejercicio").RoutineId);
        Assert.Equal(personal.Id, Assert.Single(upcoming[4].Missions, m => m.Type == "ejercicio").RoutineId);
        Assert.Equal(seed.Routine, Assert.Single(upcoming[5].Missions, m => m.Type == "ejercicio").RoutineId);
        Assert.All(upcoming.SelectMany(d => d.Missions), m => Assert.NotEqual(foreignRoutine.Id, m.RoutineId));
        Assert.Equal(before, (await db.ProgramWeeks.AsNoTracking().OrderBy(w => w.Id).Select(w => w.TasksSnapshot).ToArrayAsync()).Select(s => s.GetRawText()).ToArray());
        Assert.Empty(await db.TaskCompletions.AsNoTracking().ToArrayAsync());
        var enrollment = await seedDb.ProgramEnrollments.SingleAsync(e => e.Id == seed.Enrollment); enrollment.CurrentWeekNumber = 12;
        seedDb.ProgramWeeks.Add(new() { Id = Guid.NewGuid(), EnrollmentId = seed.Enrollment, WeekNumber = 12,
            WeekStartDateLocal = seed.Start.AddDays(77), WeekEndDateLocal = seed.Start.AddDays(83), TasksSnapshot = WeekSnapshotJson.Serialize([]) });
        await seedDb.SaveChangesAsync();
        var last = await repo.GetSnapshotAsync(seed.Enrollment, seed.Start.AddDays(80), CancellationToken.None);
        Assert.Equal(new[] { 82, 83 }, last!.Upcoming!.Select(d => d.Day));
        Assert.Empty((await repo.GetSnapshotAsync(seed.Enrollment, seed.Start.AddDays(82), CancellationToken.None))!.Upcoming!);
    }
}
