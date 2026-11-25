using System.Text.Json;
using CoppAddresd.Application.Features.Motor365;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Infrastructure.Persistence;
using CoppAddresd.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NSubstitute;

namespace CoppAddresd.IntegrationTests;

public sealed class Motor365ContractPostgresFactAttribute : FactAttribute
{
    public Motor365ContractPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Motor365ContractPostgresTests.ConnectionVariable)))
            Skip = "Requiere una BD efímera y vacía en puerto alternativo.";
    }
}

public sealed class Motor365ContractPostgresTests
{
    internal const string ConnectionVariable = "COP_MOTOR365_API_TEST_DB_CONNECTION";

    [Motor365ContractPostgresFact]
    public async Task Upsert_replay_plan_cierre_y_revocacion_usan_la_persistencia_real()
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(ConnectionVariable)!);
        Assert.StartsWith("copp_motor365_test_", connection.Database!);
        Assert.NotEqual(5432, connection.Port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var ct = timeout.Token;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection.ConnectionString,
            npgsql => npgsql.EnableRetryOnFailure()).Options;
        var patient = Guid.NewGuid();
        var user = Guid.NewGuid();
        var submission = Guid.NewGuid();
        var actor = Substitute.For<IMotor365Actor>();
        actor.UserId.Returns(user);
        actor.IsApp.Returns(true);
        var guard = Substitute.For<IPatientScopeGuard>();
        guard.EnsureSelfAsync(user, Arg.Any<CancellationToken>()).Returns(patient);
        var clock = new FixedClock();

        await using (var db = new AppDbContext(options))
        {
            var schemas = await db.Database.SqlQueryRaw<int>("""
                SELECT count(*)::int AS "Value" FROM information_schema.schemata WHERE schema_name IN ('app', 'erp')
                """).SingleAsync(ct);
            Assert.Equal(0, schemas);
            await db.Database.ExecuteSqlRawAsync("""
                CREATE SCHEMA app;
                CREATE SCHEMA erp;
                CREATE TABLE app.patient_profiles (
                    id uuid PRIMARY KEY, user_id uuid, first_name text, last_name text, timezone text,
                    deleted_at timestamptz, status text, date_of_birth date);
                CREATE TABLE erp.legal_documents (id uuid PRIMARY KEY, code text, current_version_id uuid);
                CREATE TABLE erp.legal_document_versions (
                    id uuid PRIMARY KEY, document_id uuid, major int, minor int, is_published bool);
                CREATE TABLE app.legal_document_acceptances (
                    id uuid PRIMARY KEY, user_id uuid, legal_document_version_id uuid, scope text, locale text,
                    accepted_at timestamptz, withdrawn_at timestamptz);
                CREATE TABLE erp.professionals (id uuid PRIMARY KEY);
                CREATE TABLE public."__EFMigrationsHistory" (
                    "MigrationId" varchar(150) PRIMARY KEY, "ProductVersion" varchar(32) NOT NULL);
                """, ct);
            var ids = db.Database.GetMigrations().ToArray();
            var position = Array.FindIndex(ids, x => x.EndsWith("_AddMotor365Persistence", StringComparison.Ordinal));
            Assert.True(position > 0);
            await db.Database.ExecuteSqlRawAsync(db.GetService<IMigrator>().GenerateScript(ids[position - 1], ids[position],
                MigrationsSqlGenerationOptions.NoTransactions), ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO app.patient_profiles (id, user_id, first_name, last_name, timezone, status)
                VALUES ({patient}, {user}, 'Prueba', 'Motor', 'America/Bogota', 'Activo')
                """, ct);
            foreach (var code in new[] { "paid-demo-terms-es", "paid-demo-privacy-es" })
            {
                var document = Guid.NewGuid();
                var version = Guid.NewGuid();
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO erp.legal_documents VALUES ({document}, {code}, {version});
                    INSERT INTO erp.legal_document_versions VALUES ({version}, {document}, 1, 0, true);
                    INSERT INTO app.legal_document_acceptances
                    VALUES ({Guid.NewGuid()}, {user}, {version}, {code}, 'es', now(), NULL)
                    """, ct);
            }
        }

        var input = new MotorPersonInput(null, submission, null, "F", 38, 165, 75, 85, 68, 2, "bajar", "m", 0, 0,
            [0, 0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0], null, null, new(null, "1.0"), null, null, null);
        var request = new WriteMotorPerson(null, input);
        await using (var db = new AppDbContext(options))
        {
            var store = new Motor365Store(db);
            var handler = new WriteMotorPersonHandler(new(store, actor, guard, clock));
            Assert.True((await handler.Handle(request, ct)).Created);
            Assert.Single(await db.Motor365PlanSnapshots.ToListAsync(ct));
        }

        await using (var db = new AppDbContext(options))
        {
            var store = new Motor365Store(db);
            var people = new MotorPeople(store, actor, guard, clock);
            Assert.False((await new WriteMotorPersonHandler(people).Handle(request, ct)).Created);
            var parameters = await new PutMotorParamsHandler(store, actor, clock).Handle(
                new(new(JsonSerializer.SerializeToElement(new { floorF = 2000 }), "Prueba", 1)), ct);
            Assert.Equal(2, parameters.ParamsVersion);
            var day = await new GetMotorDayHandler(people).Handle(new(patient, 1), ct);
            Assert.Equal(1, day.ParamsVersion);
            Assert.Equal(4, day.Menu!.Count);

            var close = new PutMotorLogHandler(people);
            var log = new PutMotorLog(patient, 1, new(.8m, 8, 3, 0, [], null, null, 1));
            Assert.Equal(2, (await close.Handle(log, ct)).Ver);
            Assert.Equal(2, (await close.Handle(log, ct)).Ver);
            Assert.Equal(2, (await new GetMotorDayHandler(people).Handle(new(patient, 1), ct)).ParamsVersion);
            Assert.Equal(3, (await store.ReadAuditAsync(1, 20, ct)).Count);
        }

        await using (var db = new AppDbContext(options))
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE app.legal_document_acceptances SET withdrawn_at = now() WHERE user_id = {user}
                """, ct);
            var people = new MotorPeople(new Motor365Store(db), actor, guard, clock);
            var ex = await Assert.ThrowsAsync<MotorException>(() => new GetMotorPlanHandler(people).Handle(new(patient), ct));
            Assert.Equal("terms", ex.Status!.Code);
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 27, 2, 0, 0, TimeSpan.Zero);
    }
}
