using CoppAddresd.Application.DTOs.ProgramProgress;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Domain.Enums.ProgramProgress;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    [NutritionPostgresFact]
    public async Task SesionEjercicio_CapturaAlCompletar_EditarlaNoCambiaHistorialNiReplay()
    {
        await using var db = Context(); var seed = await EffectiveSeedAsync(db);
        var media = new MediaItem { Id = Guid.NewGuid(), Title = "Técnica", Author = "Equipo", MediaType = MediaType.Video, Status = MediaStatus.Published, StorageKey = "media/tecnica.mp4" };
        var routine = await db.ExerciseRoutines.SingleAsync(r => r.Id == seed.Routine);
        routine.Name = "Sesión original";
        var exercise = new RoutineExercise { Id = Guid.NewGuid(), RoutineId = routine.Id, Name = "Sentadilla", Sets = 3, Repetitions = 12, MediaId = media.Id, Tips = "Descenso controlado" };
        db.MediaItems.Add(media); db.RoutineExercises.Add(exercise); await db.SaveChangesAsync();
        var repo = EffectiveRepository(db); var requestId = Guid.NewGuid().ToString();
        var input = new CompleteTaskInput(seed.Enrollment, seed.Start, TaskCode.ejercicio, requestId, null, null, null, null);
        await repo.CompleteTaskAsync(input, default);
        var completion = await db.TaskCompletions.AsNoTracking().SingleAsync();
        var captured = completion.DetailSnapshot!.Value.GetRawText(); var xpCount = await db.XpLedgerEntries.CountAsync();
        Assert.Equal(seed.Routine, CompletedExerciseDetail.Read(completion.DetailSnapshot)!.RoutineId);
        routine.Name = "Rutina editada después"; exercise.Name = "Otra técnica"; exercise.Sets = 8; exercise.Repetitions = 20; exercise.MediaId = null;
        db.RoutineExercises.Add(new() { Id = Guid.NewGuid(), RoutineId = routine.Id, Name = "Nuevo ejercicio", Sets = 1 }); await db.SaveChangesAsync();
        await repo.CompleteTaskAsync(input, default);
        Assert.Equal(xpCount, await db.XpLedgerEntries.CountAsync());
        Assert.Equal(captured, (await db.TaskCompletions.AsNoTracking().SingleAsync()).DetailSnapshot!.Value.GetRawText());
        var history = await repo.GetEnrollmentWeekDetailAsync(seed.Enrollment, 1, Guid.Empty, default);
        var task = Assert.Single(history!.Days.Single(d => d.LocalDate == seed.Start).Tasks, t => t.TaskCode == "ejercicio");
        Assert.Equal("Sesión original", task.ContentName); Assert.Equal(captured, task.DetailSnapshot!.Value.GetRawText()); Assert.Equal("completion", task.Source);
        var snapshot = await repo.GetSnapshotAsync(seed.Enrollment, seed.Start, default);
        var content = Assert.Single(snapshot!.TodayTasks, t => t.TaskCode == TaskCode.ejercicio).Content!;
        Assert.Equal("Sesión original", content.ExerciseRoutineName); var frozen = Assert.Single(content.Exercises!);
        Assert.Equal("Sentadilla", frozen.Name); Assert.Equal(3, frozen.Sets); Assert.Equal(12, frozen.Repetitions); Assert.Equal(media.StorageKey, frozen.MediaUrl);
        await db.MediaItems.Where(m => m.Id == media.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, MediaStatus.Archived));
        var retired = await repo.GetSnapshotAsync(seed.Enrollment, seed.Start, default);
        Assert.Null(retired!.TodayTasks.Single(t => t.TaskCode == TaskCode.ejercicio).Content!.Exercises!.Single().MediaUrl);
        Assert.Equal(captured, (await db.TaskCompletions.AsNoTracking().SingleAsync()).DetailSnapshot!.Value.GetRawText());
        // Una sesión sin referencia conocida no debe inventar contenido después.
        var empty = CompletedExerciseDetail.Capture(null, DateTimeOffset.UtcNow);
        await db.TaskCompletions.ExecuteUpdateAsync(s => s.SetProperty(c => c.DetailSnapshot, empty));
        var unavailable = await repo.GetSnapshotAsync(seed.Enrollment, seed.Start, default);
        Assert.True(unavailable!.TodayTasks.Single(t => t.TaskCode == TaskCode.ejercicio).ContentUnavailable);
    }

    [NutritionPostgresFact]
    public async Task SesionEjercicio_MigracionNullableVolumen_ReversaProtegida()
    {
        await using var db = Context(); var seed = await EffectiveSeedAsync(db);
        var repo = EffectiveRepository(db); await repo.CompleteTaskAsync(new(seed.Enrollment, seed.Start, TaskCode.podcast, null, null, null, null, null), default);
        var completion = await db.TaskCompletions.AsNoTracking().SingleAsync(); var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261113205636_AllowThirtyDayLegacyNutritionCompletions");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO app.task_completions (id,enrollment_id,program_week_id,daily_checkin_id,local_date,weekday,task_code,points_awarded,completed_at,source_ref_type) SELECT gen_random_uuid(),{completion.EnrollmentId},{completion.ProgramWeekId},{completion.DailyCheckinId},{completion.LocalDate} - n,1,'podcast',0,now(),'volume' FROM generate_series(1,10000) n");
        await migrator.MigrateAsync(); Assert.Equal(10001, await db.TaskCompletions.CountAsync());
        Assert.False(await db.TaskCompletions.AnyAsync(c => c.DetailSnapshot != null));
        var detail = CompletedExerciseDetail.Capture(null, DateTimeOffset.UtcNow);
        await db.TaskCompletions.Where(c => c.Id == completion.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.DetailSnapshot, detail));
        await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync("20261113205636_AllowThirtyDayLegacyNutritionCompletions"));
        Assert.True((await db.TaskCompletions.AsNoTracking().SingleAsync(c => c.Id == completion.Id)).DetailSnapshot.HasValue);
        await db.TaskCompletions.ExecuteUpdateAsync(s => s.SetProperty(c => c.DetailSnapshot, (System.Text.Json.JsonElement?)null));
        await migrator.MigrateAsync("20261113205636_AllowThirtyDayLegacyNutritionCompletions"); await migrator.MigrateAsync();
        Assert.Equal(10001, await db.TaskCompletions.CountAsync());
    }
}
