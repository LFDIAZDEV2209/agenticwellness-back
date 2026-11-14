using CoppAddresd.Application.Features.Nutrition.Queries.GetMyNutritionPlan;
using CoppAddresd.Application.Features.ProgramProgress.Queries.Erp;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    [NutritionPostgresFact]
    public async Task NutricionContinuidad_VersionHistoricaMisionApp_SucesorTardio_MotorSiFaltaMenu_YHuecosCero()
    {
        await using var db = Context(); var seed = await EffectiveSeedAsync(db);
        var patient = await db.PatientProfiles.AsNoTracking().SingleAsync(p => p.Id == seed.Patient);
        var first = await CycleAdherencePlanAsync(db, seed.Patient, new(2026, 11, 5), new(2026, 11, 5, 15, 0, 0, DateTimeKind.Utc), active: false);
        var last = await CycleAdherencePlanAsync(db, seed.Patient, new(2026, 11, 5), new(2026, 11, 8, 2, 0, 0, DateTimeKind.Utc), first.CycleId, 2, 4);
        var next = await CycleAdherencePlanAsync(db, seed.Patient, new(2026, 12, 9), new(2026, 12, 9, 15, 0, 0, DateTimeKind.Utc));
        var actor = Substitute.For<IPatientScopeActor>(); actor.UserId.Returns(Guid.NewGuid()); actor.IsAdmin.Returns(true);
        actor.HasPermissionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var store = new EffectiveDayContentStore(db, new PatientScopeGuard(actor, new PatientScopeStore(db)));
        var resolver = new EffectiveDayContentResolver(store);
        var bridge = new ProgramContentResolver(new WellnessRepository(db), NullLogger<ProgramContentResolver>.Instance,
            new NutritionPrescriptionRepository(db), store, resolver);
        var calendar = await bridge.ResolveRangeAsync(seed.Patient, new(2026, 11, 6), new(2026, 11, 8), default);
        Assert.Equal(first.Id, calendar[new(2026, 11, 6)].SourcePrescriptionId);
        Assert.Equal(last.Id, calendar[new(2026, 11, 7)].SourcePrescriptionId);
        var mission = await EffectiveRepository(db).GetSnapshotAsync(seed.Enrollment, new(2026, 11, 6), default);
        Assert.Equal(first.Id, mission!.TodayTasks.Single(t => t.TaskCode == TaskCode.nut).Content!.SourcePrescriptionId);
        var patients = Substitute.For<IPatientRepository>(); patients.GetByUserIdAsync(patient.UserId!.Value, Arg.Any<CancellationToken>()).Returns(patient);
        async Task<Guid?> MyPlan(DateOnly day)
        {
            var result = await new GetMyNutritionPlanQueryHandler(patients, new WellnessRepository(db),
                NullLogger<GetMyNutritionPlanQueryHandler>.Instance, new NutritionPrescriptionRepository(db),
                new EffectiveClock(day.ToDateTime(new(18, 0), DateTimeKind.Utc)), bridge).Handle(new(patient.UserId.Value), default);
            return result?.Id;
        }
        Assert.Equal(first.Id, await MyPlan(new(2026, 11, 6)));
        Assert.Equal(last.Id, await MyPlan(new(2026, 12, 6)));
        var continuity = await resolver.ResolveAsync(seed.Enrollment, new(2026, 12, 6), default);
        Assert.Equal(new EffectiveContentRef(last.Id, "continuity", 30, true), continuity.Nutrition);
        Assert.Equal(next.Id, (await resolver.ResolveAsync(seed.Enrollment, new(2026, 12, 9), default)).Nutrition.Id);
        var clock = new EffectiveClock(new(2026, 12, 6, 18, 0, 0, DateTimeKind.Utc));
        var gaps = await new GetContentGapsQueryHandler(store, resolver, clock).Handle(new(1, 100), default);
        Assert.Equal(0, gaps.Total);
        await db.NutritionPrescriptionMeals.Where(m => m.PrescriptionId == last.Id && m.DayNumber == 30).ExecuteDeleteAsync();
        var fallback = await resolver.ResolveAsync(seed.Enrollment, new(2026, 12, 6), default);
        Assert.Equal(new EffectiveContentRef(seed.Plan, "engine", 7), fallback.Nutrition);
        Assert.Equal(seed.Plan, await MyPlan(new(2026, 12, 6)));
        Assert.Equal(0, (await new GetContentGapsQueryHandler(store, resolver, clock).Handle(new(1, 100), default)).Total);
    }
}
