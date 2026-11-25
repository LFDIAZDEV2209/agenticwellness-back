using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CoppAddresd.Application.Features.LegalDocuments;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Migrations;
using CoppAddresd.Infrastructure.Persistence;
using CoppAddresd.Infrastructure.Services;
using CoppAddresd.Auth.Configuration;
using CoppAddresd.Auth.Controllers;
using CoppAddresd.Auth.Data;
using CoppAddresd.Auth.Interfaces;
using CoppAddresd.Auth.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;
using AuthEntities = CoppAddresd.Auth.Entities;

namespace CoppAddresd.IntegrationTests;

/// <summary>Only a unique disposable loopback database; a few synthetic legal rows, no external services.</summary>
public sealed class LegalConsentPersistenceTests : IAsyncLifetime
{
    private string database = "";
    private string connection = "";
    private string server = "";
    private readonly Guid user = Guid.NewGuid();
    private readonly LegalConsentSettings config = new() { EnforcementEnabled = true };
    private readonly IOptionsMonitor<LegalConsentSettings> settings = Substitute.For<IOptionsMonitor<LegalConsentSettings>>();

    public async Task InitializeAsync()
    {
        settings.CurrentValue.Returns(config);
        server = Environment.GetEnvironmentVariable("COP_TEST_DB_CONNECTION") ?? "";
        if (server.Length == 0) return;
        var source = new NpgsqlConnectionStringBuilder(server);
        if (source.Host is not ("127.0.0.1" or "localhost" or "::1"))
            throw new InvalidOperationException("Consent tests require a loopback PostgreSQL server.");
        database = $"copp_consent_test_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(server) { Database = "postgres" }.ConnectionString);
        await admin.OpenAsync();
        await using (var command = new NpgsqlCommand($"CREATE DATABASE \"{database}\" TEMPLATE template0", admin))
            await command.ExecuteNonQueryAsync();
        connection = new NpgsqlConnectionStringBuilder(server) { Database = database }.ConnectionString;
        await using var db = Context();
        await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA app; CREATE SCHEMA erp;");
        // Apply exact production migration operations; the audit-role GRANT is not needed by this
        // disposable owner-only fixture and would otherwise require a cluster-wide app_user role.
        await Apply(db, new InitialAuditSchema(), up: true, skipGrants: true);
        await Apply(db, new AddLegalDocuments(), up: true);
        await Apply(db, new AddLegalDocumentsAudit(), up: true);
        await Apply(db, new AddLegalConsentReceipts(), up: true);
    }

