using CoppAddresd.WhatsApp.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace CoppAddresd.WhatsApp.Tests;

/// <summary>BD desechable en el PostgreSQL local: nunca modifica datos de desarrollo.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly string name = "copp_whatsapp_test_" + Guid.NewGuid().ToString("N");
    private string adminConnection = "";
    private bool appSchemaCreated;
    public string ConnectionString { get; private set; } = "";
    public WhatsAppDbContext CreateDb() => new(new DbContextOptionsBuilder<WhatsAppDbContext>()
        .UseNpgsql(ConnectionString, pg => pg.MigrationsHistoryTable("__ef_migrations_history", "whatsapp")).Options);
    public CoppAddresd.Infrastructure.Persistence.AppDbContext CreateAppDb() => new(
        new DbContextOptionsBuilder<CoppAddresd.Infrastructure.Persistence.AppDbContext>().UseNpgsql(ConnectionString).Options);

    public async Task CreateAppSchemaAsync(CancellationToken ct)
    {
        if (appSchemaCreated) return;
        await using var app = CreateAppDb();
        await app.Database.OpenConnectionAsync(ct);
        var connection = app.Database.GetDbConnection();
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // El modelo de RxTerms usa gin_trgm_ops. En producción lo instala la migración de catálogos.
        command.CommandText = "CREATE EXTENSION IF NOT EXISTS pg_trgm;\n" + app.Database.GenerateCreateScript();
        await command.ExecuteNonQueryAsync(ct);

        // GenerateCreateScript solo crea el modelo EF, no las tablas SQL de KAN-178.
        // Aplicar el mismo DDL y las mismas reglas iniciales que en el despliegue.
        command.CommandText = """
            DO $$ BEGIN
                IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'app_user') THEN
                    CREATE ROLE app_user NOLOGIN;
                END IF;
            EXCEPTION WHEN duplicate_object OR unique_violation THEN
                -- Otro fixture puede crear el mismo rol global en paralelo.
                NULL;
            END $$;
            """ + "\n" + app.GetService<IHistoryRepository>().GetCreateIfNotExistsScript() + "\n"
            + app.GetService<IMigrator>().GenerateScript(
                "20261126140856_AddMotor365PauseDays", "20261127142700_AddProtocolRewards",
                MigrationsSqlGenerationOptions.NoTransactions);
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        appSchemaCreated = true;
    }

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable("COP_TEST_DB_CONNECTION");
        if (string.IsNullOrWhiteSpace(configured))
        {
            using var designDb = new WhatsAppDbContextFactory().CreateDbContext([]);
            configured = designDb.Database.GetConnectionString()!;
        }
        var connection = new NpgsqlConnectionStringBuilder(configured);
        if (connection.Host is not ("localhost" or "127.0.0.1" or "::1"))
            throw new InvalidOperationException("Los tests WhatsApp solo crean bases desechables en PostgreSQL local.");
        connection.Database = "postgres";
        adminConnection = connection.ConnectionString;
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
        connection.Database = name;
        ConnectionString = connection.ConnectionString;
        await using var db = CreateDb();
        await db.Database.MigrateAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrEmpty(adminConnection)) return;
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", admin);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
