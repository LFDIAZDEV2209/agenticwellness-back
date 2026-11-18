using CoppAddresd.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    [NutritionPostgresFact]
    public async Task VitaA_Migracion_UpDownUpConservaFilasYExigeIdempotencia()
    {
        const string previous = "20261118173357_AddMedicationMonitoring";
        const string migration = "20261118201312_AddVitaClinicalWriteModel";
        const int baselineCount = 2500;
        Assert.StartsWith("copp_nutrition_test_", new NpgsqlConnectionStringBuilder(connection).Database);
        await using var db = VitaDb();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(previous);
        Assert.Equal(previous, (await db.Database.GetAppliedMigrationsAsync()).Last());
        var patient = await PatientAsync(db);
        var other = await PatientAsync(db);
        var (metric, unit) = await VitaAMetricAsync(db, "weight", "kg", "kg", "body_comp");
        var now = VitaANow.UtcDateTime;
        var oldId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO app.clinical_measurements (id, patient_id, metric_id, unit_id, value, observed_at, source)
            SELECT CASE WHEN n = 1 THEN {oldId} ELSE gen_random_uuid() END,
                {patient.Id}, {metric}, {unit}, 74.50, {now}, 'patient'
            FROM generate_series(1, {baselineCount}) AS n
            """);

        await migrator.MigrateAsync(migration);
        Assert.Equal(migration, (await db.Database.GetAppliedMigrationsAsync()).Last());
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(baselineCount, await db.ClinicalMeasurements.CountAsync(m => m.Status == "confirmed"
            && m.SourceChannel == null && m.ReportedBy == null && m.IdempotencyKey == null));

        var confirmed = VitaAReading(patient.Id, metric, unit, now, 74.75m, "patient");
        confirmed.SourceChannel = "vita_chat";
        confirmed.ReportedBy = "patient";
        confirmed.IdempotencyKey = "synthetic-turn";
        confirmed.SupersedesId = oldId;
        var separatePatient = VitaAReading(other.Id, metric, unit, now, 79m, "patient");
        separatePatient.IdempotencyKey = confirmed.IdempotencyKey;
        db.ClinicalMeasurements.AddRange(confirmed, separatePatient);

        VitaWriteReceipt Receipt(Guid patientId, string channel) => new()
        {
            PatientId = patientId, Channel = channel, TurnId = "synthetic-turn", ToolName = "record_weight",
            RequestHash = new string('a', 64), EntityType = "clinical_measurement", EntityId = confirmed.Id,
        };
        db.VitaWriteReceipts.AddRange(Receipt(patient.Id, "vita_chat"), Receipt(patient.Id, "vita_voice"),
            Receipt(other.Id, "vita_chat"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(oldId, (await db.ClinicalMeasurements.SingleAsync(x => x.Id == confirmed.Id)).SupersedesId);
        Assert.Equal(3, await db.VitaWriteReceipts.CountAsync());

        var repeatedMeasurement = VitaAReading(patient.Id, metric, unit, now, 75m, "patient");
        repeatedMeasurement.IdempotencyKey = confirmed.IdempotencyKey;
        db.ClinicalMeasurements.Add(repeatedMeasurement);
        var uniqueMeasurement = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("ux_clinical_measurements_patient_idempotency",
            Assert.IsType<PostgresException>(uniqueMeasurement.InnerException).ConstraintName);
        db.ChangeTracker.Clear();

        db.VitaWriteReceipts.Add(Receipt(patient.Id, "vita_chat"));
        var uniqueReceipt = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("ux_vita_write_receipts_patient_channel_turn_tool",
            Assert.IsType<PostgresException>(uniqueReceipt.InnerException).ConstraintName);
        db.ChangeTracker.Clear();

        var noPreviousMeasurement = VitaAReading(patient.Id, metric, unit, now, 75m, "patient");
        noPreviousMeasurement.SupersedesId = Guid.NewGuid();
        db.ClinicalMeasurements.Add(noPreviousMeasurement);
        var fk = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(fk.InnerException).SqlState);
        db.ChangeTracker.Clear();

        await migrator.MigrateAsync(previous);
        Assert.Equal(previous, (await db.Database.GetAppliedMigrationsAsync()).Last());
        Assert.False(await db.Database.SqlQueryRaw<bool>(
            """SELECT to_regclass('app.vita_write_receipts') IS NOT NULL AS "Value" """).SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("""
            SELECT count(*)::int AS "Value" FROM information_schema.columns
            WHERE table_schema = 'app' AND table_name = 'clinical_measurements'
            AND column_name IN ('source_channel', 'reported_by', 'status', 'supersedes_id', 'idempotency_key')
            """).SingleAsync());
        Assert.Equal(baselineCount + 2, await db.Database.SqlQueryRaw<int>(
            """SELECT count(*)::int AS "Value" FROM app.clinical_measurements""").SingleAsync());

        await migrator.MigrateAsync(migration);
        Assert.Equal(migration, (await db.Database.GetAppliedMigrationsAsync()).Last());
        Assert.Equal(74.50m, (await db.ClinicalMeasurements.SingleAsync(m => m.Id == oldId)).Value);
        Assert.Equal(baselineCount + 2, await db.ClinicalMeasurements.CountAsync(m => m.Status == "confirmed"));
        Assert.Empty(await db.VitaWriteReceipts.ToListAsync());
    }
}
