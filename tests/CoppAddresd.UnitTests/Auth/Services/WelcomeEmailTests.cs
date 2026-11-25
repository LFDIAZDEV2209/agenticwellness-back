using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CoppAddresd.Auth.Configuration;
using CoppAddresd.Auth.Controllers;
using CoppAddresd.Auth.Data;
using CoppAddresd.Auth.Entities;
using CoppAddresd.Auth.Interfaces;
using CoppAddresd.Auth.Services;
using CoppAddresd.Auth.Services.Cache;
using CoppAddresd.UnitTests.Auth.TestDoubles;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoppAddresd.UnitTests.Auth.Services;

public sealed class WelcomeEmailTests
{
    private static readonly EmailSettings Settings = new()
    {
        Provider = "Smtp",
        FrontendUrl = "https://erp.coppadresd.com",
    };

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Template_OnlyShowsEnabledApplications_AndOfficialOrPlaceholderDownloads(
        bool app,
        bool erp
    )
    {
        var mail = WelcomeEmailTemplate.Build(
            new WelcomeEmailContent(
                "ana@example.invalid",
                "Ana",
                "es",
                app,
                erp,
                ["Professional"],
                [new("Personas", ["Consultar"])]
            ),
            Settings
        );
        Assert.Equal(erp, mail.HtmlBody.Contains("Entrar al ERP"));
        Assert.Equal(app, mail.HtmlBody.Contains("iOS &#183; Pr&#243;ximamente"));
        Assert.Equal(app, mail.PlainTextBody!.Contains("Android · Próximamente"));
        if (!erp)
            Assert.DoesNotContain("ERP", mail.PlainTextBody);
        if (!app)
            Assert.DoesNotContain("Android", mail.HtmlBody);
        Assert.DoesNotContain("apps.apple.com", mail.HtmlBody);
        Assert.Contains($"src=\"{Settings.WelcomeLogoUrl}\"", mail.HtmlBody);
        Assert.Contains("COPP-ADRESD", mail.HtmlBody);
        Assert.Empty(mail.InlineImages ?? []);
    }

    [Fact]
    public void Template_English_EscapesUserContent_RejectsUnsafeLinks()
    {
        var mail = WelcomeEmailTemplate.Build(
            new WelcomeEmailContent(
                "ana@example.invalid",
                "<script>x</script>",
                "en-US",
                true,
                false,
                ["<Admin>"],
                []
            ),
            new EmailSettings
            {
                WelcomeLogoUrl = "http://localhost/logo.png",
                IosDownloadUrl = "javascript:alert(1)",
                AndroidDownloadUrl = "https://play.google.com/store/apps/details?id=example",
            }
        );
        Assert.Equal("Welcome to Copp Adresd", mail.Subject);
        Assert.DoesNotContain("<script>", mail.HtmlBody);
        Assert.Contains("&lt;script&gt;", mail.HtmlBody);
        Assert.DoesNotContain("javascript:", mail.HtmlBody);
        Assert.DoesNotContain("localhost/logo.png", mail.HtmlBody);
        Assert.Contains("Download for Android", mail.HtmlBody);
        Assert.Contains("iOS · Coming soon", mail.PlainTextBody);
    }

