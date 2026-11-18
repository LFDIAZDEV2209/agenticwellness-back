using CoppAddresd.Auth.Authorization;
using CoppAddresd.Auth.Constants;
using CoppAddresd.Auth.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Auth.Controllers;

[ApiController]
public sealed class WelcomeEmailsController(AuthDbContext db) : ControllerBase
{
    [HttpPost("api/auth/internal/users/{userId:guid}/welcome/complete")]
    [AllowAnonymous]
    [RequireInternalKey]
    public async Task<IActionResult> Complete(
        Guid userId,
        [FromBody] CompleteWelcomeRequest request,
        CancellationToken ct
    )
    {
        var delivery = request.InvitationId is Guid invitationId
            ? await db.WelcomeEmailDeliveries.SingleOrDefaultAsync(
                x => x.UserId == userId && x.InvitationId == invitationId,
                ct
            )
            : await db.WelcomeEmailDeliveries.SingleOrDefaultAsync(
                x => x.DeduplicationKey == $"welcome:{userId}",
                ct
            );
        if (delivery is null)
            return NotFound();
        if (delivery.SentAt != null || delivery.ReadyAt != null)
            return NoContent();
        if (delivery.InvitationId != request.InvitationId)
            return Conflict(new { message = "La invitación no corresponde al alta pendiente." });
        if (
            request.InvitationId is Guid id
            && !await db.Invitations.AnyAsync(
                x => x.Id == id && x.RevokedAt == null && x.UsedAt == null,
                ct
            )
        )
            return Conflict(new { message = "La invitación ya no está disponible." });
        delivery.ReadyAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("api/auth/users/{userId:guid}/welcome-emails")]
    [Authorize]
    [RequireErpAudience]
    [RequirePermission(PermissionCodes.SystemAdminSettings)]
    [RequirePermission(PermissionCodes.UsersView)]
    public async Task<IActionResult> Status(Guid userId, CancellationToken ct) =>
        Ok(
            await db
                .WelcomeEmailDeliveries.AsNoTracking()
                .Where(x => x.UserId == userId)
                .OrderByDescending(x => x.CreatedAt)
                .Select(x => new
                {
                    x.Id,
                    x.Status,
                    x.ReadyAt,
                    x.Attempts,
                    x.SentAt,
                    x.NextAttemptAt,
                    x.LastErrorCode,
                })
                .ToListAsync(ct)
        );

    [HttpPost("api/auth/users/{userId:guid}/welcome-emails/{deliveryId:guid}/retry")]
    [Authorize]
    [RequireErpAudience]
    [RequirePermission(PermissionCodes.SystemAdminSettings)]
    [RequirePermission(PermissionCodes.UsersUpdate)]
    public async Task<IActionResult> Retry(Guid userId, Guid deliveryId, CancellationToken ct)
    {
        var changed = await db
            .WelcomeEmailDeliveries.Where(x =>
                x.Id == deliveryId
                && x.UserId == userId
                && x.SentAt == null
                && x.ReadyAt != null
                && (x.Status == "Failed" || x.Status == "Pending" || x.Status == "Logged")
            )
            .ExecuteUpdateAsync(
                s =>
                    s.SetProperty(x => x.Status, "Pending")
                        .SetProperty(x => x.Attempts, 0)
                        .SetProperty(x => x.NextAttemptAt, DateTime.UtcNow)
                        .SetProperty(x => x.LastErrorCode, (string?)null),
                ct
            );
        return changed == 1
            ? Accepted()
            : Conflict(
                new { message = "El envío ya fue procesado o el alta aún no fue confirmada." }
            );
    }
}

public sealed record CompleteWelcomeRequest(Guid? InvitationId);
