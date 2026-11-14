using CoppAddresd.Application.Features.Nutrition.Commands;
using CoppAddresd.Application.Features.ProgramProgress.DTOs.Nutrition;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Repositories;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NSubstitute;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    [NutritionPostgresFact]
    public async Task EjemplosCiclo_UnicosConcurrentes_CorreccionViva_NotaProtegida_YAlcanceCicloYRegistro()
    {
        await using var db = Context();
        await db.GetService<IMigrator>().MigrateAsync("20261114193738_AddNutritionIntakeDayReviews"); await db.GetService<IMigrator>().MigrateAsync();
        var seed = await EffectiveSeedAsync(db); var other = await PatientAsync(db); var foreign = await PatientAsync(db);
        var owner = await db.PatientProfiles.Where(p => p.Id == seed.Patient).Select(p => p.UserId!.Value).SingleAsync();
        var professional = NutritionProfessional(seed.Patient); db.Professionals.Add(professional);
        db.PatientProfessionalAssignments.AddRange(new() { PatientId = seed.Patient, ProfessionalId = professional.Id },
            new() { PatientId = other.Id, ProfessionalId = professional.Id }); await db.SaveChangesAsync();
        var plan = await CycleAdherencePlanAsync(db, seed.Patient, seed.Start, new(2026, 11, 2, 15, 0, 0, DateTimeKind.Utc));
        var otherPlan = await CycleAdherencePlanAsync(db, other.Id, seed.Start, new(2026, 11, 2, 15, 0, 0, DateTimeKind.Utc));
        var foreignPlan = await CycleAdherencePlanAsync(db, foreign.Id, seed.Start, new(2026, 11, 2, 15, 0, 0, DateTimeKind.Utc));
        var body = new NutritionIntakePayload(100, null, null, null, null, null, "manual", null);
        await EffectiveRepository(db).LogNutritionAsync(seed.Patient, MealCode.des, seed.Start, body, owner, "highlight", default);
        await AddCycleIntakesAsync(db, other.Id, seed.Start, MealCode.des); await AddCycleIntakesAsync(db, foreign.Id, seed.Start, MealCode.des);
        var intake = await db.NutritionIntakeLogs.Where(l => l.PatientId == seed.Patient).Select(l => l.Id).SingleAsync();
        var otherIntake = await db.NutritionIntakeLogs.Where(l => l.PatientId == other.Id).Select(l => l.Id).SingleAsync();
        var foreignIntake = await db.NutritionIntakeLogs.Where(l => l.PatientId == foreign.Id).Select(l => l.Id).SingleAsync();
        var actor = Substitute.For<IPatientScopeActor>(); actor.UserId.Returns(owner);
        actor.ProfessionalIdAsync(Arg.Any<CancellationToken>()).Returns(professional.Id);
        actor.HasPermissionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var clock = new EffectiveClock(new(2026, 11, 7, 18, 0, 0, DateTimeKind.Utc));
        IntakeExamplesHandler Handler(CoppAddresd.Infrastructure.Persistence.AppDbContext context)
            => new(new PatientScopeGuard(actor, new PatientScopeStore(context)), actor, new NutritionCycleHighlightStore(context), new NutritionCycleReadStore(context), clock);
        async Task Pin() { await using var context = Context(); await Handler(context).Handle(new PinIntakeExample(plan.CycleId, intake, "Ejemplo de desayuno"), default); }
        await Task.WhenAll(Pin(), Pin()); Assert.Equal(1, await db.Set<NutritionCycleHighlight>().CountAsync());
        var handler = Handler(db); var first = Assert.Single((await handler.Handle(new GetIntakeExamples(plan.CycleId), default)).Items);
        Assert.Equal(100m, first.Recorded.Calories); Assert.Equal(owner, first.PinnedBy); Assert.Equal("Ejemplo de desayuno", first.Note);
        await EffectiveRepository(db).UpdateNutritionIntakeAsync(seed.Patient, MealCode.des, seed.Start, body with { Calories = 125 }, owner, default);
        var corrected = Assert.Single((await handler.Handle(new GetIntakeExamples(plan.CycleId), default)).Items);
        Assert.Equal(125m, corrected.Recorded.Calories); Assert.Equal(first.PinnedAt, corrected.PinnedAt); Assert.Equal(intake, corrected.IntakeLogId);
        actor.HasPermissionAsync("Nutrition.NotesView", Arg.Any<CancellationToken>()).Returns(false);
        Assert.Null(Assert.Single((await handler.Handle(new GetIntakeExamples(plan.CycleId), default)).Items).Note);
        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(new PinIntakeExample(plan.CycleId, otherIntake), default));
        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(new UnpinIntakeExample(otherPlan.CycleId, intake), default));
        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(new PinIntakeExample(plan.CycleId, foreignIntake), default));
        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(new GetIntakeExamples(foreignPlan.CycleId), default));
        Assert.Equal(1, await db.Set<NutritionCycleHighlight>().CountAsync());
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO app.nutrition_cycle_highlights SELECT * FROM app.nutrition_cycle_highlights WHERE cycle_id = {plan.CycleId}
            """));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(new PinIntakeExample(plan.CycleId, intake, new string('x', 2001)), default));
        Assert.Empty((await handler.Handle(new GetIntakeExamples(plan.CycleId, Page: 2), default)).Items);
        await handler.Handle(new UnpinIntakeExample(plan.CycleId, intake), default); await handler.Handle(new UnpinIntakeExample(plan.CycleId, intake), default);
        Assert.Equal(0, (await handler.Handle(new GetIntakeExamples(plan.CycleId), default)).Total);
        actor.IsAdmin.Returns(true); await handler.Handle(new PinIntakeExample(foreignPlan.CycleId, foreignIntake), default);
        Assert.Single((await handler.Handle(new GetIntakeExamples(foreignPlan.CycleId), default)).Items);
        actor.HasPermissionAsync("Nutrition.Manage", Arg.Any<CancellationToken>()).Returns(false);
        await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(new UnpinIntakeExample(foreignPlan.CycleId, foreignIntake), default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.Handle(new GetIntakeExamples(plan.CycleId), cancelled.Token));
        await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync("20261114193738_AddNutritionIntakeDayReviews"));
        Assert.Equal(1, await db.Set<NutritionCycleHighlight>().CountAsync());
    }
}
