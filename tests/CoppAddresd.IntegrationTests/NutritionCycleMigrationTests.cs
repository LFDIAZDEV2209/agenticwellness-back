using CoppAddresd.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    [NutritionPostgresFact]
    public async Task Ciclos_MigracionRellenaCuatro_VolumenFKYReversaSinCambiarPrescripciones()
    {
        await using var db = Context(); var patient = await PatientAsync(db);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261114141434_AddExerciseSessionDetailSnapshot");
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO app.nutrition_prescriptions
              (id,patient_id,cycle_id,version,revision,state,provisional,is_published,menus_outdated,manual_edits_confirmed,
               start_date,end_date,created_by,created_at,approved_by,approved_at)
            SELECT gen_random_uuid(),{patient.Id},md5('cycle-' || (n % 4))::uuid,n / 4 + 1,1,
              CASE WHEN n < 4 THEN 'aprobado' ELSE 'borrador_ia' END,false,n < 4,false,false,
              DATE '2026-01-01' + (n % 4) * 30,DATE '2026-01-30' + (n % 4) * 30,
              {patient.UserId!.Value},TIMESTAMPTZ '2026-01-01 12:00Z',
              CASE WHEN n < 4 THEN {patient.UserId.Value} END,
              CASE WHEN n < 4 THEN TIMESTAMPTZ '2026-01-01 12:00Z' + (n % 4) * INTERVAL '30 days' END
            FROM generate_series(0,10003) n;
            """);
        var before = await db.NutritionPrescriptions.AsNoTracking().OrderBy(p => p.Id)
            .Select(p => new { p.Id, p.CycleId, p.PatientId, p.Version, p.State, p.StartDate, p.EndDate }).ToArrayAsync();
        await migrator.MigrateAsync();
        var cycles = await db.NutritionCycles.AsNoTracking().OrderBy(c => c.Number).ToArrayAsync();
        Assert.Equal(4, cycles.Length); Assert.Equal(new int?[] { 1, 2, 3, 4 }, cycles.Select(c => c.Number));
        Assert.All(cycles, c => { Assert.Equal("en_curso", c.State); Assert.Equal(patient.Id, c.PatientId); Assert.Equal(c.StartDate!.Value.AddDays(29), c.EndDate); });
        Assert.Equal(before, await db.NutritionPrescriptions.AsNoTracking().OrderBy(p => p.Id)
            .Select(p => new { p.Id, p.CycleId, p.PatientId, p.Version, p.State, p.StartDate, p.EndDate }).ToArrayAsync());
        var constraint = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM app.nutrition_cycles WHERE id = {cycles[0].Id}"));
        Assert.Equal("23001", constraint.SqlState);
        await migrator.MigrateAsync("20261114141434_AddExerciseSessionDetailSnapshot");
        Assert.Equal(10004, await db.NutritionPrescriptions.CountAsync());
        await migrator.MigrateAsync(); Assert.Equal(4, await db.NutritionCycles.CountAsync());
    }

    [NutritionPostgresFact]
    public async Task Ciclos_BorradoresSinNumero_PublicacionConcurrenteSinHuecos_YAjenoRechazado()
    {
        await using var db = Context(); var patient = await PatientAsync(db); var foreign = await PatientAsync(db);
        var drafts = Enumerable.Range(0, 3).Select(_ => new NutritionPrescription { PatientId = patient.Id, CreatedBy = patient.UserId!.Value }).ToArray();
        db.NutritionPrescriptions.AddRange(drafts); await db.SaveChangesAsync();
        Assert.All(await db.NutritionCycles.AsNoTracking().ToArrayAsync(), c =>
        { Assert.Equal("borrador", c.State); Assert.Null(c.Number); Assert.Null(c.StartDate); Assert.Null(c.EndDate); });
        async Task Publish(Guid id, DateOnly date)
        {
            await using var other = Context();
            await other.Database.ExecuteSqlInterpolatedAsync($"UPDATE app.nutrition_prescriptions SET state='aprobado',is_published=true,start_date={date},end_date={date.AddDays(29)},approved_by={patient.UserId!.Value},approved_at={DateTime.UtcNow} WHERE id={id}");
        }
        await Task.WhenAll(Publish(drafts[0].Id, new(2026, 1, 1)), Publish(drafts[1].Id, new(2026, 1, 31)));
        Assert.Equal(new int?[] { 1, 2 }, await db.NutritionCycles.Where(c => c.Number != null).OrderBy(c => c.Number).Select(c => c.Number).ToArrayAsync());
        Assert.Null((await db.NutritionCycles.AsNoTracking().SingleAsync(c => c.Id == drafts[2].CycleId)).Number);
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE app.nutrition_prescriptions SET patient_id={foreign.Id} WHERE id={drafts[0].Id}"));
        Assert.Equal("23503", error.SqlState);
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO app.nutrition_cycles (id,patient_id,number,start_date,end_date,state) VALUES ({Guid.NewGuid()},{patient.Id},1,DATE '2026-03-02',DATE '2026-03-31','en_curso')"));
        Assert.Equal("23505", duplicate.SqlState);
    }
}