    [Fact]
    public async Task Queue_RollbackCreatesNoDelivery_AndRepeatedSuccessfulRequestsCreateOne()
    {
        var (db, connection) = IdentityTestDoubles.CreateSqliteDbContext();
        await using var disposeDb = db;
        await using var disposeConnection = connection;
        await db.Database.EnsureCreatedAsync();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "ana",
            Email = "ana@example.invalid",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await WelcomeEmailQueue.EnqueueAsync(db, user.Id);
            await transaction.RollbackAsync();
        }
        db.ChangeTracker.Clear();
        Assert.Empty(await db.WelcomeEmailDeliveries.ToListAsync());
        await WelcomeEmailQueue.EnqueueAsync(db, user.Id);
        await WelcomeEmailQueue.EnqueueAsync(db, user.Id);
        Assert.Single(await db.WelcomeEmailDeliveries.ToListAsync());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Dispatcher_ReadsDefinitiveAccessAndPermissions_AndDoesNotSendTwice(
        bool app,
        bool erp
    )
    {
        await using var fixture = await Fixture.Create(app, erp);
        var clinic = Guid.NewGuid();
        var role = new ApplicationRole
        {
            Id = Guid.NewGuid(),
            Name = "Professional",
            IsActive = true,
        };
        var view = new Permission
        {
            Id = Guid.NewGuid(),
            Code = "Patients.View",
            Name = "View patients",
            Module = "Patients",
        };
        var deleted = new Permission
        {
            Id = Guid.NewGuid(),
            Code = "Patients.Delete",
            Name = "Delete patients",
            Module = "Patients",
        };
        fixture.Db.AddRange(role, view, deleted);
        fixture.Db.ScopedRoleAssignments.Add(
            new()
            {
                Id = Guid.NewGuid(),
                UserId = fixture.User.Id,
                RoleId = role.Id,
                ScopeType = "Clinic",
                ScopeId = clinic,
            }
        );
        fixture.Db.RolePermissions.AddRange(
            new RolePermission { RoleId = role.Id, PermissionId = view.Id },
            new RolePermission { RoleId = role.Id, PermissionId = deleted.Id }
        );
        fixture.Db.ScopedPermissionAssignments.Add(
            new()
            {
                Id = Guid.NewGuid(),
                UserId = fixture.User.Id,
                PermissionId = deleted.Id,
                ScopeType = "Clinic",
                ScopeId = clinic,
                Effect = "Deny",
            }
        );
        await fixture.Db.SaveChangesAsync();
        await WelcomeEmailQueue.EnqueueAsync(fixture.Db, fixture.User.Id, ready: false);
        var delivery = await fixture.Db.WelcomeEmailDeliveries.SingleAsync();
        await fixture.Dispatch();
        Assert.Empty(fixture.Sender.Messages);
        var controller = new WelcomeEmailsController(fixture.Db);
        Assert.IsType<NoContentResult>(
            await controller.Complete(fixture.User.Id, new(null), default)
        );
        await fixture.Dispatch();
        await fixture.Dispatch();
        var mail = Assert.Single(fixture.Sender.Messages);
        Assert.Equal(erp, mail.PlainTextBody!.Contains("ERP"));
        Assert.Equal(app, mail.PlainTextBody.Contains("Android"));
        if (erp)
            Assert.Contains("Personas: Consultar", mail.PlainTextBody);
        Assert.DoesNotContain("Eliminar", mail.PlainTextBody);
        await fixture.Db.Entry(delivery).ReloadAsync();
        Assert.Equal("Sent", delivery.Status);
        Assert.Equal(1, delivery.Attempts);
    }