    public async Task DisposeAsync()
    {
        if (database.Length == 0) return;
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(server) { Database = "postgres" }.ConnectionString);
        await admin.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", admin);
        await command.ExecuteNonQueryAsync();
    }

    private AppDbContext Context()
    {
        var actor = Substitute.For<IAuditActorContext>();
        actor.ActorType.Returns(AuditActorType.User);
        actor.UserId.Returns(user);
        actor.RequestId.Returns("synthetic-consent-request");
        return new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connection, p => p.EnableRetryOnFailure())
            .AddInterceptors(new AuditTriggerInterceptor(actor)).Options);
    }

    private LegalConsentService Service(AppDbContext db) => new(db, settings, TimeProvider.System);

    [NutritionPostgresFact]
    public async Task Paid_signup_validates_the_published_pair_and_rolls_back_partial_writes()
    {
        await using var db = Context();
        var config = new PaidActivationDemoSettings(new("http://127.0.0.1:5174"), TimeSpan.FromMinutes(10))
        {
            AppOrigin = new("http://127.0.0.1:5173"),
            AccountDocuments = new Dictionary<string, PaidAccountLegalDocumentCodes>
            {
                ["es"] = new() { Terms = "fixture-account-terms", Privacy = "fixture-account-privacy" }
            }
        };
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var services = builder.Services;
        services.AddDbContext<AuthDbContext>(options => options.UseNpgsql(connection));
        services.AddIdentityCore<AuthEntities.ApplicationUser>().AddRoles<AuthEntities.ApplicationRole>()
            .AddEntityFrameworkStores<AuthDbContext>();
        services.AddControllers().AddApplicationPart(typeof(PaidActivationDemoController).Assembly);
        services.AddAuthorization();
        services.AddRateLimiter(options => options.AddFixedWindowLimiter("auth", limiter =>
        {
            limiter.PermitLimit = 100;
            limiter.Window = TimeSpan.FromMinutes(1);
        }));
        services.AddSingleton(config);
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton(Substitute.For<IPatientAccessGuard>());
        services.AddSingleton<PaidActivationLinks>();
        services.AddScoped<PaidActivationService>();
        services.AddScoped<PaidActivationSignupService>();
        await using var app = builder.Build();
        app.UseRateLimiter();
        app.MapControllers();
        await using var fixtureScope = app.Services.CreateAsyncScope();
        var fixture = fixtureScope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await fixture.Database.ExecuteSqlRawAsync(fixture.Database.GenerateCreateScript());
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE app.patient_profiles (
                id uuid PRIMARY KEY, user_id uuid NOT NULL REFERENCES auth."Users"("Id"),
                first_name text, last_name text, email text, emergency_contact text, status text,
                created_at timestamptz, deleted_at timestamptz
            );
            """);

        var limit = DateTime.UtcNow.AddMinutes(10);
        var activation = new AuthEntities.PaidActivation
        {
            PaymentId = Guid.NewGuid(), Email = "paid-legal@example.invalid", Plan = "monthly", Language = "es",
            AmountCents = 1996, EmailRevision = 1, ConfirmedAt = DateTime.UtcNow,
            LastEmailRequestedAt = DateTime.UtcNow, RedeemedAt = DateTime.UtcNow,
            SetupSessionId = Guid.NewGuid(), SetupSessionExpiresAt = limit
        };
        fixture.PaidActivations.Add(activation);
        fixture.Applications.Add(new() { Id = Guid.NewGuid(), Code = "app", Name = "Test app" });
        await fixture.SaveChangesAsync();
        var proof = new PaidSetupProof(activation.PaymentId, activation.SetupSessionId.Value, 1, limit);
        LegalDocument AddDocument(string code)
        {
            var doc = new LegalDocument { Id = Guid.NewGuid(), Code = code, Title = code };
            var version = new LegalDocumentVersion { Id = Guid.NewGuid(), DocumentId = doc.Id, Major = 1,
                IsPublished = true, Content = "Synthetic account document." };
            doc.CurrentVersionId = version.Id;
            db.LegalDocuments.Add(doc);
            db.LegalDocumentVersions.Add(version);
            return doc;
        }
        var terms = AddDocument(config.AccountDocuments["es"].Terms);
        var privacy = AddDocument(config.AccountDocuments["es"].Privacy);
        await db.SaveChangesAsync();

        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var cookie = app.Services.GetRequiredService<PaidActivationLinks>().IssueSetup(proof);
        client.DefaultRequestHeaders.Add("Cookie", $"copp_paid_setup_demo={cookie}");
        client.DefaultRequestHeaders.Add("User-Agent", "Test browser");

        async Task<(HttpStatusCode Status, string? Result)> Complete(params Guid[] versions)
        {
            using var response = await client.PostAsJsonAsync("/api/auth/paid-activation/demo/complete",
                new PaidActivationDemoController.CompleteSetup("Fixture", "Signup", "Fixture contact",
                    "+12025550101", "Synthetic1!", versions));
            if (response.StatusCode == HttpStatusCode.InternalServerError) return (response.StatusCode, null);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            return (response.StatusCode, json.GetProperty(response.IsSuccessStatusCode ? "status" : "code").GetString());
        }

        var oldTerms = terms.CurrentVersionId!.Value;
        var privacyId = privacy.CurrentVersionId!.Value;
        Assert.Equal((HttpStatusCode.Conflict, "document_version_changed"), await Complete(oldTerms));
        Assert.Equal((HttpStatusCode.Conflict, "document_version_changed"), await Complete(oldTerms, oldTerms));
        Assert.Empty(await fixture.Users.AsNoTracking().ToListAsync());
        Assert.Empty(await db.LegalDocumentAcceptances.AsNoTracking().ToListAsync());

        var latest = new LegalDocumentVersion { Id = Guid.NewGuid(), DocumentId = terms.Id,
            Major = 2, IsPublished = true, Content = "Updated synthetic text." };
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var publish = await db.Database.BeginTransactionAsync();
            db.LegalDocumentVersions.Add(latest);
            terms.CurrentVersionId = latest.Id;
            await db.SaveChangesAsync();
            var concurrentOldRequest = Complete(oldTerms, privacyId);
            await publish.CommitAsync();
            Assert.Equal((HttpStatusCode.Conflict, "document_version_changed"), await concurrentOldRequest);
        });

        await db.Database.ExecuteSqlRawAsync("ALTER TABLE app.patient_profiles ADD CONSTRAINT fixture_reject_write CHECK (false)");
        Assert.Equal(HttpStatusCode.InternalServerError, (await Complete(latest.Id, privacyId)).Status);
        Assert.Empty(await fixture.Users.AsNoTracking().ToListAsync());
        Assert.Empty(await db.LegalDocumentAcceptances.AsNoTracking().ToListAsync());
        Assert.Null(await fixture.PaidActivations.AsNoTracking().Select(item => item.LinkedUserId).SingleAsync());
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE app.patient_profiles DROP CONSTRAINT fixture_reject_write");

        var results = await Task.WhenAll(Complete(latest.Id, privacyId), Complete(latest.Id, privacyId));
        Assert.All(results, result => Assert.Equal((HttpStatusCode.OK, "linked"), result));
        var created = await fixture.Users.AsNoTracking().SingleAsync();
        Assert.Equal(created.Id, await fixture.PaidActivations.AsNoTracking().Select(item => item.LinkedUserId).SingleAsync());
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM app.patient_profiles").SingleAsync());
        var receipts = await db.LegalDocumentAcceptances.AsNoTracking().OrderBy(item => item.Scope).ToListAsync();
        Assert.Equal(new[] { AccountLegalScopes.Privacy, AccountLegalScopes.Terms }, receipts.Select(item => item.Scope));
        Assert.Equal(new[] { privacyId, latest.Id }, receipts.Select(item => item.LegalDocumentVersionId));
        Assert.All(receipts, receipt =>
        {
            Assert.Equal(created.Id, receipt.UserId);
            Assert.Equal("es", receipt.Locale);
            Assert.Contains("127.0.0.1", receipt.DisclosureSnapshot);
            Assert.Contains("Test browser", receipt.DisclosureSnapshot);
        });
        var apiConfig = new AccountLegalConsentSettings { EnforcementEnabled = true };
        apiConfig.Documents["es"] = new()
        {
            Terms = config.AccountDocuments["es"].Terms, Privacy = config.AccountDocuments["es"].Privacy
        };
        var apiSettings = Substitute.For<IOptionsMonitor<AccountLegalConsentSettings>>();
        apiSettings.CurrentValue.Returns(apiConfig);
        var account = new AccountLegalConsentService(db, apiSettings, TimeProvider.System);
        var journey = new WellnessJourneyStore(db, TimeProvider.System, account);
        var access = await journey.GetAccessAsync(created.Id, default);
        Assert.NotNull(access);
        Assert.Equal(activation.PaymentId, access.PaymentId);
        Assert.True(access.ConsentCurrent);
    }

    [NutritionPostgresFact]
    public async Task Account_receipt_serializes_concurrent_retries_and_records_metadata()
    {
        await using var db = Context();
        var configuration = new AccountLegalConsentSettings { EnforcementEnabled = true };
        configuration.Documents["en"] = new() { Terms = "fixture-terms-en", Privacy = "fixture-privacy-en" };
        LegalDocumentVersion Add(string code)
        {
            var document = new LegalDocument { Id = Guid.NewGuid(), Code = code, Title = code };
            var version = new LegalDocumentVersion { Id = Guid.NewGuid(), DocumentId = document.Id,
                Major = 1, IsPublished = true, Content = "Synthetic test data." };
            document.CurrentVersionId = version.Id;
            db.LegalDocuments.Add(document);
            db.LegalDocumentVersions.Add(version);
            return version;
        }
        var terms = Add(configuration.Documents["en"].Terms);
        Add(configuration.Documents["en"].Privacy);
        await db.SaveChangesAsync();
        var monitor = Substitute.For<IOptionsMonitor<AccountLegalConsentSettings>>();
        monitor.CurrentValue.Returns(configuration);
        async Task<LegalConsentReceiptDto> Accept()
        {
            await using var context = Context();
            var service = new AccountLegalConsentService(context, monitor, TimeProvider.System);
            return await service.AcceptAsync(user, new(terms.Id, "en", Guid.NewGuid()),
                new("192.0.2.10", "Test client"), default);
        }

        var results = await Task.WhenAll(Accept(), Accept());
        Assert.Equal(results[0], results[1]);
        var receipt = await db.LegalDocumentAcceptances.AsNoTracking().SingleAsync();
        Assert.Equal(terms.Id, receipt.LegalDocumentVersionId);
        Assert.Equal(user, receipt.UserId);
        Assert.Equal("en", receipt.Locale);
        Assert.Contains("192.0.2.10", receipt.DisclosureSnapshot);
        Assert.Contains("Test client", receipt.DisclosureSnapshot);
        var audit = await db.ActivityLogs.AsNoTracking().SingleAsync(row => row.TableName == "legal_document_acceptances");
        Assert.Equal(user, audit.UserId);
    }

    private static async Task Apply(AppDbContext db, Migration migration, bool up, bool skipGrants = false)
    {
        var operations = (up ? migration.UpOperations : migration.DownOperations)
            .Where(op => !skipGrants || op is not SqlOperation sql || !sql.Sql.TrimStart().StartsWith("GRANT", StringComparison.Ordinal)).ToArray();
        foreach (var command in db.GetService<IMigrationsSqlGenerator>().Generate(operations, db.Model))
            await db.Database.ExecuteSqlRawAsync(command.CommandText);
    }

    [NutritionPostgresFact]
    public async Task Concurrent_acceptance_is_one_receipt_with_actor_audit_and_withdrawal_blocks_reuse()
    {
        await using var db = Context();
        var doc = new LegalDocument { Id = Guid.NewGuid(), Code = "synthetic-disclosure-es", Title = "Synthetic" };
        var version = new LegalDocumentVersion { Id = Guid.NewGuid(), DocumentId = doc.Id, Major = 1,
            Content = "Synthetic fixture only", IsPublished = true, CreatedAt = DateTime.UtcNow };
        doc.CurrentVersionId = version.Id;
        db.LegalDocuments.Add(doc); db.LegalDocumentVersions.Add(version);
        await db.SaveChangesAsync();
        config.Scopes["chat"] = new() { Approved = true, ProcessingRevision = "synthetic-v1",
            Providers = ["synthetic-provider"], Documents = new() { ["es"] = doc.Code } };
        var status = (await Service(db).GetStatusAsync(user, "es", default)).Scopes.Single(s => s.Scope == "chat");
        var request = new AcceptLegalConsentRequest("chat", version.Id, "es", status.DisclosureId!, Guid.NewGuid());
        async Task<LegalConsentReceiptDto> Accept()
        {
            await using var context = Context();
            return await Service(context).AcceptAsync(user, request, default);
        }
        var receipts = await Task.WhenAll(Accept(), Accept());
        Assert.Equal(receipts[0].Id, receipts[1].Id);
        Assert.Equal(1, await db.LegalDocumentAcceptances.CountAsync());
        var audit = await db.ActivityLogs.AsNoTracking().SingleAsync(x => x.TableName == "legal_document_acceptances");
        Assert.Equal(user, audit.UserId);
        Assert.Equal("synthetic-consent-request", audit.RequestId);
        config.Scopes["chat"].Providers = ["synthetic-provider-revision"];
        var superseded = (await Service(db).GetStatusAsync(user, "es", default)).Scopes.Single(s => s.Scope == "chat");
        Assert.False(superseded.Accepted);
        Assert.Equal(receipts[0].Id, superseded.ReceiptId);
        Assert.Null((await Service(db).GetStatusAsync(Guid.NewGuid(), "es", default)).Scopes.Single(s => s.Scope == "chat").ReceiptId);
        await Service(db).WithdrawAsync(user, superseded.ReceiptId!.Value, default);
        Assert.Null((await Service(db).GetStatusAsync(user, "es", default)).Scopes.Single(s => s.Scope == "chat").ReceiptId);
        Assert.Equal(403, (await Assert.ThrowsAsync<AiConsentException>(() => Service(db).EnsureUserAsync(user, "chat", default))).StatusCode);
        Assert.NotNull((await Service(db).AcceptAsync(user, request, default)).WithdrawnAt);
        Assert.Equal(2, await db.ActivityLogs.CountAsync(x => x.TableName == "legal_document_acceptances"));
    }

    [NutritionPostgresFact]
    public async Task Migration_roundtrip_preserves_existing_legal_documents_and_published_content_cannot_change()
    {
        await using var db = Context();
        var document = Guid.NewGuid(); var version = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO erp.legal_documents (id,code,title) VALUES ({document},'synthetic-es','Synthetic')");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO erp.legal_document_versions (id,document_id,major,minor,is_published,content) VALUES ({version},{document},1,0,true,'Synthetic immutable text')");
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE erp.legal_document_versions SET content='Changed' WHERE id={version}"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        await Apply(db, new AddLegalConsentReceipts(), up: false);
        Assert.Equal(1, await db.LegalDocuments.CountAsync());
        Assert.Equal("Synthetic immutable text", (await db.LegalDocumentVersions.SingleAsync()).Content);
        await Apply(db, new AddLegalConsentReceipts(), up: true);
        Assert.Equal(0, await db.LegalDocumentAcceptances.CountAsync());
    }
}
