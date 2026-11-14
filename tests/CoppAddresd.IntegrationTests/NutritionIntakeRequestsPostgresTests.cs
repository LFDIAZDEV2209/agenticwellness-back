using CoppAddresd.Application.Features.ProgramProgress.DTOs.Nutrition;
using CoppAddresd.Domain.Entities.FoodAi;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    private static FoodAnalysis IntakeAnalysis(Guid? owner) => new() { Id = Guid.NewGuid(), AnalysisId = Guid.NewGuid(),
        UserId = owner, ImageKey = "foodai/prueba.jpg", DetectorVersion = "test", SegmenterVersion = "test", ClassifierVersion = "test", PortionMethod = "test" };

    [NutritionPostgresFact]
    public async Task FotoIntake_PropiedadEstricta_ReintentoSinDuplicar_NiDeshacerCorreccion_YLegadoSinClave()
    {
        await using var db = Context(); var seed = await EffectiveSeedAsync(db); var other = await PatientAsync(db);
        var owner = await db.PatientProfiles.Where(p => p.Id == seed.Patient).Select(p => p.UserId!.Value).SingleAsync();
        var own = IntakeAnalysis(owner); var foreign = IntakeAnalysis(other.UserId); var anonymous = IntakeAnalysis(null);
        db.FoodAnalyses.AddRange(own, foreign, anonymous);
        db.HabitTemplates.Add(new() { Id = Guid.NewGuid(), Code = "alm", Name = "Almuerzo" }); await db.SaveChangesAsync();
        var repo = EffectiveRepository(db); var body = new NutritionIntakePayload(100m, 5m, 10m, 4m, 2m, null, "ai_photo", own.AnalysisId);
        foreach (var denied in new[] { foreign.AnalysisId, anonymous.AnalysisId, Guid.NewGuid() })
            await Assert.ThrowsAsync<NotFoundException>(() => repo.LogNutritionAsync(seed.Patient, MealCode.des, seed.Start, body with { FoodAnalysisId = denied }, owner, "invalid", default));
        Assert.Empty(await db.HabitChecks.ToArrayAsync()); Assert.Empty(await db.NutritionIntakeRequests.ToArrayAsync()); Assert.Empty(await db.XpLedgerEntries.ToArrayAsync());
        var saved = await repo.LogNutritionAsync(seed.Patient, MealCode.des, seed.Start, body, owner, "photo-key", default);
        var xp = await db.XpLedgerEntries.CountAsync();
        var replay = await repo.LogNutritionAsync(seed.Patient, MealCode.des, seed.Start, body, owner, "photo-key", default);
        Assert.Equal(saved.HabitCheckId, replay.HabitCheckId); Assert.Equal(0, replay.XpAwarded);
        Assert.Equal(own.AnalysisId, (await db.NutritionIntakeLogs.AsNoTracking().SingleAsync()).FoodAnalysisId);
        Assert.Equal(1, await db.NutritionIntakeRequests.CountAsync()); Assert.Equal(xp, await db.XpLedgerEntries.CountAsync());
        await Assert.ThrowsAsync<BusinessRuleViolationException>(() => repo.LogNutritionAsync(seed.Patient, MealCode.des, seed.Start, body with { Calories = 101m }, owner, "photo-key", default));
        await Assert.ThrowsAsync<NotFoundException>(() => repo.UpdateNutritionIntakeAsync(seed.Patient, MealCode.des, seed.Start, body with { FoodAnalysisId = foreign.AnalysisId }, owner, default));
        await repo.UpdateNutritionIntakeAsync(seed.Patient, MealCode.des, seed.Start, body with { Calories = 125m, Source = "manual" }, owner, default);
        await repo.LogNutritionAsync(seed.Patient, MealCode.des, seed.Start, body, owner, "photo-key", default);
        Assert.Equal(125m, (await db.NutritionIntakeLogs.AsNoTracking().SingleAsync()).Calories);
        Assert.Equal(xp, await db.XpLedgerEntries.CountAsync());
        await repo.LogNutritionAsync(seed.Patient, MealCode.alm, seed.Start, null, owner, default);
        await Assert.ThrowsAsync<BusinessRuleViolationException>(() => repo.LogNutritionAsync(seed.Patient, MealCode.alm, seed.Start, null, owner, default));
        Assert.Equal(2, await db.NutritionIntakeLogs.CountAsync());
    }

    [NutritionPostgresFact]
    public async Task FotoIntake_ConcurrenteUnaOperacion_AguaAdmiteVariasClaves_YReversaProtegida()
    {
        await using var db = Context(); var seed = await EffectiveSeedAsync(db);
        var migrator = db.GetService<IMigrator>(); await migrator.MigrateAsync("20261114174950_AddNutritionCycleInterruption"); await migrator.MigrateAsync();
        var owner = await db.PatientProfiles.Where(p => p.Id == seed.Patient).Select(p => p.UserId!.Value).SingleAsync();
        var body = new NutritionIntakePayload(100m, null, null, null, null, null, "manual", null);
        async Task<Guid> Send()
        { await using var context = Context(); return (await EffectiveRepository(context).LogNutritionAsync(seed.Patient, MealCode.des, seed.Start, body, owner, "concurrent", default)).HabitCheckId; }
        var results = await Task.WhenAll(Send(), Send()); Assert.Equal(results[0], results[1]);
        Assert.Equal(1, await db.NutritionIntakeLogs.CountAsync()); Assert.Equal(1, await db.NutritionIntakeRequests.CountAsync()); Assert.Equal(1, await db.XpLedgerEntries.CountAsync());
        db.HabitTemplates.Add(new() { Id = Guid.NewGuid(), Code = "agua", Name = "Agua", Category = "hidratacion" }); await db.SaveChangesAsync();
        var repo = EffectiveRepository(db); var water = body with { Calories = null, WaterMl = 250 };
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION app.reject_intake_request() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'test rollback'; END $$; CREATE TRIGGER reject_intake_request BEFORE INSERT ON app.nutrition_intake_requests FOR EACH ROW EXECUTE FUNCTION app.reject_intake_request();");
        await Assert.ThrowsAsync<DbUpdateException>(() => repo.LogNutritionAsync(seed.Patient, MealCode.agua, seed.Start, water, owner, "water-1", default));
        Assert.Equal(1, await db.NutritionIntakeLogs.CountAsync()); Assert.Equal(1, await db.XpLedgerEntries.CountAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_intake_request ON app.nutrition_intake_requests; DROP FUNCTION app.reject_intake_request();");
        await repo.LogNutritionAsync(seed.Patient, MealCode.agua, seed.Start, water, owner, "water-1", default);
        await repo.LogNutritionAsync(seed.Patient, MealCode.agua, seed.Start, water with { WaterMl = 500 }, owner, "water-2", default);
        await repo.LogNutritionAsync(seed.Patient, MealCode.agua, seed.Start, water, owner, "water-1", default);
        Assert.Equal(500, (await db.NutritionIntakeLogs.AsNoTracking().SingleAsync(l => l.MealCode == MealCode.agua)).WaterMl);
        Assert.Equal(3, await db.NutritionIntakeRequests.CountAsync()); Assert.Equal(2, await db.XpLedgerEntries.CountAsync());
        await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync("20261114174950_AddNutritionCycleInterruption"));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repo.LogNutritionAsync(seed.Patient, MealCode.des, seed.Start, body, owner, "cancelled", cancelled.Token));
        Assert.Equal(3, await db.NutritionIntakeRequests.CountAsync());
    }
}
