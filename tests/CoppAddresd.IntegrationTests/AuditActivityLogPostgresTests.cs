using System.Data.Common;
using CoppAddresd.Application.Features.Audit;
using CoppAddresd.Infrastructure.Persistence;
using CoppAddresd.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace CoppAddresd.IntegrationTests;

/// <summary>Solo usa una BD efímera local y no aplica migraciones ni modifica tablas compartidas.</summary>
public sealed class AuditActivityLogPostgresTests : IAsyncLifetime
{
    private readonly string _database = $"audit_option_b_test_{Guid.NewGuid():N}";
    private string? _adminConnection;
    private string? _testConnection;
    private readonly SqlCapture _capture = new();
    private static readonly DateTimeOffset Instant = DateTimeOffset.Parse("2026-11-17T13:00:00Z");

    public async Task InitializeAsync()
    {
        var source = Environment.GetEnvironmentVariable("COP_TEST_DB_CONNECTION");
        if (string.IsNullOrWhiteSpace(source)) return;
        var settings = new NpgsqlConnectionStringBuilder(source);
        if (settings.Host is not ("localhost" or "127.0.0.1" or "::1"))
            throw new InvalidOperationException("La prueba de auditoría exige PostgreSQL local.");
        settings.Database = "postgres";
        settings.Pooling = false;
        _adminConnection = settings.ConnectionString;
        await using var admin = new NpgsqlConnection(_adminConnection);
        await admin.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE DATABASE {_database}", admin);
        await create.ExecuteNonQueryAsync();
        settings.Database = _database;
        _testConnection = settings.ConnectionString;
        try
        {
            await using var db = Context();
            await db.Database.ExecuteSqlRawAsync("""
                CREATE SCHEMA audit;
                CREATE TABLE audit.activity_logs (
                    id uuid PRIMARY KEY, occurred_at timestamptz NOT NULL, action varchar(32) NOT NULL,
                    schema_name varchar(64) NOT NULL, table_name varchar(64) NOT NULL, record_id text NOT NULL,
                    actor_type varchar(20) NOT NULL, user_id uuid, user_email varchar(320), user_role varchar(100),
                    ip_address varchar(45), request_id varchar(100), correlation_id varchar(100),
                    old_data jsonb, new_data jsonb, changed_data jsonb, metadata jsonb);
                INSERT INTO audit.activity_logs (id,occurred_at,action,schema_name,table_name,record_id,actor_type,user_email,correlation_id,old_data,new_data,changed_data,metadata) VALUES
                ('00000000-0000-0000-0000-000000000001','2026-11-17T12:00:00Z','INSERT','auth','users','record-a','USER','Ana%_@EXAMPLE.test','corr-a',jsonb_build_object('privado',true),jsonb_build_object('privado',true),jsonb_build_object('privado',true),jsonb_build_object('privado',true)),
                ('00000000-0000-0000-0000-000000000002','2026-11-17T13:00:00Z','UPDATE','erp','clinics','record-b','USER','ANA%_@example.test','corr%_b',null,null,null,null),
                ('00000000-0000-0000-0000-000000000003','2026-11-17T13:00:00Z','UPDATE','erp','clinics','record%_c','SYSTEM',null,null,null,null,null,null),
                ('00000000-0000-0000-0000-000000000004','2026-11-17T14:00:00Z','DELETE','tele','appointments','record-d','USER','anaXY@example.test','corr-d',null,null,null,null),
                ('00000000-0000-0000-0000-000000000005','2026-11-17T15:00:00Z','INSERT','app','patients','record-e','ANONYMOUS',null,null,null,null,null,null),
                ('00000000-0000-0000-0000-000000000006','2026-11-17T16:00:00Z','AdaptationApplied','app','programs','record-f','SYSTEM',null,null,null,null,null,null),
                ('00000000-0000-0000-0000-000000000007','2026-11-17T17:00:00Z','Insert','audit','activity_logs','record-g','System',null,null,null,null,null,null);
                """);
            _capture.Commands.Clear();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        if (_adminConnection is null || _testConnection is null) return;
        await using var admin = new NpgsqlConnection(_adminConnection);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_database} WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
        _testConnection = null;
    }