    [Fact]
    public async Task Dispatcher_FailureSurvivesRestart_RetryUsesSameUserAndValidInvitation()
    {
        await using var fixture = await Fixture.Create(false, true);
        var invitation = new Invitation
        {
            Id = Guid.NewGuid(),
            UserId = fixture.User.Id,
            TokenHash = "initial",
            ExpiresAt = DateTime.UtcNow.AddHours(72),
        };
        fixture.Db.Invitations.Add(invitation);
        await fixture.Db.SaveChangesAsync();
        await WelcomeEmailQueue.EnqueueAsync(fixture.Db, fixture.User.Id, invitation.Id);
        fixture.Sender.Fail = true;
        await fixture.Dispatch();
        fixture.Db.ChangeTracker.Clear();
        var delivery = await fixture.Db.WelcomeEmailDeliveries.SingleAsync();
        Assert.Equal("Pending", delivery.Status);
        Assert.Equal("DeliveryFailed", delivery.LastErrorCode);
        delivery.NextAttemptAt = DateTime.UtcNow.AddMinutes(-1);
        await fixture.Db.SaveChangesAsync();
        fixture.Sender.Fail = false;
        await fixture.Dispatch();
        var mail = Assert.Single(fixture.Sender.Messages);
        var token = Regex.Match(mail.PlainTextBody!, @"token=([A-Za-z0-9_-]+)").Groups[1].Value;
        Assert.NotEmpty(token);
        invitation = await fixture.Db.Invitations.AsNoTracking().SingleAsync();
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant(),
            invitation.TokenHash
        );
        Assert.Single(await fixture.Db.Users.ToListAsync());
        Assert.Single(await fixture.Db.Invitations.ToListAsync());
        Assert.DoesNotContain(token, System.Text.Json.JsonSerializer.Serialize(delivery));
    }

    [Fact]
    public async Task Dispatcher_SkipsSuspendedAccessAndRevokedInvitations()
    {
        await using var fixture = await Fixture.Create(false, true);
        var access = await fixture.Db.UserApplications.SingleAsync();
        access.IsSuspended = true;
        await fixture.Db.SaveChangesAsync();
        await WelcomeEmailQueue.EnqueueAsync(fixture.Db, fixture.User.Id);
        await fixture.Dispatch();
        Assert.Empty(fixture.Sender.Messages);
        Assert.Equal(
            "NoActiveAccess",
            (await fixture.Db.WelcomeEmailDeliveries.SingleAsync()).LastErrorCode
        );
        access = await fixture.Db.UserApplications.SingleAsync();
        access.IsSuspended = false;
        var invitation = new Invitation
        {
            Id = Guid.NewGuid(),
            UserId = fixture.User.Id,
            TokenHash = "revoked",
            ExpiresAt = DateTime.UtcNow.AddHours(72),
            RevokedAt = DateTime.UtcNow,
        };
        fixture.Db.Invitations.Add(invitation);
        await fixture.Db.SaveChangesAsync();
        await WelcomeEmailQueue.EnqueueAsync(fixture.Db, fixture.User.Id, invitation.Id);
        await fixture.Dispatch();
        Assert.Empty(fixture.Sender.Messages);
        Assert.Equal(
            "InvitationUnavailable",
            (await fixture.Db.WelcomeEmailDeliveries.SingleAsync()).LastErrorCode
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppUser_LaterInvitedToErp_GetsOnePasswordInvitationAfterFinalization(
        bool previousDeliveryStillProcessing
    )
    {
        await using var fixture = await Fixture.Create(true, false);
        await WelcomeEmailQueue.EnqueueAsync(fixture.Db, fixture.User.Id);
        await fixture.Dispatch();
        Assert.Single(fixture.Sender.Messages);
        if (previousDeliveryStillProcessing)
        {
            var previous = await fixture.Db.WelcomeEmailDeliveries.SingleAsync();
            previous.Status = "Processing";
            previous.SentAt = null;
            await fixture.Db.SaveChangesAsync();
        }
        var erp = new CoppAddresd.Auth.Entities.Application
        {
            Id = Guid.NewGuid(),
            Code = "erp",
            Name = "ERP",
            IsActive = true,
        };
        fixture.Db.Applications.Add(erp);
        fixture.Db.UserApplications.Add(new() { UserId = fixture.User.Id, ApplicationId = erp.Id });
        var invitation = new Invitation
        {
            Id = Guid.NewGuid(),
            UserId = fixture.User.Id,
            TokenHash = "initial",
            ExpiresAt = DateTime.UtcNow.AddHours(72),
        };
        fixture.Db.Invitations.Add(invitation);
        await fixture.Db.SaveChangesAsync();
        await WelcomeEmailQueue.EnqueueAsync(
            fixture.Db,
            fixture.User.Id,
            invitation.Id,
            ready: false
        );
        await WelcomeEmailQueue.EnqueueAsync(
            fixture.Db,
            fixture.User.Id,
            invitation.Id,
            ready: false
        );
        Assert.Equal(2, await fixture.Db.WelcomeEmailDeliveries.CountAsync());
        Assert.IsType<NoContentResult>(
            await new WelcomeEmailsController(fixture.Db).Complete(
                fixture.User.Id,
                new(invitation.Id),
                default
            )
        );
        fixture.Db.ChangeTracker.Clear();
        var id = await fixture
            .Db.WelcomeEmailDeliveries.Where(x => x.InvitationId == invitation.Id)
            .Select(x => x.Id)
            .SingleAsync();
        var dispatcher = new WelcomeEmailDispatcher(
            fixture.Db,
            new ScopedPermissionService(fixture.Db, new NoCacheService()),
            fixture.Sender,
            Options.Create(Settings),
            NullLogger<WelcomeEmailDispatcher>.Instance
        );
        await dispatcher.DispatchAsync(id, default);
        await dispatcher.DispatchAsync(id, default);
        Assert.Equal(2, fixture.Sender.Messages.Count);
        Assert.Contains("Crear mi contrase&#241;a", fixture.Sender.Messages.Last().HtmlBody);
        Assert.Contains("ERP", fixture.Sender.Messages.Last().PlainTextBody);
        Assert.Contains("Android", fixture.Sender.Messages.Last().PlainTextBody);
    }

    [Fact]
    public async Task RepeatedOtpLogin_CannotClearOrFinalizePendingErpInvitation()
    {
        await using var fixture = await Fixture.Create(true, true);
        var invitation = new Invitation
        {
            Id = Guid.NewGuid(),
            UserId = fixture.User.Id,
            TokenHash = "initial",
            ExpiresAt = DateTime.UtcNow.AddHours(72),
        };
        fixture.Db.Invitations.Add(invitation);
        await fixture.Db.SaveChangesAsync();
        await WelcomeEmailQueue.EnqueueAsync(
            fixture.Db,
            fixture.User.Id,
            invitation.Id,
            ready: false
        );
        await WelcomeEmailQueue.EnqueueAsync(fixture.Db, fixture.User.Id);
        var delivery = await fixture.Db.WelcomeEmailDeliveries.SingleAsync();
        Assert.Equal(invitation.Id, delivery.InvitationId);
        Assert.Null(delivery.ReadyAt);
        await fixture.Dispatch();
        Assert.Empty(fixture.Sender.Messages);
    }

    [Fact]
    public async Task LogProvider_DoesNotInvalidateDevLinkOrClaimActualDelivery()
    {
        await using var fixture = await Fixture.Create(false, true);
        var invitation = new Invitation
        {
            Id = Guid.NewGuid(),
            UserId = fixture.User.Id,
            TokenHash = "original-dev-token-hash",
            ExpiresAt = DateTime.UtcNow.AddHours(72),
        };
        fixture.Db.Invitations.Add(invitation);
        await fixture.Db.SaveChangesAsync();
        await WelcomeEmailQueue.EnqueueAsync(fixture.Db, fixture.User.Id, invitation.Id);
        var deliveryId = await fixture.Db.WelcomeEmailDeliveries.Select(x => x.Id).SingleAsync();
        fixture.Db.ChangeTracker.Clear();
        var dispatcher = new WelcomeEmailDispatcher(
            fixture.Db,
            new ScopedPermissionService(fixture.Db, new NoCacheService()),
            fixture.Sender,
            Options.Create(new EmailSettings { Provider = "Log" }),
            NullLogger<WelcomeEmailDispatcher>.Instance
        );
        await dispatcher.DispatchAsync(deliveryId, default);
        Assert.Equal(
            "original-dev-token-hash",
            (await fixture.Db.Invitations.SingleAsync()).TokenHash
        );
        var delivery = await fixture.Db.WelcomeEmailDeliveries.SingleAsync();
        Assert.Equal("Logged", delivery.Status);
        Assert.Null(delivery.SentAt);
    }

    private sealed class Sender : IEmailSender
    {
        public bool Fail;
        public List<EmailMessage> Messages { get; } = [];

        public Task SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            if (Fail)
                throw new IOException("mail transport unavailable");
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required AuthDbContext Db;
        public required Microsoft.Data.Sqlite.SqliteConnection Connection;
        public ApplicationUser User = new()
        {
            Id = Guid.NewGuid(),
            Email = "ana@example.invalid",
            UserName = "ana",
            FirstName = "Ana",
            IsActive = true,
        };
        public Sender Sender = new();

        public static async Task<Fixture> Create(bool app, bool erp)
        {
            var (db, connection) = IdentityTestDoubles.CreateSqliteDbContext();
            var fixture = new Fixture { Db = db, Connection = connection };
            await db.Database.EnsureCreatedAsync();
            db.Users.Add(fixture.User);
            foreach (var code in new[] { app ? "app" : null, erp ? "erp" : null }.OfType<string>())
            {
                var application = new CoppAddresd.Auth.Entities.Application
                {
                    Id = Guid.NewGuid(),
                    Code = code,
                    Name = code,
                    IsActive = true,
                };
                db.Applications.Add(application);
                db.UserApplications.Add(
                    new() { UserId = fixture.User.Id, ApplicationId = application.Id }
                );
            }
            await db.SaveChangesAsync();
            return fixture;
        }

        public async Task Dispatch()
        {
            Db.ChangeTracker.Clear();
            var id = await Db.WelcomeEmailDeliveries.Select(x => x.Id).SingleAsync();
            var dispatcher = new WelcomeEmailDispatcher(
                Db,
                new ScopedPermissionService(Db, new NoCacheService()),
                Sender,
                Options.Create(Settings),
                NullLogger<WelcomeEmailDispatcher>.Instance
            );
            await dispatcher.DispatchAsync(id, default);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
