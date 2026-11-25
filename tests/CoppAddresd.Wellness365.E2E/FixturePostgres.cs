using CoppAddresd.Application.Features.Wellness365;
using CoppAddresd.Domain.Wellness365;
using CoppAddresd.Infrastructure.Persistence;
using CoppAddresd.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

sealed class FixturePostgres
{
    private readonly DbContextOptions<AppDbContext> _options;

    public FixturePostgres(string connectionString)
    {
        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        if (connection.Host is not ("localhost" or "127.0.0.1") || connection.Port == 5432 ||
            connection.Database?.StartsWith("copp_motor365_test_", StringComparison.Ordinal) != true)
            throw new InvalidOperationException("Se requiere un PostgreSQL local efímero de Motor 365.");

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, x => x.EnableRetryOnFailure()).Options;
    }

    public AppDbContext Open() => new(_options);
    public WellnessPlanStore Store(AppDbContext db, FixtureClock clock) => new(db, new WellnessPlanService(clock));

    public async Task<Guid> SeedAsync(Scenario scenario, WellnessPlanCalendar calendar, CancellationToken ct)
    {
        await using var db = Open();
        if (!await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('app.person') IS NOT NULL AS \"Value\"").SingleAsync(ct))
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE SCHEMA app;
                CREATE SCHEMA erp;
                CREATE TABLE app.patient_profiles (id uuid PRIMARY KEY, timezone varchar(64), date_of_birth date, deleted_at timestamptz);
                CREATE TABLE app.legal_document_acceptances (id uuid PRIMARY KEY);
                CREATE TABLE erp.professionals (id uuid PRIMARY KEY);
                CREATE TABLE app.wellness_journey_generations (id uuid PRIMARY KEY, patient_id uuid REFERENCES app.patient_profiles);
                CREATE TABLE public."__EFMigrationsHistory" (
                    "MigrationId" varchar(150) PRIMARY KEY, "ProductVersion" varchar(32) NOT NULL);
                """, ct);
            var ids = db.Database.GetMigrations().ToArray();
            var motor = Array.FindIndex(ids, id => id.EndsWith("_AddMotor365Persistence", StringComparison.Ordinal));
            var calendarMigration = ids.Single(id => id.EndsWith("_AddMotor365Calendar", StringComparison.Ordinal));
            var sql = db.GetService<IMigrator>().GenerateScript(ids[motor - 1], calendarMigration);
            await db.Database.ExecuteSqlRawAsync(sql, ct);
        }

        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO app.patient_profiles (id, timezone, date_of_birth)
            VALUES ({id}, {calendar.TimeZoneId}, '1990-01-01')
            """, ct);
        db.Motor365People.Add(new Motor365Person
        {
            PatientId = id, Sex = WellnessSex.Female, BirthYear = 1990, HeightCm = 170,
            InitialWeightKg = 80, InitialWaistCm = 85, ActivityLevel = 2,
            Goal = WellnessGoal.LoseWeight, TargetWeightKg = (decimal)(scenario.Target ?? 70),
            StartDate = calendar.StartDate
        });
        await db.SaveChangesAsync(ct);
        return id;
    }

    public async Task<object> StatsAsync(Guid patientId, CancellationToken ct)
    {
        await using var db = Open();
        var dates = await db.Motor365DailyLogs.Where(x => x.PersonId == patientId).OrderBy(x => x.Day)
            .Select(x => x.LogDate).ToArrayAsync(ct);
        var events = await db.Motor365AuditLog.CountAsync(x => x.PersonId == patientId, ct);
        var planVersion = await db.Motor365People.Where(x => x.PatientId == patientId)
            .Select(x => x.PlanVersion).SingleAsync(ct);
        return new { dates, events, planVersion };
    }
}
