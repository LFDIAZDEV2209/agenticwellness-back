using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Wellness365;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CoppAddresd.UnitTests.Wellness365;

public sealed class Motor365PersistenceTests
{
    private const string AllowedSymptomsSql = "symptoms <@ ARRAY['mareo','hambre','fatiga','insomnio','nauseas']::text[]";

    private static AppDbContext CreateContext() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql("Host=127.0.0.1;Port=1;Database=motor365_model;Username=design_only").Options);

    [Fact]
    public void Model_ScopesDataToCanonicalPatientAndKeepsTheCacheKey()
    {
        using var db = CreateContext();
        var model = db.GetService<IDesignTimeModel>().Model;
        var person = model.FindEntityType(typeof(Motor365Person))!;
        var patient = Assert.Single(person.GetForeignKeys());
        Assert.Equal(typeof(PatientProfile), patient.PrincipalEntityType.ClrType);
        Assert.Equal(nameof(Motor365Person.PatientId), Assert.Single(patient.Properties).Name);
        Assert.True(patient.IsUnique);
        Assert.Equal(DeleteBehavior.Restrict, patient.DeleteBehavior);

        var log = model.FindEntityType(typeof(Motor365DailyLog))!;
        Assert.Equal(new[] { "PersonId", "Day" }, log.FindPrimaryKey()!.Properties.Select(x => x.Name));
        Assert.Contains(log.GetIndexes(), x => x.IsUnique &&
            x.Properties.Select(p => p.Name).SequenceEqual(new[] { "PersonId", "LogDate" }));
        Assert.Contains(log.GetIndexes(), x => x.Properties.Select(p => p.Name).SequenceEqual(new[] { "LogDate" }));
        Assert.Equal(AllowedSymptomsSql,
            Assert.Single(log.GetCheckConstraints(), x => x.Name == "ck_motor365_daily_log_symptoms").Sql);

        var snapshot = model.FindEntityType(typeof(Motor365PlanSnapshot))!;
        Assert.Equal(new[] { "PersonId", "PlanVersion", "ParamsVersion" },
            snapshot.FindPrimaryKey()!.Properties.Select(x => x.Name));
        Assert.Contains(snapshot.GetForeignKeys(), x => x.PrincipalEntityType.ClrType == typeof(Motor365EngineParams)
            && x.Properties.Select(p => p.Name).SequenceEqual(new[] { "ParamsVersion", "EngineVer" }));
        Assert.Equal(PropertySaveBehavior.Ignore, snapshot.FindProperty("ScreeningComplete")!.GetBeforeSaveBehavior());
    }

    [Fact]
    public void Screening_MissingAnyAnswerBlocksPlan()
    {
        using var db = CreateContext();
        var screening = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Motor365Screening))!;
        var answers = screening.GetProperties().Where(p => p.ClrType == typeof(short?)).ToArray();
        Assert.Equal(13, answers.Length);
        var entry = new Motor365Screening();
        foreach (var answer in answers)
            answer.PropertyInfo!.SetValue(entry, (short)0);
        Assert.True(entry.HasCompleteAnswers);

        foreach (var answer in answers)
        {
            var column = answer.GetColumnName();
            Assert.True(answer.IsNullable);
            Assert.Contains(screening.GetCheckConstraints(), check => check.Sql == $"{column} IN (0, 1)");
            Assert.Contains($"{column} IS NOT NULL", screening.FindProperty("AnswersComplete")!.GetComputedColumnSql());
            answer.PropertyInfo!.SetValue(entry, null);
            Assert.False(entry.HasCompleteAnswers);
            answer.PropertyInfo!.SetValue(entry, (short)1);
        }
        Assert.True(entry.HasCompleteAnswers);
    }

    [Fact]
    public void Migration_UsesRealChecksAndDropsExactlyItsTables()
    {
        using var db = CreateContext();
        var assembly = db.GetService<IMigrationsAssembly>();
        var (id, type) = Assert.Single(assembly.Migrations, x => x.Key.EndsWith("_AddMotor365Persistence", StringComparison.Ordinal));
        var migration = assembly.CreateMigration(type, db.Database.ProviderName!);
        var up = migration.UpOperations.OfType<CreateTableOperation>().ToArray();
        var down = migration.DownOperations.OfType<DropTableOperation>().ToArray();
        Assert.Equal(
            new[] { "audit_log", "consent", "daily_log", "engine_event", "engine_params", "person", "plan_snapshot", "professional_review", "screening" },
            up.Select(x => x.Name).Order());
        Assert.Equal(up.Select(x => (x.Schema, x.Name)).Order(), down.Select(x => (x.Schema, x.Name)).Order());
        Assert.All(up, x => Assert.Equal("app", x.Schema));
        Assert.All(migration.UpOperations, x => Assert.True(
            x is CreateTableOperation or CreateIndexOperation or AddForeignKeyOperation or SqlOperation));
        Assert.All(migration.DownOperations, x => Assert.True(x is DropTableOperation or SqlOperation));
        var checks = Assert.Single(up, x => x.Name == "daily_log").CheckConstraints.Select(x => x.Sql);
        Assert.Contains("day BETWEEN 1 AND 365", checks);
        Assert.Contains("adherence BETWEEN 0 AND 1", checks);
        Assert.Contains("sleep_hours BETWEEN 0 AND 24", checks);
        Assert.Contains("energy BETWEEN 1 AND 5", checks);
        Assert.Contains("pain BETWEEN 0 AND 10", checks);
        Assert.Contains("symptoms <@ ARRAY['mareo','hambre','fatiga','insomnio']::text[]", checks);

        var screeningGate = Assert.Single(migration.UpOperations.OfType<AddForeignKeyOperation>());
        Assert.Equal("plan_snapshot", screeningGate.Table);
        Assert.Equal("screening", screeningGate.PrincipalTable);
        Assert.Equal(new[] { "person_id", "submission_id", "screening_complete" }, screeningGate.Columns);
        Assert.Equal(new[] { "person_id", "submission_id", "answers_complete" }, screeningGate.PrincipalColumns);

        var previous = assembly.Migrations.Keys.LastOrDefault(x => string.CompareOrdinal(x, id) < 0);
        var migrator = db.GetService<IMigrator>();
        var upSql = migrator.GenerateScript(previous, id);
        var downSql = migrator.GenerateScript(id, previous);
        Assert.Contains("REVOKE UPDATE, DELETE, TRUNCATE ON TABLE app.audit_log FROM app_user", upSql);
        Assert.Contains("BEFORE UPDATE OR DELETE OR TRUNCATE ON app.audit_log", upSql);
        Assert.Contains("DROP FUNCTION app.motor365_reject_audit_mutation()", downSql);
    }
}
