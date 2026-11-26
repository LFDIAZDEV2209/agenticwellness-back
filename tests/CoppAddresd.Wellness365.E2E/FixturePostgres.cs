using CoppAddresd.Application.Features.Wellness365;
using CoppAddresd.Domain.Wellness365;
using CoppAddresd.Infrastructure.Persistence;
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
    public FixtureWellnessPlanStore Store(AppDbContext db, FixtureClock clock) => new(db, new WellnessPlanService(clock));

    public async Task<Guid> SeedAsync(Scenario scenario, WellnessPlanCalendar calendar, CancellationToken ct)
    {
        await using var db = Open();
        if (!await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('app.person') IS NOT NULL AS \"Value\"").SingleAsync(ct))
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE SCHEMA app;
                CREATE SCHEMA erp;
                CREATE SCHEMA auth;
                CREATE TABLE app.patient_profiles (id uuid PRIMARY KEY, user_id uuid, status text,
                    first_name text, last_name text, timezone varchar(64), date_of_birth date, deleted_at timestamptz);
                CREATE TABLE auth."PaidActivations" ("PaymentId" uuid PRIMARY KEY, "Language" text,
                    "LinkedUserId" uuid, "RedeemedAt" timestamptz, "MembershipState" text, "MembershipPaid" boolean,
                    "MembershipExpiresAt" timestamptz, "ConfirmedAt" timestamptz, "Plan" text);
                CREATE TABLE erp.legal_documents (id uuid PRIMARY KEY, code text, current_version_id uuid);
                CREATE TABLE erp.legal_document_versions (
                    id uuid PRIMARY KEY, document_id uuid, major int, minor int, is_published bool);
                CREATE TABLE app.legal_document_acceptances (id uuid PRIMARY KEY, user_id uuid,
                    legal_document_version_id uuid, scope text, locale text, accepted_at timestamptz, withdrawn_at timestamptz);
                CREATE TABLE erp.professionals (id uuid PRIMARY KEY);
                CREATE TABLE app.wellness_journey_generations (id uuid PRIMARY KEY, patient_id uuid REFERENCES app.patient_profiles);
                CREATE TABLE public."__EFMigrationsHistory" (
                    "MigrationId" varchar(150) PRIMARY KEY, "ProductVersion" varchar(32) NOT NULL);
                """, ct);
            var ids = db.Database.GetMigrations().ToArray();
            var motor = Array.FindIndex(ids, id => id.EndsWith("_AddMotor365Persistence", StringComparison.Ordinal));
            var latest = ids.Single(id => id.EndsWith("_AllowMotor365NauseaObservation", StringComparison.Ordinal));
            var sql = db.GetService<IMigrator>().GenerateScript(ids[motor - 1], latest);
            await db.Database.ExecuteSqlRawAsync(sql, ct);
        }

        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE app.patient_profiles SET deleted_at = now() WHERE user_id = {FixturePlanAuth.AppUser};
            INSERT INTO app.patient_profiles (id, user_id, status, first_name, last_name, timezone, date_of_birth)
            VALUES ({id}, {FixturePlanAuth.AppUser}, 'Activo', 'Persona', 'QA', {calendar.TimeZoneId}, '1990-01-01');
            INSERT INTO auth."PaidActivations" VALUES ({id}, 'es', {FixturePlanAuth.AppUser},
                now(), 'active', {scenario.Block != "membership_inactive"}, '2030-01-01Z', '2025-01-01Z', 'annual');
            INSERT INTO app.wellness_journey_generations (id, patient_id) VALUES ({id}, {id})

            """, ct);
        db.Motor365People.Add(new Motor365Person
        {
            PatientId = id, Sex = WellnessSex.Female, BirthYear = 1990, HeightCm = 170,
            InitialWeightKg = 80, InitialWaistCm = 85, ActivityLevel = 2,
            Goal = WellnessGoal.LoseWeight, TargetWeightKg = (decimal)(scenario.Target ?? 70),
            StartDate = calendar.StartDate
        });
        db.Motor365Screenings.Add(new Motor365Screening
        {
            PersonId = id, SubmissionId = id, ParQ1 = scenario.Block == "doctor" ? (short)1 : (short)0,
            ParQ2 = 0, ParQ3 = 0, ParQ4 = 0, ParQ5 = 0, ParQ6 = 0, ParQ7 = 0,
            Nqs1 = 0, Nqs2 = 0, Nqs3 = 0, Nqs4 = 0, Nqs5 = 0,
            Nqs6 = scenario.Block == "unanswered" ? null : (short)0
        });
        await db.SaveChangesAsync(ct);
        foreach (var scope in new[] { "account-terms", "account-privacy" })
        {
            var doc = Guid.NewGuid();
            var version = Guid.NewGuid();
            var receipt = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO erp.legal_documents VALUES ({doc}, {"fixture-" + scope}, {version});
                INSERT INTO erp.legal_document_versions VALUES ({version}, {doc}, 1, 0, true);
                INSERT INTO app.legal_document_acceptances VALUES
                    ({receipt}, {FixturePlanAuth.AppUser}, {version}, {scope}, 'es', now(), null);
                INSERT INTO app.consent (person_id, acceptance_id) VALUES ({id}, {receipt})
                """, ct);
        }
        return id;
    }

    public async Task ProfileTimeZoneAsync(Guid patientId, string zone, CancellationToken ct)
    {
        await using var db = Open();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE app.patient_profiles SET timezone = {zone} WHERE id = {patientId}", ct);
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
