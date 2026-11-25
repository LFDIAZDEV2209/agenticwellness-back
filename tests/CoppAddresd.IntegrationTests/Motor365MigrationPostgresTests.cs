using CoppAddresd.Domain.Wellness365;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace CoppAddresd.IntegrationTests;

public sealed class Motor365IsolatedPostgresFactAttribute : FactAttribute
{
    public Motor365IsolatedPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Motor365MigrationPostgresTests.ConnectionVariable)))
            Skip = "Requiere una BD PostgreSQL efímera y vacía. Ver docs/modules/motor-365/README.md.";
    }
}

public sealed class Motor365MigrationPostgresTests
{
    internal const string ConnectionVariable = "COP_MOTOR365_TEST_DB_CONNECTION";

    [Motor365IsolatedPostgresFact]
    public async Task Up_EnforcesConstraintsAndAudit_DownRestoresPredecessorObjects()
    {
        var connectionString = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(ConnectionVariable)!);
        Assert.StartsWith("copp_motor365_test_", connectionString.Database!);
        Assert.NotEqual(5432, connectionString.Port);

        await using var connection = new NpgsqlConnection(connectionString.ConnectionString);
        await connection.OpenAsync();
        Assert.Equal(0L, await ScalarAsync<long>(connection, """
            SELECT count(*) FROM information_schema.schemata WHERE schema_name IN ('app', 'erp')
            """));

        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        var ids = db.Database.GetMigrations().ToArray();
        var index = Array.FindIndex(ids, id => id.EndsWith("_AddMotor365Persistence", StringComparison.Ordinal));
        Assert.True(index > 0);
        var migrator = db.GetService<IMigrator>();

        // Se reproduce la frontera de la migración con los objetos preexistentes que referencia.
        await ExecuteAsync(connection, """
            CREATE SCHEMA app;
            CREATE SCHEMA erp;
            CREATE TABLE app.patient_profiles (id uuid PRIMARY KEY);
            CREATE TABLE app.legal_document_acceptances (id uuid PRIMARY KEY);
            CREATE TABLE erp.professionals (id uuid PRIMARY KEY);
            CREATE TABLE app.wellness_journey_generations (id uuid PRIMARY KEY, patient_id uuid REFERENCES app.patient_profiles);
            CREATE TABLE public."__EFMigrationsHistory" (
                "MigrationId" varchar(150) PRIMARY KEY, "ProductVersion" varchar(32) NOT NULL);
            ALTER DEFAULT PRIVILEGES IN SCHEMA app
                GRANT SELECT, INSERT, UPDATE, DELETE, TRUNCATE ON TABLES TO app_user;
            INSERT INTO app.patient_profiles VALUES
                ('00000000-0000-0000-0000-000000000001'), ('00000000-0000-0000-0000-000000000002');
            INSERT INTO app.wellness_journey_generations VALUES
                ('00000000-0000-0000-0000-000000000003', '00000000-0000-0000-0000-000000000001');
            """);
        const string inventory = """
            SELECT jsonb_build_array(
                (SELECT jsonb_agg(relname ORDER BY relname) FROM pg_class
                    WHERE relnamespace IN ('app'::regnamespace, 'erp'::regnamespace)),
                (SELECT jsonb_agg(proname ORDER BY proname) FROM pg_proc
                    WHERE pronamespace IN ('app'::regnamespace, 'erp'::regnamespace)),
                (SELECT jsonb_agg(tgname ORDER BY tgname) FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid
                    WHERE c.relnamespace IN ('app'::regnamespace, 'erp'::regnamespace) AND NOT t.tgisinternal),
                (SELECT jsonb_agg("MigrationId" ORDER BY "MigrationId") FROM public."__EFMigrationsHistory")
            )::text
            """;
        var before = await ScalarAsync<string>(connection, inventory);
        await ExecuteAsync(connection, migrator.GenerateScript(ids[index - 1], ids[index]));

        const string first = "00000000-0000-0000-0000-000000000001";
        const string second = "00000000-0000-0000-0000-000000000002";
        const string submission = "00000000-0000-0000-0000-000000000003";
        await ExecuteAsync(connection, $"""
            INSERT INTO app."person"
                (patient_id, sex, birth_year, height_cm, initial_weight_kg, initial_waist_cm, activity_level,
                 goal, pace, vegetarian, lactose_free, fitness_level, start_date, engine_ver, plan_version)
            SELECT id, 'Female', 1989, 170, 75, 82, 1, 'LoseWeight', 'Moderate', false, false, 0, '2026-11-25', '5.2', 1
            FROM app.patient_profiles;
            INSERT INTO app.engine_params (version, engine_ver, parameters_json, changed_by, reason)
            VALUES (1, '5.2', jsonb_build_object(), '{first}', 'Fixture');
            INSERT INTO app.screening
                (person_id, submission_id, par_q_1, par_q_2, par_q_3, par_q_4, par_q_5, par_q_6, par_q_7,
                 nqs_1, nqs_2, nqs_3, nqs_4, nqs_5)
            VALUES ('{first}', '{submission}', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            """);

