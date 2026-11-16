using System.Text.Json;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace CoppAddresd.IntegrationTests;

public sealed class ReleaseMigrationMergeFactAttribute : FactAttribute
{
    public ReleaseMigrationMergeFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COP_TEST_DB_CONNECTION"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COP_RELEASE_MIGRATION_INVENTORY")))
            Skip = "Preflight: requiere PostgreSQL aislado e inventario Git de las dos ramas.";
    }
}

public sealed partial class NutritionPlanWorkflowTests
{
    [ReleaseMigrationMergeFact]
    public async Task ReleaseMerge_MigracionesNuevasYSaltosDesdeAmbasRamas_NoColisionanNiPierdenModelo()
    {
        await using var db = Context();
        // Nunca usar una BD compartida para probar Down/Up ni reescribir su historial.
        Assert.StartsWith("copp_nutrition_test_", new NpgsqlConnectionStringBuilder(connection).Database);
        using var inventory = JsonDocument.Parse(await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("COP_RELEASE_MIGRATION_INVENTORY")!));
        static string[] Ids(JsonElement root, string side) => root.GetProperty(side).EnumerateArray()
            .Where(r => r.GetProperty("context").GetString() == "AppDbContext")
            .Select(r => r.GetProperty("id").GetString()!).Order().ToArray();
        var before = Ids(inventory.RootElement, "before"); var dev = Ids(inventory.RootElement, "dev");
        var expected = before.Union(dev).Order().ToArray();
        Assert.Equal(expected, db.Database.GetMigrations().ToArray()); // Metadata local, sin I/O.
        Assert.Equal(expected, (await db.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        var assembly = db.GetService<IMigrationsAssembly>();
        const string sos = "20261113195741_AddSosWhatsAppOutbox";
        var cumulativeModels = 0;
        foreach (var (id, type) in assembly.Migrations.Where(m => string.CompareOrdinal(m.Key, sos) >= 0))
        {
            var migration = assembly.CreateMigration(type, db.Database.ProviderName!);
            Assert.NotNull(migration.TargetModel.FindEntityType("CoppAddresd.Domain.Entities.SosWhatsAppDelivery"));
            cumulativeModels++;
        }
        var sentinel = new ExerciseRoutine { Id = Guid.NewGuid(), Name = "Sentinela merge", Contraindications = "Conservar F4", Status = NutritionPlanStatus.Active };
        db.ExerciseRoutines.Add(sentinel); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var migrator = db.GetService<IMigrator>();

        // Estado F4 previo: su historial completo, sin la nueva migración SOS (más antigua que su última ID).
        await db.Database.ExecuteSqlRawAsync("DROP TABLE app.sos_whatsapp_deliveries");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM public.\"__EFMigrationsHistory\" WHERE \"MigrationId\"={sos}");
        Assert.Equal(before, (await db.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.Equal(new[] { sos }, (await db.Database.GetPendingMigrationsAsync()).ToArray());
        await db.Database.MigrateAsync();
        Assert.Equal(expected, (await db.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.Equal("Conservar F4", (await db.ExerciseRoutines.AsNoTracking().SingleAsync(r => r.Id == sentinel.Id)).Contraindications);

        // Estado dev: cadena compartida + SOS + detalle 2.11, sin la columna exclusiva F4 previa al detalle.
        await migrator.MigrateAsync("20261114141434_AddExerciseSessionDetailSnapshot");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE app.exercise_routines DROP COLUMN contraindications");
        const string contraindications = "20261114122311_AddExerciseRoutineContraindications";
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM public.\"__EFMigrationsHistory\" WHERE \"MigrationId\"={contraindications}");
        Assert.Equal(dev, (await db.Database.GetAppliedMigrationsAsync()).ToArray());
        var f4Pending = expected.Except(dev).ToArray();
        Assert.Equal(f4Pending, (await db.Database.GetPendingMigrationsAsync()).ToArray());
        await db.Database.MigrateAsync();
        Assert.Equal(expected, (await db.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync()); Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal("Sentinela merge", (await db.ExerciseRoutines.AsNoTracking().SingleAsync(r => r.Id == sentinel.Id)).Name);
        Assert.True(await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('app.sos_whatsapp_deliveries') IS NOT NULL AS \"Value\"").SingleAsync());
        Assert.Equal(200, await db.Database.SqlQueryRaw<int>("SELECT character_maximum_length AS \"Value\" FROM information_schema.columns WHERE table_schema='app' AND table_name='patient_ai_briefs' AND column_name='model_name'").SingleAsync());
        var report = Environment.GetEnvironmentVariable("COP_RELEASE_EVIDENCE");
        if (!string.IsNullOrWhiteSpace(report))
            await File.WriteAllTextAsync(Path.Combine(report, "migration-runtime.json"), JsonSerializer.Serialize(new
            {
                isolatedDatabase = database, freshApplied = expected.Length, fromF4Pending = new[] { sos },
                fromDevPending = f4Pending, historicalModelsIncludingSos = cumulativeModels,
                appliedAfterBothUpgrades = expected, pending = 0, modelChanges = false,
                sentinelPreserved = true, sharedDatabaseWrites = 0,
            }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
