using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Infrastructure.Persistence;
using CoppAddresd.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NSubstitute;

namespace CoppAddresd.IntegrationTests;

public sealed class MedicationReminderTests : IAsyncLifetime
{
    private readonly string server = Environment.GetEnvironmentVariable("COP_TEST_DB_CONNECTION") ?? "";
    private readonly string database = $"copp_med_reminders_{Guid.NewGuid():N}";
    private readonly Guid patientId = Guid.NewGuid();
    private readonly Guid userId = Guid.NewGuid();
    private readonly Guid treatmentId = Guid.NewGuid();
    private readonly Guid regimenId = Guid.NewGuid();
    private readonly DateTime scheduled = new(2026, 11, 19, 13, 0, 0, DateTimeKind.Utc);
    private readonly TestClock clock = new();
    private bool created;
    private static readonly TimeSpan FollowUp = TimeSpan.FromMinutes(30);

    private AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(new NpgsqlConnectionStringBuilder(server) { Database = database }.ConnectionString,
            pg => pg.EnableRetryOnFailure()).Options);

    private NpgsqlConnection Admin() => new(new NpgsqlConnectionStringBuilder(server) { Database = "postgres" }.ConnectionString);
    private MedicationControlStore Store(AppDbContext db) => new(db, Substitute.For<IPatientScopeGuard>(), clock);

    public async Task InitializeAsync()
    {
        if (server.Length == 0) return;
        await using var admin = Admin();
        await admin.OpenAsync();
        await using var create = new NpgsqlCommand($"""CREATE DATABASE "{database}" TEMPLATE template0""", admin);
        await create.ExecuteNonQueryAsync();
        created = true;
        await using var db = Context();
        await db.Database.ExecuteSqlRawAsync("""CREATE SCHEMA auth; CREATE TABLE auth."Users" ("Id" uuid PRIMARY KEY, "SecurityStamp" text NULL);""");
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO auth."Users" ("Id") VALUES ({userId})""");
        var start = scheduled.AddDays(-1);
        db.PatientProfiles.Add(new PatientProfile { Id = patientId, UserId = userId, FirstName = "QA", LastName = "Recordatorios" });
        db.MedicationTreatments.Add(new MedicationTreatment
        {
            Id = treatmentId, PatientId = patientId, Name = "Producto de prueba", ClientRequestId = Guid.NewGuid(),
            CreatedBy = userId, CreatedAt = start, UpdatedAt = start,
        });
        db.MedicationRegimens.Add(new MedicationRegimen
        {
            Id = regimenId, TreatmentId = treatmentId, TimeZone = "America/Bogota", Dosage = 1,
            Unit = "unidad", StartDate = DateOnly.FromDateTime(start), CreatedAt = start,
            Instructions = "Texto de prueba",
        });
        db.MedicationScheduleSlots.Add(new MedicationScheduleSlot { Id = Guid.NewGuid(), RegimenId = regimenId, LocalTime = new TimeOnly(8, 0) });
        db.MedicationTreatmentStatusEvents.Add(new MedicationTreatmentStatusEvent
        {
            Id = Guid.NewGuid(), TreatmentId = treatmentId, Status = "active", Revision = 1, EffectiveAt = start,
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (!created) return;
        await using var admin = Admin();
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"""DROP DATABASE "{database}" WITH (FORCE)""", admin);
        await drop.ExecuteNonQueryAsync();
    }

    [NutritionPostgresFact]
    public async Task ConcurrentClaimsAtLocalTime_SnoozeAndFollowUp_DeduplicateInApp()
    {
        await using var first = Context();
        await using var second = Context();
        var store = Store(first);
        Assert.Contains(patientId, await store.ListReminderPatientsAsync(null, default));
        Assert.Empty(await store.ListReminderPatientsAsync(patientId, default));
        clock.UtcNow = scheduled.AddSeconds(-1);
        Assert.Empty(await store.ClaimRemindersAsync(patientId, "app", FollowUp, default));
        clock.UtcNow = scheduled.AddSeconds(5);
        var claims = await Task.WhenAll(store.ClaimRemindersAsync(patientId, "app", FollowUp, default),
            Store(second).ClaimRemindersAsync(patientId, "app", FollowUp, default));
        var initial = Assert.Single(claims.SelectMany(claim => claim));
        Assert.Equal(scheduled, initial.Occurrence.Dose.ScheduledAt);
        Assert.Equal(userId, initial.Occurrence.UserId);
        var doseId = initial.Occurrence.Dose.Id;
        var lookup = await store.GetPendingAsync(patientId, doseId, scheduled, default);
        Assert.Equal("Texto de prueba", lookup?.Instructions);

        clock.UtcNow = scheduled.AddMinutes(3);
        Assert.True(await store.SnoozeAsync(patientId, doseId, scheduled, 15, default));
        clock.UtcNow = scheduled.AddMinutes(4);
        Assert.True(await store.SnoozeAsync(patientId, doseId, scheduled, 15, default));
        clock.UtcNow = scheduled.AddMinutes(18);
        Assert.Single(await store.ClaimRemindersAsync(patientId, "app", FollowUp, default));
        Assert.Empty(await store.ClaimRemindersAsync(patientId, "app", FollowUp, default));
        clock.UtcNow = scheduled.AddMinutes(30);
        Assert.Empty(await store.ClaimRemindersAsync(patientId, "app", FollowUp, default));
        Assert.False(await store.SnoozeAsync(patientId, doseId, scheduled, 30, default));
        var notifications = await first.AppNotifications.Where(n => n.PatientId == patientId).ToListAsync();
        Assert.Equal(2, notifications.Count);
        Assert.All(notifications, item => Assert.DoesNotContain("Producto de prueba", item.Message));
    }

    [NutritionPostgresFact]
    public async Task ExistingRecordAndSuspension_SuppressFutureDeliveries()
    {
        await using var db = Context();
        var store = Store(db);
        clock.UtcNow = scheduled;
        var initial = Assert.Single(await store.ClaimRemindersAsync(patientId, "app", FollowUp, default));
        await store.RecordAsync(patientId, userId, "patient_reported", initial.Occurrence.Dose.Id,
            new(Guid.NewGuid(), "taken", scheduled, null, 0, scheduled), default);
        clock.UtcNow = scheduled + FollowUp;
        Assert.Empty(await store.ClaimRemindersAsync(patientId, "app", FollowUp, default));
        Assert.Null(await store.GetPendingAsync(patientId, initial.Occurrence.Dose.Id, scheduled, default));
        var treatment = await db.MedicationTreatments.SingleAsync(t => t.Id == treatmentId);
        treatment.Status = "paused";
        await db.SaveChangesAsync();
        clock.UtcNow = scheduled.AddDays(1);
        Assert.Empty(await store.ClaimRemindersAsync(patientId, "app", FollowUp, default));
        clock.UtcNow = scheduled.AddDays(1).AddMinutes(5);
        treatment = await db.MedicationTreatments.SingleAsync(t => t.Id == treatmentId);
        treatment.Status = "active";
        db.MedicationTreatmentStatusEvents.AddRange(
            new MedicationTreatmentStatusEvent { Id = Guid.NewGuid(), TreatmentId = treatmentId,
                Revision = 2, Status = "paused", EffectiveAt = scheduled.AddDays(1).AddMinutes(1) },
            new MedicationTreatmentStatusEvent { Id = Guid.NewGuid(), TreatmentId = treatmentId,
                Revision = 3, Status = "active", EffectiveAt = clock.UtcNow });
        await db.SaveChangesAsync();
        clock.UtcNow = scheduled.AddDays(1) + FollowUp;
        Assert.Empty(await store.ClaimRemindersAsync(patientId, "app", FollowUp, default));
        Assert.Single(await db.AppNotifications.Where(n => n.PatientId == patientId).ToListAsync());
    }

    [NutritionPostgresFact]
    public async Task AutomaticFollowUp_DoesNotReplayExpiredInitialOrMixChannels()
    {
        await using var db = Context();
        var store = Store(db);
        clock.UtcNow = scheduled.AddMinutes(20);
        Assert.Empty(await store.ClaimRemindersAsync(patientId, "app", FollowUp, default));
        clock.UtcNow = scheduled + FollowUp;
        var app = Assert.Single(await store.ClaimRemindersAsync(patientId, "app", FollowUp, default));
        Assert.Empty(await store.ClaimRemindersAsync(patientId, "whatsapp", FollowUp, default));
        await store.SetReminderPreferencesAsync(patientId, userId, new(true, true, true, 0, null), default);
        var secondary = Assert.Single(await store.ClaimRemindersAsync(patientId, "whatsapp", FollowUp, default));
        Assert.Equal(app.Occurrence.Dose.Id, secondary.Occurrence.Dose.Id);
        Assert.NotEqual(app.DeliveryId, secondary.DeliveryId);
        Assert.Single(await db.AppNotifications.Where(n => n.PatientId == patientId).ToListAsync());
        Assert.All(await db.Set<MedicationReminderDelivery>().ToListAsync(), delivery => Assert.Equal(1, delivery.Phase));
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTime UtcNow { get; set; }
        public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);
    }
}
