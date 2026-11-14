using CoppAddresd.Application.Common.Behaviors;
using CoppAddresd.Application.Features.Wellness;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Repositories;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NSubstitute;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    [NutritionPostgresFact]
    public async Task ClonarRutina_CopiaGrafoYDemostraciones_PreservaAdvertencias_AlcancePropioAjenoYAdmin()
    {
        await using var db = Context(); var own = await PatientAsync(db); var foreign = await PatientAsync(db);
        var professional = NutritionProfessional(own.Id); db.Professionals.Add(professional);
        db.PatientProfessionalAssignments.Add(new() { PatientId = own.Id, ProfessionalId = professional.Id });
        var video = new MediaItem { Id = Guid.NewGuid(), Title = "Demostración", Author = "Equipo", MediaType = MediaType.Video,
            Status = MediaStatus.Published, SourceKind = "external", ExternalUrl = "https://www.youtube.com/watch?v=0FkRr45YeEQ", StorageKey = "external/youtube/0FkRr45YeEQ" };
        var image = new MediaItem { Id = Guid.NewGuid(), Title = "Técnica", Author = "Equipo", MediaType = MediaType.Image, StorageKey = "media/tecnica.png" };
        db.MediaItems.AddRange(video, image);
        var source = new ExerciseRoutine { Id = Guid.NewGuid(), Name = "Fuerza", Description = "Sesión base", Status = NutritionPlanStatus.Active,
            Contraindications = "Revisar lesión de rodilla", TargetMuscles = "Piernas", WarmupNotes = "Cinco minutos", MediaId = video.Id,
            Exercises = [new() { Id = Guid.NewGuid(), Name = "Sentarse y levantarse", Sets = 3, Repetitions = 12, RestSeconds = 30, WeightKg = 2,
                Tempo = "2-1-2", Rpe = 5, Tips = "Controlar descenso", MediaId = video.Id, SortOrder = 1 },
                new() { Id = Guid.NewGuid(), Name = "Plancha", DurationSecs = 30, MediaId = image.Id, SortOrder = 2 }] };
        db.ExerciseRoutines.Add(source); await db.SaveChangesAsync();
        var actor = Substitute.For<IPatientScopeActor>(); actor.UserId.Returns(own.UserId);
        actor.ProfessionalIdAsync(Arg.Any<CancellationToken>()).Returns(professional.Id);
        actor.HasPermissionAsync("Wellness.Manage", Arg.Any<CancellationToken>()).Returns(true);
        var services = new ServiceCollection(); services.AddLogging(); services.AddScoped(_ => Context());
        services.AddSingleton(actor); services.AddScoped<IPatientScopeStore, PatientScopeStore>(); services.AddScoped<IPatientScopeGuard, PatientScopeGuard>();
        services.AddScoped<IWellnessRepository, WellnessRepository>();
        services.AddMediatR(c => c.RegisterServicesFromAssembly(typeof(CloneExerciseRoutineCommand).Assembly));
        services.AddValidatorsFromAssemblyContaining<CloneExerciseRoutineValidator>();
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        await using var provider = services.BuildServiceProvider();
        async Task<T> Send<T>(IRequest<T> request) { await using var scope = provider.CreateAsyncScope(); return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request); }
        var clone = await Send(new CloneExerciseRoutineCommand(source.Id, new()));
        Assert.NotEqual(source.Id, clone.Id); Assert.Equal(NutritionPlanStatus.Draft, clone.Status); Assert.Null(clone.AssignmentId);
        Assert.Equal(source.Contraindications, clone.Contraindications); Assert.Equal(source.MediaId, clone.MediaId); Assert.Equal(own.UserId, clone.CreatedBy);
        Assert.Equal(2, clone.Exercises.Count); Assert.Equal(new[] { video.Id, image.Id }, clone.Exercises.Select(e => e.MediaId!.Value));
        Assert.DoesNotContain(clone.Exercises, e => source.Exercises.Any(s => s.Id == e.Id));
        var first = clone.Exercises[0]; Assert.Equal(3, first.Sets); Assert.Equal(12, first.Repetitions); Assert.Equal("2-1-2", first.Tempo); Assert.Equal(5, first.Rpe);
        var personal = await Send(new CloneExerciseRoutineCommand(source.Id, new("Mi versión", own.Id)));
        Assert.Equal(NutritionPlanStatus.Active, personal.Status); Assert.NotNull(personal.AssignmentId);
        Assert.Equal(own.Id, (await db.RoutineAssignments.AsNoTracking().SingleAsync(a => a.Id == personal.AssignmentId)).PatientId);
        var before = await db.ExerciseRoutines.CountAsync(); var assignmentsBefore = await db.RoutineAssignments.CountAsync();
        await Assert.ThrowsAsync<NotFoundException>(() => Send(new CloneExerciseRoutineCommand(source.Id, new(PatientId: foreign.Id))));
        Assert.Equal(before, await db.ExerciseRoutines.CountAsync()); Assert.Equal(assignmentsBefore, await db.RoutineAssignments.CountAsync());
        actor.IsAdmin.Returns(true);
        Assert.NotNull((await Send(new CloneExerciseRoutineCommand(source.Id, new(PatientId: foreign.Id)))).AssignmentId);
        actor.HasPermissionAsync("Wellness.Manage", Arg.Any<CancellationToken>()).Returns(false);
        await Assert.ThrowsAsync<ForbiddenException>(() => Send(new CloneExerciseRoutineCommand(source.Id, new())));
        actor.HasPermissionAsync("Wellness.Manage", Arg.Any<CancellationToken>()).Returns(true);
        var oldRequest = new UpdateExerciseRoutineRequest(source.Name, source.Description, source.Difficulty, source.EstimatedMinutes,
            source.Category, source.Status, source.TargetMuscles, source.Equipment, source.WarmupNotes, source.CooldownNotes, source.MediaId, null);
        Assert.Equal(source.Contraindications, (await Send(new UpdateExerciseRoutineCommand(source.Id, oldRequest)))!.Contraindications);
        Assert.Null((await Send(new UpdateExerciseRoutineCommand(source.Id, oldRequest with { Contraindications = "" })))!.Contraindications);
    }

    [NutritionPostgresFact]
    public async Task Contraindicaciones_MigracionAditivaDiezMilFilas_ReversaProtegeDatos()
    {
        await using var db = Context();
        var migrations = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        var current = migrations.Single(m => m.EndsWith("_AddExerciseRoutineContraindications"));
        var previous = migrations[Array.IndexOf(migrations, current) - 1]; var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(previous);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO app.exercise_routines (id,name,difficulty,category,status,created_at) SELECT gen_random_uuid(),'Volumen sintético',{(int)RoutineDifficulty.Moderado},{(int)RoutineCategory.Mixta},{(int)NutritionPlanStatus.Draft},now() FROM generate_series(1,10000)");
        await migrator.MigrateAsync(current);
        Assert.Equal(10000, await db.ExerciseRoutines.CountAsync()); Assert.False(await db.ExerciseRoutines.AnyAsync(r => r.Contraindications != null));
        var id = await db.ExerciseRoutines.Select(r => r.Id).FirstAsync();
        await db.ExerciseRoutines.Where(r => r.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Contraindications, "Conservar advertencia"));
        await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync(previous));
        Assert.Equal("Conservar advertencia", await db.ExerciseRoutines.Where(r => r.Id == id).Select(r => r.Contraindications).SingleAsync());
        await db.ExerciseRoutines.Where(r => r.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Contraindications, (string?)null));
        await migrator.MigrateAsync(previous); await migrator.MigrateAsync(current);
        Assert.Equal(10000, await db.ExerciseRoutines.CountAsync());
    }
}
