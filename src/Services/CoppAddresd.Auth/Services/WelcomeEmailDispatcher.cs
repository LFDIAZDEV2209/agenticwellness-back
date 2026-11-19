using System.Security.Cryptography;
using System.Text;
using CoppAddresd.Auth.Configuration;
using CoppAddresd.Auth.Data;
using CoppAddresd.Auth.Entities;
using CoppAddresd.Auth.Interfaces;
using CoppAddresd.Auth.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoppAddresd.Auth.Services;

public sealed class WelcomeEmailDispatcher(
    AuthDbContext db,
    IScopedPermissionService permissions,
    IEmailSender sender,
    IOptions<EmailSettings> options,
    ILogger<WelcomeEmailDispatcher> logger
)
{
    public async Task DispatchAsync(Guid id, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var settings = options.Value;
        if (!settings.WelcomeEnabled)
            return;
        var maxAttempts = Math.Clamp(settings.WelcomeMaxAttempts, 1, 20);
        // Compare-and-swap: solo una réplica reclama este envío. No hay red dentro de una transacción.
        var claimed = await db
            .WelcomeEmailDeliveries.Where(x =>
                x.Id == id
                && x.ReadyAt != null
                && x.Attempts < maxAttempts
                && (
                    x.Status == "Pending" && x.NextAttemptAt <= now
                    || x.Status == "Processing" && x.LeaseUntil <= now
                )
            )
            .ExecuteUpdateAsync(
                s =>
                    s.SetProperty(x => x.Status, "Processing")
                        .SetProperty(x => x.LeaseUntil, now.AddMinutes(5))
                        .SetProperty(x => x.Attempts, x => x.Attempts + 1),
                ct
            );
        if (claimed == 0)
            return;
        var delivery = await db.WelcomeEmailDeliveries.SingleAsync(x => x.Id == id, ct);
        try
        {
            if (delivery.PatientId is Guid patientId)
            {
                var content = await PatientWelcomeQueue.ResolveAsync(db, patientId, ct);
                if (content is null)
                {
                    delivery.Status = "Skipped";
                    delivery.LastErrorCode = "PatientUnavailable";
                    delivery.LeaseUntil = null;
                    await db.SaveChangesAsync(ct);
                }
                else
                {
                    await SendAsync(delivery, content, settings, ct);
                }
                return;
            }

            var user = await db
                .Users.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == delivery.UserId, ct);
            if (user is null || !user.IsActive || string.IsNullOrWhiteSpace(user.Email))
            {
                delivery.Status = "Skipped";
                delivery.LastErrorCode = "UserUnavailable";
                await db.SaveChangesAsync(ct);
                return;
            }
            var apps = await db
                .UserApplications.AsNoTracking()
                .Where(x => x.UserId == user.Id && !x.IsSuspended && x.Application.IsActive)
                .Select(x => x.Application.Code)
                .ToListAsync(ct);
            if (!apps.Contains("app") && !apps.Contains("erp"))
            {
                delivery.Status = "Skipped";
                delivery.LastErrorCode = "NoActiveAccess";
                await db.SaveChangesAsync(ct);
                return;
            }
            var lang = await db
                .UserPreferences.Where(x => x.UserId == user.Id)
                .Select(x => x.Lang)
                .FirstOrDefaultAsync(ct);
            var roles = await db
                .UserRoles.Where(x => x.UserId == user.Id)
                .Join(db.Roles.Where(x => x.IsActive), x => x.RoleId, r => r.Id, (_, r) => r.Name!)
                .ToListAsync(ct);
            roles.AddRange(
                await db
                    .ScopedRoleAssignments.Where(x => x.UserId == user.Id && x.Role.IsActive)
                    .Select(x => x.Role.Name!)
                    .ToListAsync(ct)
            );

            var capabilities = new List<WelcomeCapability>();
            if (apps.Contains("erp"))
            {
                // El evaluador real aplica Grant/Deny. No usar la unión laxa de /me ni inferir permisos por nombre del rol.
                var contexts = await db
                    .ScopedRoleAssignments.Where(x => x.UserId == user.Id)
                    .Select(x => new { x.ScopeType, x.ScopeId })
                    .Union(
                        db.ScopedPermissionAssignments.Where(x => x.UserId == user.Id)
                            .Select(x => new { x.ScopeType, x.ScopeId })
                    )
                    .ToListAsync(ct);
                var scopes = contexts
                    .Select(x => new ScopeEntry(x.ScopeType, x.ScopeId))
                    .Append(ScopeEntry.Global);
                var allowed = new HashSet<string>(StringComparer.Ordinal);
                foreach (var scope in scopes.Distinct())
                {
                    allowed.UnionWith(
                        await permissions.GetEffectivePermissionsAsync(
                            user.Id,
                            scope == ScopeEntry.Global ? [scope] : [scope, ScopeEntry.Global],
                            ct
                        )
                    );
                }
                var catalog = await db
                    .Permissions.AsNoTracking()
                    .Where(x => allowed.Contains(x.Code))
                    .OrderBy(x => x.Module)
                    .ThenBy(x => x.Code)
                    .Select(x => new
                    {
                        x.Module,
                        x.Code,
                        x.Name,
                    })
                    .ToListAsync(ct);
                foreach (var group in catalog.GroupBy(x => x.Module))
                    capabilities.Add(
                        new WelcomeCapability(
                            WelcomePermissionLabels.Module(group.Key, lang),
                            group
                                .Select(x => WelcomePermissionLabels.Action(x.Code, x.Name, lang))
                                .Distinct()
                                .ToArray()
                        )
                    );
            }

            string? invitationUrl = null;
            DateTime? expiresAt = null;
            if (delivery.InvitationId is Guid invitationId)
            {
                var invitation = await db.Invitations.SingleOrDefaultAsync(
                    x => x.Id == invitationId && x.UserId == user.Id,
                    ct
                );
                if (
                    invitation is null
                    || invitation.RevokedAt != null
                    || invitation.UsedAt != null
                    || invitation.ExpiresAt <= now
                )
                {
                    delivery.Status = "Skipped";
                    delivery.LastErrorCode = "InvitationUnavailable";
                    await db.SaveChangesAsync(ct);
                    return;
                }
                // El token existe solo durante este intento; al reintentar se rota, sin persistir secretos recuperables.
                var token = Convert
                    .ToBase64String(RandomNumberGenerator.GetBytes(32))
                    .TrimEnd('=')
                    .Replace('+', '-')
                    .Replace('/', '_');
                // Log no entrega el cuerpo: conservar el enlace dev retornado por el alta.
                if (!settings.Provider.Equals("Log", StringComparison.OrdinalIgnoreCase))
                    invitation.TokenHash = Convert
                        .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))
                        .ToLowerInvariant();
                invitationUrl = settings.FrontendUrl.TrimEnd('/') + "/invitaciones?token=" + token;
                expiresAt = invitation.ExpiresAt;
                await db.SaveChangesAsync(ct);
            }
            await SendAsync(
                delivery,
                new WelcomeEmailContent(
                    user.Email,
                    user.FirstName,
                    lang,
                    apps.Contains("app"),
                    apps.Contains("erp"),
                    roles.Distinct().Order().ToArray(),
                    capabilities,
                    invitationUrl,
                    expiresAt
                ),
                settings,
                ct
            );
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // El lease permite recuperar tras caída/cancelación sin adelantar un envío concurrente.
            throw;
        }
        catch (Exception ex)
        {
            delivery.Status = delivery.Attempts >= maxAttempts ? "Failed" : "Pending";
            delivery.LastErrorCode =
                ex is InvalidOperationException ? "InvalidConfigurationOrState" : "DeliveryFailed";
            delivery.NextAttemptAt = DateTime.UtcNow.AddMinutes(
                Math.Min(60, Math.Pow(2, delivery.Attempts))
            );
            delivery.LeaseUntil = null;
            await db.SaveChangesAsync(ct);
            // SMTP puede incluir destinatarios o enlaces en el mensaje de error: no registrar su texto.
            logger.LogWarning(
                "Bienvenida {DeliveryId}: fallo {ErrorType}, intento {Attempt}",
                delivery.Id,
                ex.GetType().Name,
                delivery.Attempts
            );
        }
    }

    private async Task SendAsync(
        WelcomeEmailDelivery delivery,
        WelcomeEmailContent content,
        EmailSettings settings,
        CancellationToken ct)
    {
        await sender.SendAsync(WelcomeEmailTemplate.Build(content, settings), ct);
        delivery.Status = settings.Provider.Equals("Log", StringComparison.OrdinalIgnoreCase)
            ? "Logged"
            : "Sent";
        delivery.SentAt = delivery.Status == "Sent" ? DateTime.UtcNow : null;
        delivery.LastErrorCode = null;
        delivery.LeaseUntil = null;
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Bienvenida {DeliveryId}: {Status}, intento {Attempt}",
            delivery.Id,
            delivery.Status,
            delivery.Attempts
        );
    }
}