    private AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(_testConnection!).AddInterceptors(_capture).Options);

    [AuditPostgresFact]
    public async Task ConsultaGeneral_IncluyeEsquemasOrdenEstablePaginacionYProyeccionSegura()
    {
        await using var db = Context();
        var handler = new ListAuditActivityLogQueryHandler(new AuditLogRepository(db));
        var first = await handler.Handle(new(PageSize: 3), CancellationToken.None);
        Assert.Equal(7, first.Total); Assert.Equal(3, first.TotalPages);
        Assert.Equal(new[] { 7, 6, 5 }, first.Items.Select(x => int.Parse(x.Id.ToString()[^2..])));
        Assert.Contains(first.Items, x => x.SchemaName == "audit");
        Assert.Equal("INSERT", first.Items[0].Action); Assert.Equal("SYSTEM", first.Items[0].ActorType);
        var second = await handler.Handle(new(Page: 2, PageSize: 3), CancellationToken.None);
        Assert.Equal(new[] { 4, 3, 2 }, second.Items.Select(x => int.Parse(x.Id.ToString()[^2..])));
        var last = await handler.Handle(new(Page: 3, PageSize: 3), CancellationToken.None);
        Assert.Single(last.Items); Assert.Equal("auth", last.Items[0].SchemaName);
        var empty = await handler.Handle(new(Page: int.MaxValue, PageSize: 100), CancellationToken.None);
        Assert.Empty(empty.Items); Assert.Equal(7, empty.Total); Assert.Equal(int.MaxValue, empty.Page);
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.NotEmpty(_capture.Commands);
        Assert.All(_capture.Commands, sql =>
        {
            foreach (var name in new[] { "old_data", "new_data", "changed_data", "metadata", "user_id", "request_id" })
                Assert.DoesNotContain(name, sql, StringComparison.OrdinalIgnoreCase);
        });
    }

    [AuditPostgresFact]
    public async Task FiltrosSql_CombinadosFechasInclusivasActorYBusquedaLiteral()
    {
        await using var db = Context();
        var handler = new ListAuditActivityLogQueryHandler(new AuditLogRepository(db));
        var exact = await handler.Handle(new(Schema: "erp", Table: "clinics", Action: "UPDATE", Actor: " ana%_ ",
            From: Instant.ToOffset(TimeSpan.FromHours(-5)), To: Instant, Search: "corr%_"), CancellationToken.None);
        Assert.Equal(2, int.Parse(Assert.Single(exact.Items).Id.ToString()[^2..]));
        Assert.Equal(1, exact.Total); Assert.Equal(1, exact.TotalPages);
        var byRecord = await handler.Handle(new(Search: "%_"), CancellationToken.None);
        Assert.Equal(new[] { 3, 2 }, byRecord.Items.Select(x => int.Parse(x.Id.ToString()[^2..])));
        var byActor = await handler.Handle(new(Actor: "%_"), CancellationToken.None);
        Assert.Equal(2, byActor.Total);
        var inserts = await handler.Handle(new(Action: "INSERT"), CancellationToken.None);
        Assert.Equal(3, inserts.Total);
        var deletes = await handler.Handle(new(Action: "DELETE"), CancellationToken.None);
        Assert.Single(deletes.Items);
        var none = await handler.Handle(new(Table: "inexistente"), CancellationToken.None);
        Assert.Empty(none.Items); Assert.Equal(0, none.Total); Assert.Equal(0, none.TotalPages);
    }

    [AuditPostgresFact]
    public async Task ConsultaSql_RespetaCancelacion()
    {
        await using var db = Context();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AuditLogRepository(db).ListAsync(new(), cancellation.Token));
    }

    private sealed class SqlCapture : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}

public sealed class AuditPostgresFactAttribute : FactAttribute
{
    public AuditPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COP_TEST_DB_CONNECTION")))
            Skip = "Requiere COP_TEST_DB_CONNECTION local; cada prueba crea y elimina una BD efímera propia.";
    }
}
