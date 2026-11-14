using System.Text.Json;
using CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    [NutritionPostgresFact]
    public async Task RutinaDia_Postgres_PrecedenciaSimpleRangoYSnapshotAntiguo()
    {
        await using var db = Context(); var seed = await EffectiveSeedAsync(db);
        var personal = new ExerciseRoutine { Id = Guid.NewGuid(), Name = "Personal", Status = NutritionPlanStatus.Active };
        var legacy = new ExerciseRoutine { Id = Guid.NewGuid(), Name = "Legado", Status = NutritionPlanStatus.Active };
        db.ExerciseRoutines.AddRange(personal, legacy);
        db.RoutineAssignments.Add(new() { PatientId = seed.Patient, RoutineId = legacy.Id, StartDate = seed.Start.AddDays(-1).ToDateTime(TimeOnly.MinValue), Status = AssignmentStatus.Active });
        await db.SaveChangesAsync();
        var store = new EffectiveDayContentStore(db); var effective = new EffectiveDayContentResolver(store);
        var bridge = new ProgramContentResolver(new WellnessRepository(db), NullLogger<ProgramContentResolver>.Instance,
            new NutritionPrescriptionRepository(db), store, effective);
        foreach (var scenario in new[] { "rest", "snapshot", "template", "old" })
        {
            var week = await db.ProgramWeeks.SingleAsync(w => w.EnrollmentId == seed.Enrollment && w.WeekNumber == 1);
            week.TasksSnapshot = scenario == "old" ? JsonSerializer.Deserialize<JsonElement>("""[{"weekday":1,"task_code":"ejercicio","points":10,"sort_order":1}]""")
                : WeekSnapshotJson.Serialize([new(1, "ejercicio", 10, 1, RoutineId: scenario is "rest" or "snapshot" ? personal.Id : null, Rest: scenario == "rest")]);
            await db.SaveChangesAsync();
            if (scenario == "old") await db.WeeklyDayTemplates.Where(d => d.TemplateId == seed.Template && d.TaskCode == TaskCode.ejercicio)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.RoutineId, (Guid?)null));
            var single = await bridge.ResolveAsync(seed.Patient, seed.Start);
            var range = await bridge.ResolveRangeAsync(seed.Patient, seed.Start, seed.Start);
            Assert.Equal(single, range[seed.Start]);
            Assert.Equal(scenario switch { "rest" => null, "snapshot" => personal.Id, "template" => seed.Routine, _ => (Guid?)legacy.Id }, single!.ExerciseRoutineId);
            Assert.Equal(scenario == "rest", single.Rest);
        }
    }
}