public sealed class WelcomeEmailWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<WelcomeEmailWorker> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var settings = scope
                    .ServiceProvider.GetRequiredService<IOptions<EmailSettings>>()
                    .Value;
                if (!settings.WelcomeEnabled)
                    continue;
                var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
                try
                {
                    await PatientWelcomeQueue.TransferPendingAsync(db, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("Transferencia de bienvenidas de paciente pendiente: {ErrorType}", ex.GetType().Name);
                }
                var now = DateTime.UtcNow;
                var max = Math.Clamp(settings.WelcomeMaxAttempts, 1, 20);
                await db
                    .WelcomeEmailDeliveries.Where(x =>
                        x.Status == "Processing" && x.LeaseUntil <= now && x.Attempts >= max
                    )
                    .ExecuteUpdateAsync(
                        s =>
                            s.SetProperty(x => x.Status, "Failed")
                                .SetProperty(x => x.LastErrorCode, "LeaseExpired"),
                        stoppingToken
                    );
                var ids = await db
                    .WelcomeEmailDeliveries.AsNoTracking()
                    .Where(x =>
                        x.ReadyAt != null
                        && x.Attempts < max
                        && (
                            x.Status == "Pending" && x.NextAttemptAt <= now
                            || x.Status == "Processing" && x.LeaseUntil <= now
                        )
                    )
                    .OrderBy(x => x.NextAttemptAt)
                    .Select(x => x.Id)
                    .Take(20)
                    .ToListAsync(stoppingToken);
                foreach (var id in ids)
                {
                    using var dispatchScope = scopeFactory.CreateScope();
                    await dispatchScope
                        .ServiceProvider.GetRequiredService<WelcomeEmailDispatcher>()
                        .DispatchAsync(id, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    "Outbox de bienvenida no disponible: {ErrorType}",
                    ex.GetType().Name
                );
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
