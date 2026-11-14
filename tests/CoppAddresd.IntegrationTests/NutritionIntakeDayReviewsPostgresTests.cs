using CoppAddresd.Application.Features.Nutrition.Commands;
using CoppAddresd.Application.Features.ProgramProgress.DTOs.Nutrition;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services;
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
    public async Task RevisionDiaria_CorreccionReabre_ReintentoNo_ContadorSoloPropios_YAjenoDenegado()
    {
        await using var db = Context();
        await db.GetService<IMigrator>().MigrateAsync("20261114184553_AddNutritionIntakeRequests");
        await db.GetService<IMigrator>().MigrateAsync();
        var seed = await EffectiveSeedAsync(db); var other = await PatientAsync(db);
        var owner = await db.PatientProfiles.Where(p => p.Id == seed.Patient).Select(p => p.UserId!.Value).SingleAsync();
        var professional = NutritionProfessional(seed.Patient); db.Professionals.Add(professional);
        db.PatientProfessionalAssignments.Add(new() { PatientId = seed.Patient, ProfessionalId = professional.Id }); await db.SaveChangesAsync();
        var plan = await CycleAdherencePlanAsync(db, seed.Patient, seed.Start, new(2026, 11, 2, 15, 0, 0, DateTimeKind.Utc));
        var foreign = await CycleAdherencePlanAsync(db, other.Id, seed.Start, new(2026, 11, 2, 15, 0, 0, DateTimeKind.Utc));
        var actor = Substitute.For<IPatientScopeActor>(); actor.UserId.Returns(owner);
        actor.ProfessionalIdAsync(Arg.Any<CancellationToken>()).Returns(professional.Id);
        actor.HasPermissionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var scope = new PatientScopeGuard(actor, new PatientScopeStore(db));
        var store = new NutritionIntakeDayReviewStore(db, scope);
        var handler = new IntakeDayReviewHandler(scope, actor, store, new NutritionCycleReadStore(db), new EffectiveClock(new(2026, 11, 7, 18, 0, 0, DateTimeKind.Utc)));
        var repo = EffectiveRepository(db); var body = new NutritionIntakePayload(100, null, null, null, null, null, "manual", null);
        await repo.LogNutritionAsync(seed.Patient, MealCode.des, seed.Start, body, owner, "review-test", default);
        await AddCycleIntakesAsync(db, other.Id, seed.Start, MealCode.des);
        Assert.Equal(1, (await handler.Handle(new GetUnreviewedIntakeDays(), default)).Days);
        var reviewed = await handler.Handle(new MarkIntakeDayReviewed(plan.CycleId, seed.Start), default);
        Assert.False(reviewed.Unreviewed); Assert.Equal(owner, reviewed.ReviewedBy); Assert.NotNull(reviewed.ReviewedAt);
        Assert.Equal(0, (await handler.Handle(new GetUnreviewedIntakeDays(), default)).Days);
        var intakeBefore = await db.NutritionIntakeLogs.AsNoTracking().SingleAsync(l => l.PatientId == seed.Patient);
        await repo.LogNutritionAsync(seed.Patient, MealCode.des, seed.Start, body, owner, "review-test", default);
        await repo.UpdateNutritionIntakeAsync(seed.Patient, MealCode.des, seed.Start, body, owner, default);
        Assert.Equal(intakeBefore.UpdatedAt, (await db.NutritionIntakeLogs.AsNoTracking().SingleAsync(l => l.PatientId == seed.Patient)).UpdatedAt);
        Assert.False((await handler.Handle(new GetIntakeDayReviews(plan.CycleId, seed.Start, seed.Start), default)).Single().Unreviewed);
        await repo.UpdateNutritionIntakeAsync(seed.Patient, MealCode.des, seed.Start, body with { Calories = 125 }, owner, default);
        var changed = (await handler.Handle(new GetIntakeDayReviews(plan.CycleId, seed.Start, seed.Start), default)).Single();
        Assert.True(changed.Unreviewed); Assert.True(changed.ModifiedSinceReview);
        Assert.Equal(1, (await handler.Handle(new GetUnreviewedIntakeDays(), default)).Days);
        await handler.Handle(new MarkIntakeDayReviewed(plan.CycleId, seed.Start), default);
        await handler.Handle(new MarkIntakeDayReviewed(plan.CycleId, seed.Start), default);
        Assert.Equal(1, await db.NutritionIntakeDayReviews.CountAsync());
        Assert.False((await handler.Handle(new GetIntakeDayReviews(plan.CycleId, seed.Start, seed.Start), default)).Single().Unreviewed);
        Assert.True((await handler.Handle(new UnmarkIntakeDayReviewed(plan.CycleId, seed.Start), default)).Unreviewed);
        await handler.Handle(new UnmarkIntakeDayReviewed(plan.CycleId, seed.Start), default);
        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(new MarkIntakeDayReviewed(foreign.CycleId, seed.Start), default));
        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(new UnmarkIntakeDayReviewed(foreign.CycleId, seed.Start), default));
        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(new GetIntakeDayReviews(foreign.CycleId, seed.Start, seed.Start), default));
        await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(new MarkIntakeDayReviewed(plan.CycleId, seed.Start.AddDays(-1)), default));
        await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(new MarkIntakeDayReviewed(plan.CycleId, seed.Start.AddDays(20)), default));
        actor.IsAdmin.Returns(true); Assert.Equal(2, (await handler.Handle(new GetUnreviewedIntakeDays(), default)).Days);
        await handler.Handle(new MarkIntakeDayReviewed(foreign.CycleId, seed.Start), default);
        Assert.Equal(1, (await handler.Handle(new GetUnreviewedIntakeDays(), default)).Days);
        actor.HasPermissionAsync("Nutrition.Manage", Arg.Any<CancellationToken>()).Returns(false);
        await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(new UnmarkIntakeDayReviewed(foreign.CycleId, seed.Start), default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.Handle(new GetUnreviewedIntakeDays(), cancelled.Token));
        await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync("20261114184553_AddNutritionIntakeRequests"));
        Assert.Equal(1, await db.NutritionIntakeDayReviews.CountAsync());
    }
}
