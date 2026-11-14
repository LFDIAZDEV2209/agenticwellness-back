using CoppAddresd.Application.Features.Nutrition.Cases;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    [NutritionPostgresFact]
    public async Task CicloFechas_PublicacionTardia_NumeraSoloPublicados_YVersionConservaFechas()
    {
        await using var db = Context(); var patient = await PatientAsync(db); var actor = NutritionAdmin;
        var initial = Cases(db, time: new EffectiveClock(new(2026, 11, 5, 15, 0, 0, DateTimeKind.Utc)));
        async Task<NutritionView> GenerateApprove(NutritionCases cases)
        {
            var p = await cases.ExecuteAsync(new(NutritionOperation.Generate, patient.Id, null, null, actor, CompleteNutritionInput), default);
            p = await cases.ExecuteAsync(Mutation(p, NutritionOperation.Submit, actor), default);
            return await cases.ExecuteAsync(Mutation(p, NutritionOperation.Approve, actor, confirm: true), default);
        }
        var first = await GenerateApprove(initial);
        Assert.Equal(new DateOnly(2026, 11, 5), first.StartDate); Assert.Equal(new DateOnly(2026, 12, 4), first.EndDate);
        // Un borrador abandonado no consume la numeración del paciente.
        var unused = await initial.ExecuteAsync(new(NutritionOperation.Generate, patient.Id, null, null, actor, CompleteNutritionInput), default);
        var version = await initial.ExecuteAsync(Mutation(first, NutritionOperation.CreateVersion, actor), default);
        version = await initial.ExecuteAsync(Mutation(version, NutritionOperation.Submit, actor), default);
        version = await initial.ExecuteAsync(Mutation(version, NutritionOperation.Approve, actor, confirm: true), default);
        Assert.Equal(first.StartDate, version.StartDate); Assert.Equal(first.EndDate, version.EndDate);
        var late = Cases(db, time: new EffectiveClock(new(2026, 12, 9, 15, 0, 0, DateTimeKind.Utc)));
        var second = await GenerateApprove(late);
        Assert.Equal(new DateOnly(2026, 12, 9), second.StartDate); Assert.Equal(new DateOnly(2027, 1, 7), second.EndDate);
        Assert.Equal(1, (await db.NutritionCycles.AsNoTracking().SingleAsync(c => c.Id == first.CycleId)).Number);
        Assert.Equal(2, (await db.NutritionCycles.AsNoTracking().SingleAsync(c => c.Id == second.CycleId)).Number);
        Assert.Null((await db.NutritionCycles.AsNoTracking().SingleAsync(c => c.Id == unused.CycleId)).Number);
    }

    [NutritionPostgresFact]
    public async Task CicloFechas_RetiroInterrumpeSoloVigente_YFalloRevierteInscripcion()
    {
        await using var db = Context(); var seed = await EffectiveSeedAsync(db); var other = await PatientAsync(db);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261114171403_AddNutritionCycles"); await migrator.MigrateAsync();
        var start = new DateOnly(2026, 11, 5); var actor = other.UserId!.Value;
        db.NutritionPrescriptions.AddRange(new NutritionPrescription { PatientId = seed.Patient, State = "aprobado", IsPublished = true,
            StartDate = start, EndDate = start.AddDays(29), ApprovedBy = actor, ApprovedAt = start.ToDateTime(new(15, 0), DateTimeKind.Utc) },
            new NutritionPrescription { PatientId = other.Id, State = "aprobado", IsPublished = true, StartDate = start,
                EndDate = start.AddDays(29), ApprovedBy = actor, ApprovedAt = start.ToDateTime(new(15, 0), DateTimeKind.Utc) });
        await db.SaveChangesAsync();
        var history = (await db.ProgramWeeks.AsNoTracking().OrderBy(w => w.Id).Select(w => w.TasksSnapshot).ToArrayAsync())
            .Select(s => s.GetRawText()).ToArray();
        var repository = new ProgramRepository(db, new ConfigurationBuilder().Build(), clock: new EffectiveClock(new(2026, 12, 6, 15, 0, 0, DateTimeKind.Utc)));
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION app.reject_cycle_retirement() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'test rollback'; END $$; CREATE TRIGGER reject_cycle_retirement BEFORE UPDATE ON app.nutrition_cycles FOR EACH ROW EXECUTE FUNCTION app.reject_cycle_retirement();");
        var failure = await Assert.ThrowsAsync<PostgresException>(() => repository.WithdrawAsync(seed.Enrollment, actor, default));
        Assert.Equal("P0001", failure.SqlState);
        Assert.Equal(ProgramEnrollmentStatus.Active, (await db.ProgramEnrollments.AsNoTracking().SingleAsync()).Status);
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_cycle_retirement ON app.nutrition_cycles; DROP FUNCTION app.reject_cycle_retirement();");
        await repository.WithdrawAsync(seed.Enrollment, actor, default);
        Assert.Equal("interrumpido", (await db.NutritionCycles.AsNoTracking().SingleAsync(c => c.PatientId == seed.Patient)).State);
        Assert.NotNull((await db.NutritionCycles.AsNoTracking().SingleAsync(c => c.PatientId == seed.Patient)).InterruptedAt);
        await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync("20261114171403_AddNutritionCycles"));
        Assert.Equal("en_curso", (await db.NutritionCycles.AsNoTracking().SingleAsync(c => c.PatientId == other.Id)).State);
        Assert.Equal(history, (await db.ProgramWeeks.AsNoTracking().OrderBy(w => w.Id).Select(w => w.TasksSnapshot).ToArrayAsync())
            .Select(s => s.GetRawText()).ToArray());
        Assert.Empty(await db.TaskCompletions.ToArrayAsync());
    }
}
