using System.Text.Json.Nodes;
using CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Domain.Enums.ProgramProgress;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.IntegrationTests;

public sealed class NutritionPostgresTheoryAttribute : TheoryAttribute
{
    public NutritionPostgresTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COP_TEST_DB_CONNECTION")))
            Skip = "Requiere PostgreSQL aislado mediante COP_TEST_DB_CONNECTION.";
    }
}

public sealed partial class NutritionPlanWorkflowTests
{
    [NutritionPostgresTheory]
    [InlineData("snapshot")]
    [InlineData("rest")]
    [InlineData("old")]
    [InlineData("empty")]
    public async Task RutinaRespuestas_SnapshotYDetalleSemanalCoinciden(string scenario)
    {
        await using var db = Context(); var seed = await EffectiveSeedAsync(db);
        var personal = new ExerciseRoutine { Id = Guid.NewGuid(), Name = "Rutina del paciente", Status = NutritionPlanStatus.Active };
        db.ExerciseRoutines.Add(personal);
        db.RoutineAssignments.Add(new() { PatientId = seed.Patient, RoutineId = personal.Id, Status = AssignmentStatus.Active,
            StartDate = seed.Start.ToDateTime(TimeOnly.MinValue) });
        var week = await db.ProgramWeeks.SingleAsync(w => w.EnrollmentId == seed.Enrollment && w.WeekNumber == 1);
        week.TasksSnapshot = WeekSnapshotJson.Serialize(WeekSnapshotJson.Parse(week.TasksSnapshot).Select(r =>
            r.TaskCode == "ejercicio" && r.Weekday == 1 ? r with { RoutineId = personal.Id, Rest = scenario == "rest" } : r).ToArray());
        if (scenario == "old")
        {
            var json = JsonNode.Parse(week.TasksSnapshot.GetRawText())!.AsArray();
            foreach (var item in json)
            {
                var row = item!.AsObject(); row.Remove("origin"); row.Remove("rest"); row.Remove("routine_id");
            }
            week.TasksSnapshot = System.Text.Json.JsonSerializer.SerializeToElement(json);
        }
        if (scenario == "empty") week.TasksSnapshot = WeekSnapshotJson.Serialize([]);
        await db.SaveChangesAsync();
        if (scenario == "old") await db.WeeklyDayTemplates.Where(d => d.TemplateId == seed.Template && d.TaskCode == TaskCode.ejercicio)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.RoutineId, (Guid?)null));
        var repo = EffectiveRepository(db);
        var snapshot = await repo.GetSnapshotAsync(seed.Enrollment, seed.Start, CancellationToken.None);
        var detail = await repo.GetEnrollmentWeekDetailAsync(seed.Enrollment, 1, Guid.Empty, CancellationToken.None);
        var day = Assert.Single(detail!.Days, d => d.LocalDate == seed.Start);
        Assert.Equal(snapshot!.TodayTasks.Select(t => t.TaskCode.ToString()).Order(), day.Tasks.Select(t => t.TaskCode).Order());
        if (scenario == "rest")
        {
            Assert.DoesNotContain(snapshot.TodayTasks, t => t.TaskCode == TaskCode.ejercicio);
            Assert.DoesNotContain(day.Tasks, t => t.TaskCode == "ejercicio"); Assert.True(day.Rest);
        }
        else
        {
            var mission = Assert.Single(snapshot.TodayTasks, t => t.TaskCode == TaskCode.ejercicio);
            var row = Assert.Single(day.Tasks, t => t.TaskCode == "ejercicio");
            Assert.Equal(scenario == "empty" ? seed.Routine : personal.Id, mission.Content!.ExerciseRoutineId);
            Assert.Equal(mission.Content.ExerciseRoutineId, row.ContentRefId);
            Assert.Equal(mission.Content.ExerciseRoutineName, row.ContentName); Assert.Equal(mission.Points, row.ScheduledPoints);
        }
    }
}