        await FailsAsync(connection, "23514", "ck_motor365_screening_nqs_6", "UPDATE app.screening SET nqs_6 = 2");
        await FailsAsync(connection, "23514", "ck_motor365_person_sex", "UPDATE app.\"person\" SET sex = 'Invalid'");

        string Log(int day, string adherence, int sleep, int energy, int pain, string symptoms = "ARRAY[]::text[]") => $"""
            INSERT INTO app.daily_log (person_id, day, log_date, adherence, sleep_hours, energy, pain, symptoms, completed_exercise)
            VALUES ('{first}', {day}, '2026-11-25', {adherence}, {sleep}, {energy}, {pain}, {symptoms}, false)
            """;
        await FailsAsync(connection, "23514", "ck_motor365_daily_log_day", Log(366, "1", 8, 3, 0));
        await FailsAsync(connection, "23514", "ck_motor365_daily_log_adherence", Log(1, "1.01", 8, 3, 0));
        await FailsAsync(connection, "23514", "ck_motor365_daily_log_sleep", Log(1, "1", 25, 3, 0));
        await FailsAsync(connection, "23514", "ck_motor365_daily_log_energy", Log(1, "1", 8, 0, 0));
        await FailsAsync(connection, "23514", "ck_motor365_daily_log_pain", Log(1, "1", 8, 3, 11));
        await FailsAsync(connection, "23514", "ck_motor365_daily_log_symptoms",
            Log(1, "1", 8, 3, 0, "ARRAY['mareo','desconocido']::text[]"));
        await ExecuteAsync(connection, Log(1, "0", 0, 1, 0));
        await ExecuteAsync(connection, Log(365, "1", 24, 5, 10, "ARRAY['mareo','hambre','fatiga','insomnio']::text[]")
            .Replace("2026-11-25", "2027-11-24"));
        await FailsAsync(connection, "23505", "PK_daily_log", Log(1, "1", 8, 3, 0).Replace("2026-11-25", "2026-11-26"));

        string Snapshot(string patient) => $"""
            INSERT INTO app.plan_snapshot (person_id, plan_version, params_version, submission_id, engine_ver, plan_json)
            VALUES ('{patient}', 1, 1, '{submission}', '5.2', jsonb_build_object())
            """;
        await FailsAsync(connection, "23503", "fk_motor365_snapshot_complete_screening", Snapshot(first));
        await ExecuteAsync(connection, "UPDATE app.screening SET nqs_6 = 1");
        await FailsAsync(connection, "23503", "fk_motor365_snapshot_complete_screening", Snapshot(second));
        var cache = new Motor365PlanSnapshot
        {
            PersonId = Guid.Parse(first),
            SubmissionId = Guid.Parse(submission),
            PlanVersion = 1,
            ParamsVersion = 1,
            PlanJson = "{}",
        };
        db.Motor365PlanSnapshots.Add(cache);
        await db.SaveChangesAsync();
        Assert.True(db.Entry(cache).Property<bool>("ScreeningComplete").CurrentValue);

        await ExecuteAsync(connection, $"INSERT INTO app.audit_log (person_id, action) VALUES ('{first}', 'plan_generated')");
        await FailsAsync(connection, "55000", null, "UPDATE app.audit_log SET action = 'rewrite'");
        await FailsAsync(connection, "55000", null, "DELETE FROM app.audit_log");
        await FailsAsync(connection, "55000", null, "TRUNCATE app.audit_log");
        Assert.False(await ScalarAsync<bool>(connection, """
            SELECT has_table_privilege('app_user', 'app.audit_log', 'UPDATE')
                OR has_table_privilege('app_user', 'app.audit_log', 'DELETE')
                OR has_table_privilege('app_user', 'app.audit_log', 'TRUNCATE')
            """));

        await ExecuteAsync(connection, migrator.GenerateScript(ids[index], ids[index - 1]));
        Assert.Equal(before, await ScalarAsync<string>(connection, inventory));
        Assert.Equal(2L, await ScalarAsync<long>(connection, "SELECT count(*) FROM app.patient_profiles"));
        Assert.Equal(1L, await ScalarAsync<long>(connection, "SELECT count(*) FROM app.wellness_journey_generations"));
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return Assert.IsType<T>(await command.ExecuteScalarAsync());
    }

    private static async Task FailsAsync(NpgsqlConnection connection, string state, string? constraint, string sql)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, sql));
        Assert.Equal(state, error.SqlState);
        if (constraint is not null)
            Assert.Equal(constraint, error.ConstraintName);
    }
}
