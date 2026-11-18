using System.Net.Http.Json;
using CoppAddresd.Application.Features.Agents.Vita;
using CoppAddresd.Application.Features.ProgramProgress.Events;
using CoppAddresd.Application.Features.Sos;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Infrastructure.Metrics;
using CoppAddresd.Infrastructure.Persistence;
using CoppAddresd.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NSubstitute;
using Xunit.Abstractions;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    private const string BeforeVitalsMigration = "20261118201312_AddVitaClinicalWriteModel";
    private const string VitalsMigration = "20261118214000_ConsolidateLegacyVitalSigns";
    private readonly ITestOutputHelper finalOutput;

    public NutritionPlanWorkflowTests(ITestOutputHelper finalOutput) => this.finalOutput = finalOutput;

    [NutritionPostgresFact]
    public async Task VitaFinal_Migration_ReconcilesLegacyAndPreservesReadersWritersAndRollback()
    {
        await using var db = Context();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(BeforeVitalsMigration);
        var patient = await PatientAsync(db);
        var now = VitaANow.UtcDateTime;
        db.VitalSigns.AddRange(
            new VitalSign { PatientId = patient.Id, MeasuredAt = now.AddHours(-2), CreatedAt = now.AddHours(-1),
                WeightKg = 73.4m, HeightCm = 163.5m, Systolic = 120, Diastolic = 80, HeartRate = 75,
                TemperatureC = 36.6m, O2Saturation = 97 },
            new VitalSign { PatientId = patient.Id, MeasuredAt = now.AddHours(-1), CreatedAt = now,
                Diastolic = 78, HeartRate = 79 },
            new VitalSign { PatientId = patient.Id, MeasuredAt = now.AddDays(-2), CreatedAt = now });
        var (pulse, unit) = await VitaAMetricAsync(db, "heart_rate", "bpm", "bpm", "vital");
        var independent = VitaAReading(patient.Id, pulse, unit, now.AddHours(-3), 70m, "device");
        db.ClinicalMeasurements.Add(independent);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH extra AS (
                INSERT INTO app.patient_profiles (id, medical_record_number, first_name, last_name, status)
                SELECT gen_random_uuid(), 'VITA-QA-' || n, 'Paciente', 'Sintético', 'Activo'
                FROM generate_series(1,80) n
                RETURNING id
            )
            INSERT INTO app.vital_signs (patient_id, measured_at, weight_kg, diastolic)
            SELECT extra.id, {now} - day * interval '1 day', 81.5,
                CASE WHEN day % 2 = 0 THEN 80 ELSE NULL END
            FROM extra CROSS JOIN generate_series(1,48) day;
            """);
        var before = await ReadVitaLegacyRowsAsync(db);
        var counts = new Dictionary<string, int>
        {
            ["weight"] = before.Count(x => x.Weight != null),
            ["height"] = before.Count(x => x.Height != null),
            ["systolic_bp"] = before.Count(x => x.Systolic != null),
            ["diastolic_bp"] = before.Count(x => x.Diastolic != null),
            ["heart_rate"] = before.Count(x => x.Pulse != null),
            ["temperature_c"] = before.Count(x => x.Temperature != null),
            ["o2_saturation"] = before.Count(x => x.Oxygen != null),
        };
        Assert.Equal(3843, before.Length);
        Assert.Equal(5769, counts.Values.Sum());

        await migrator.MigrateAsync(VitalsMigration);
        await migrator.MigrateAsync(VitalsMigration);
        Assert.Equal(before, await ReadVitaLegacyRowsAsync(db));
        var converted = db.ClinicalMeasurements.AsNoTracking().Where(x => x.IdempotencyKey != null
            && x.IdempotencyKey.StartsWith("legacy-vitals:"));
        var actual = await converted.GroupBy(x => x.Metric!.Code)
            .Select(x => new { Code = x.Key, Count = x.Count() }).ToDictionaryAsync(x => x.Code, x => x.Count);
        foreach (var (code, count) in counts)
        {
            Assert.Equal(count, actual[code]);
            finalOutput.WriteLine("{0}: antes={1}, después={2}, diferencia=0", code, count, actual[code]);
        }
        Assert.Equal(counts.Values.Sum(), actual.Values.Sum());
        Assert.Equal(actual.Values.Sum(), await converted.Select(x => x.IdempotencyKey).Distinct().CountAsync());
        Assert.False(await converted.AnyAsync(x => x.Source != "legacy" || x.ReportedBy != null || x.SourceChannel != null
            || x.SourceKey != null || x.Status != "confirmed" || x.UnitId != x.Metric!.DefaultUnitId));
        finalOutput.WriteLine("TOTAL: filas legacy={0}; valores antes={1}; mediciones canónicas={2}", before.Length,
            counts.Values.Sum(), actual.Values.Sum());

        await db.Database.ExecuteSqlRawAsync("ANALYZE app.vital_signs_legacy; ANALYZE app.clinical_measurements;");
        var plan = await db.Database.SqlQuery<string>($"""
            EXPLAIN (ANALYZE, BUFFERS, COSTS OFF)
            SELECT * FROM app.vital_signs WHERE patient_id = {patient.Id} ORDER BY measured_at DESC LIMIT 10
            """).ToListAsync();
        Assert.Contains(plan, line => line.Contains("ix_vital_signs_patient_measured_at", StringComparison.Ordinal));
        Assert.Contains(plan, line => line.Contains("ix_clinical_measurements_batch_id", StringComparison.Ordinal));
        foreach (var line in plan) finalOutput.WriteLine(line);

        var repository = new PatientRepository(db);
        Assert.Equal(3, (await repository.GetByIdAsync(patient.Id))!.VitalSigns.Count);
        var snapshot = await SosVitalsSnapshot.ResolveAsync(patient.Id, null,
            new ClinicalMeasurementRepository(db), now, CancellationToken.None);
        Assert.Equal(70m, Assert.Single(snapshot, x => x.MetricCode == "HR").Value);
        await using var host = await VitaAHostAsync();
        using var client = VitaAClient(host);
        var vitals = await client.GetFromJsonAsync<VitaVitalsDto>($"{VitaARoute}/vitals?userId={patient.UserId}");
        var latestPulse = Assert.Single(vitals!.Latest, x => x.MetricCode == "heart_rate");
        Assert.Equal(79m, latestPulse.Value);
        Assert.Null(latestPulse.Source);

        // Mismo modo COPY que el importador, con el rol de la aplicación.
        var importedId = Guid.NewGuid();
        await db.Database.OpenConnectionAsync();
        try
        {
            await db.Database.ExecuteSqlRawAsync("GRANT USAGE ON SCHEMA app TO app_user; SET ROLE app_user;");
            var pg = (NpgsqlConnection)db.Database.GetDbConnection();
            await using (var copy = await pg.BeginTextImportAsync("""
                COPY app.vital_signs (id, patient_id, measured_at, systolic) FROM STDIN WITH (FORMAT csv)
                """))
            {
                await copy.WriteLineAsync(FormattableString.Invariant($"{importedId},{patient.Id},{now:O},123"));
            }
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE app.vital_signs SET systolic = 124 WHERE id = {importedId}");
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("RESET ROLE;");
            await db.Database.CloseConnectionAsync();
        }
        Assert.Equal(124, (await db.VitalSigns.AsNoTracking().SingleAsync(x => x.Id == importedId)).Systolic);
        Assert.Equal(124m, await converted.Where(x => x.BatchId == importedId).Select(x => x.Value).SingleAsync());

        db.ChangeTracker.Clear();
        var viaRepository = await repository.AddAsync(new PatientProfile
        {
            FirstName = "Otra", LastName = "Prueba", Status = "Activo",
            VitalSigns = [new VitalSign { MeasuredAt = now, HeightCm = 169m }]
        });
        Assert.Equal(169m, Assert.Single((await repository.GetByIdAsync(viaRepository.Id))!.VitalSigns).HeightCm);
        db.ChangeTracker.Clear();
        viaRepository = (await repository.GetByIdAsync(viaRepository.Id))!;
        viaRepository.VitalSigns = [new VitalSign { MeasuredAt = now, WeightKg = 71.2m }];
        await repository.UpdateAsync(viaRepository);
        Assert.Equal(71.2m, Assert.Single((await repository.GetByIdAsync(viaRepository.Id))!.VitalSigns).WeightKg);
        var afterWrites = await ReadVitaLegacyRowsAsync(db);
        var consolidatedIds = await converted.Select(x => x.Id).OrderBy(x => x).ToArrayAsync();

        await migrator.MigrateAsync(BeforeVitalsMigration);
        Assert.Equal(afterWrites, await ReadVitaLegacyRowsAsync(db));
        Assert.Empty(await converted.ToArrayAsync());
        Assert.Equal(independent.Id, await db.ClinicalMeasurements.Select(x => x.Id).SingleAsync());
        await migrator.MigrateAsync(VitalsMigration);
        Assert.Equal(afterWrites, await ReadVitaLegacyRowsAsync(db));
        Assert.Equal(consolidatedIds, await converted.Select(x => x.Id).OrderBy(x => x).ToArrayAsync());
        finalOutput.WriteLine("Down y nuevo Up: mismos valores, fechas e identificadores; medición independiente conservada.");
    }

    [NutritionPostgresFact]
    public async Task VitaFinal_CapturePublishesOneBiometriaEventAndRollupDespiteConcurrentReplay()
    {
        await using var db = Context();
        var patient = await PatientAsync(db);
        var realQueue = new BiometriaMetricsQueue();
        var queue = Substitute.For<IBiometriaMetricsQueue>();
        queue.EnqueueAsync(Arg.Any<IBiometriaMetricEvent>(), Arg.Any<CancellationToken>())
            .Returns(x => realQueue.EnqueueAsync(x.Arg<IBiometriaMetricEvent>(), x.Arg<CancellationToken>()));
        queue.ReadAllAsync(Arg.Any<CancellationToken>()).Returns(x => realQueue.ReadAllAsync(x.Arg<CancellationToken>()));
        await using var host = await VitaAHostAsync(captureEnabled: true, biometria: queue);
        using var client = VitaAClient(host);
        var input = new ProposeVitaCaptureRequest("record_measurement", "vita_chat", "metric-event-1", VitaANow.AddMinutes(-5),
            MetricCode: "heart_rate", UnitCode: "bpm", Value: 77m);
        var proposal = await ProposeCaptureAsync(client, patient.UserId!.Value, input);
        await queue.DidNotReceive().EnqueueAsync(Arg.Any<IBiometriaMetricEvent>(), Arg.Any<CancellationToken>());
        var receipts = await Task.WhenAll(Enumerable.Range(0, 2)
            .Select(_ => ConfirmCaptureAsync(client, patient.UserId.Value, input, proposal.Token)));
        Assert.Equal(receipts[0], receipts[1]);
        Assert.Equal(receipts[0], await ConfirmCaptureAsync(client, patient.UserId.Value, input, proposal.Token));
        await queue.Received(1).EnqueueAsync(new BiometriaMeasuredEvent(
            patient.Id, patient.Gender, null, VitaAToday, null, null, null, null, null), Arg.Any<CancellationToken>());
        Assert.Equal(1, await db.VitaWriteReceipts.CountAsync());
        Assert.Equal(1, await db.ClinicalMeasurements.CountAsync());

        var metric = db.BiometriaDailyMetrics.AsNoTracking().Where(x => x.MetricDate == VitaAToday
            && x.MetricKey == "glucosa_distribution" && x.DimensionKey == "Sin dato");
        for (var attempt = 0; attempt < 50 && !await metric.AnyAsync(); attempt++)
            await Task.Delay(100);
        var row = await metric.SingleAsync();
        Assert.Equal(1L, row.TotalCount);
        Assert.Equal(0m, row.TotalValue);
        finalOutput.WriteLine("Captura + confirmaciones concurrentes + replay: 1 recibo, 1 medición, 1 evento y delta 1 en el rollup.");
    }

    private static Task<VitaLegacyRow[]> ReadVitaLegacyRowsAsync(AppDbContext db) => db.VitalSigns.AsNoTracking().OrderBy(x => x.Id)
        .Select(x => new VitaLegacyRow(x.Id, x.PatientId, x.MeasuredAt, x.CreatedAt, x.WeightKg, x.HeightCm, x.Systolic,
            x.Diastolic, x.HeartRate, x.TemperatureC, x.O2Saturation)).ToArrayAsync();

    private sealed record VitaLegacyRow(Guid Id, Guid Patient, DateTime Measured, DateTime Created,
        decimal? Weight, decimal? Height, int? Systolic, int? Diastolic, int? Pulse, decimal? Temperature, int? Oxygen);
}
