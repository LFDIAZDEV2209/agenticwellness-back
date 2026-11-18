using System.Collections.Concurrent;
using CoppAddresd.Auth.Configuration;
using CoppAddresd.Auth.Data;
using CoppAddresd.Auth.Entities;
using CoppAddresd.Auth.Interfaces;
using CoppAddresd.Auth.Services;
using CoppAddresd.Auth.Services.Cache;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CoppAddresd.IntegrationTests;

/// <summary>BD efímera por caso: migraciones reales y carreras de múltiples réplicas.</summary>
public sealed class WelcomeEmailPostgresTests : IAsyncLifetime
{
    private readonly string name = "copp_welcome_test_" + Guid.NewGuid().ToString("N");
    private string admin = null!;
    private DbContextOptions<AuthDbContext> options = null!;
    private bool created;

    public async Task InitializeAsync()
    {
        var source =
            Environment.GetEnvironmentVariable("COP_TEST_DB_CONNECTION")
            ?? throw new InvalidOperationException(
                "Requiere COP_TEST_DB_CONNECTION, nunca usa la BD de desarrollo."
            );
        var builder = new NpgsqlConnectionStringBuilder(source)
        {
            Database = "postgres",
            Pooling = false,
        };
        admin = builder.ConnectionString;
        await using var connection = new NpgsqlConnection(admin);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"CREATE DATABASE \"{name}\" TEMPLATE template0",
            connection
        );
        await command.ExecuteNonQueryAsync();
        created = true;
        builder.Database = name;
        options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql(
                builder.ConnectionString,
                x => x.MigrationsHistoryTable("__ef_migrations_history", "auth")
            )
            .Options;
        await using var db = new AuthDbContext(options);
        await db.Database.MigrateAsync();
    }

    [Fact]
    public async Task ConcurrentEnqueueAndDispatch_OneDeliveryAcrossReplicas()
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "ana",
            Email = "ana@example.invalid",
            FirstName = "Ana",
            IsActive = true,
        };
        await using (var db = new AuthDbContext(options))
        {
            var app = new CoppAddresd.Auth.Entities.Application
            {
                Id = Guid.NewGuid(),
                Code = "erp",
                Name = "ERP",
                IsActive = true,
            };
            db.AddRange(
                user,
                app,
                new UserApplication { UserId = user.Id, ApplicationId = app.Id }
            );
            await db.SaveChangesAsync();
        }
        await Task.WhenAll(
            Enumerable
                .Range(0, 8)
                .Select(async _ =>
                {
                    await using var db = new AuthDbContext(options);
                    await WelcomeEmailQueue.EnqueueAsync(db, user.Id);
                })
        );
        Guid id;
        await using (var db = new AuthDbContext(options))
            id = (await db.WelcomeEmailDeliveries.SingleAsync()).Id;
        var sender = new Sender();
        await Task.WhenAll(
            Enumerable
                .Range(0, 4)
                .Select(async _ =>
                {
                    await using var db = new AuthDbContext(options);
                    await new WelcomeEmailDispatcher(
                        db,
                        new ScopedPermissionService(db, new NoCacheService()),
                        sender,
                        Options.Create(
                            new EmailSettings
                            {
                                Provider = "Smtp",
                                FrontendUrl = "https://erp.coppadresd.com",
                            }
                        ),
                        NullLogger<WelcomeEmailDispatcher>.Instance
                    ).DispatchAsync(id, default);
                })
        );
        Assert.Single(sender.Messages);
        await using var check = new AuthDbContext(options);
        Assert.Equal("Sent", (await check.WelcomeEmailDeliveries.SingleAsync()).Status);
        Assert.Single(await check.Users.ToListAsync());
    }

    [Fact]
    public async Task WelcomeMigration_DownAndUp_PreservesExistingUserData()
    {
        await using var db = new AuthDbContext(options);
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "retained",
            FirstName = "Ana",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var migrations = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.EndsWith("AddWelcomeEmailOutbox", migrations[^1]);
        await db.GetService<IMigrator>().MigrateAsync(migrations[^2]);
        Assert.True(await db.Users.AnyAsync(x => x.Id == user.Id));
        await db.Database.MigrateAsync();
        await WelcomeEmailQueue.EnqueueAsync(db, user.Id);
        Assert.Single(await db.WelcomeEmailDeliveries.ToListAsync());
    }

    [Fact]
    public async Task ApiFinalizationMigration_UpDownAndRetryWithRealPostgres()
    {
        await using var auth = new AuthDbContext(options);
        var apiOptions =
            new DbContextOptionsBuilder<CoppAddresd.Infrastructure.Persistence.AppDbContext>()
                .UseNpgsql(auth.Database.GetConnectionString())
                .Options;
        await using var api = new CoppAddresd.Infrastructure.Persistence.AppDbContext(apiOptions);
        await api.Database.ExecuteSqlRawAsync("CREATE SCHEMA IF NOT EXISTS erp");
        Assert.False(api.Database.HasPendingModelChanges());
        var migration = new CoppAddresd.Infrastructure.Migrations.AddWelcomeEmailFinalizations();
        var generator = api.GetService<IMigrationsSqlGenerator>();
        foreach (var command in generator.Generate(migration.UpOperations, api.Model))
            await api.Database.ExecuteSqlRawAsync(command.CommandText);
        var handler = new CompletionHandler();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://auth.example.invalid"),
        };
        var client = new CoppAddresd.Api.Security.AuthInvitationsClient(
            http,
            Options.Create(new CoppAddresd.Api.Configuration.AuthServiceSettings()),
            api,
            NullLogger<CoppAddresd.Api.Security.AuthInvitationsClient>.Instance
        );
        var userId = Guid.NewGuid();
        await client.CompleteWelcomeAsync(userId, Guid.NewGuid());
        Assert.Null((await api.WelcomeEmailFinalizations.SingleAsync()).CompletedAt);
        handler.Status = System.Net.HttpStatusCode.NoContent;
        await client.FlushWelcomeAsync(userId, default);
        Assert.NotNull((await api.WelcomeEmailFinalizations.SingleAsync()).CompletedAt);
        foreach (var command in generator.Generate(migration.DownOperations, api.Model))
            await api.Database.ExecuteSqlRawAsync(command.CommandText);
        Assert.Empty(await auth.WelcomeEmailDeliveries.ToListAsync());
        foreach (var command in generator.Generate(migration.UpOperations, api.Model))
            await api.Database.ExecuteSqlRawAsync(command.CommandText);
        api.ChangeTracker.Clear();
        Assert.Empty(await api.WelcomeEmailFinalizations.ToListAsync());
    }

    private sealed class CompletionHandler : HttpMessageHandler
    {
        public System.Net.HttpStatusCode Status = System.Net.HttpStatusCode.ServiceUnavailable;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        ) => Task.FromResult(new HttpResponseMessage(Status));
    }

    public async Task DisposeAsync()
    {
        if (!created)
            return;
        await using var connection = new NpgsqlConnection(admin);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"DROP DATABASE \"{name}\" WITH (FORCE)",
            connection
        );
        await command.ExecuteNonQueryAsync();
    }

    private sealed class Sender : IEmailSender
    {
        public ConcurrentBag<EmailMessage> Messages = [];

        public Task SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
