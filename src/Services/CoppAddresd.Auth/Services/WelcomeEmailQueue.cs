using CoppAddresd.Auth.Data;
using CoppAddresd.Auth.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Auth.Services;

public static class WelcomeEmailQueue
{
    /// <summary>Se llama dentro de la transacción del alta, después de guardar accesos y roles.</summary>
    public static async Task EnqueueAsync(
        AuthDbContext db,
        Guid userId,
        Guid? invitationId = null,
        bool ready = true,
        bool resend = false,
        CancellationToken ct = default
    )
    {
        var key = resend ? $"invitation:{invitationId}" : $"welcome:{userId}";
        var delivery = await db.WelcomeEmailDeliveries.SingleOrDefaultAsync(
            x => x.DeduplicationKey == key,
            ct
        );
        if (
            !resend
            && invitationId.HasValue
            && delivery is not null
            && (
                delivery.SentAt != null
                || delivery.Status == "Logged"
                || delivery.Status == "Processing" && delivery.InvitationId != invitationId
            )
        )
        {
            // Una cuenta OTP ya recibió bienvenida de app; su primer acceso ERP sí necesita una invitación.
            await EnqueueAsync(db, userId, invitationId, ready, resend: true, ct: ct);
            return;
        }
        if (delivery is null)
        {
            var queued = new WelcomeEmailDelivery
            {
                UserId = userId,
                DeduplicationKey = key,
                InvitationId = invitationId,
                ReadyAt = ready ? DateTime.UtcNow : null,
            };
            if (db.Database.IsNpgsql())
            {
                // Dos verificaciones OTP concurrentes pueden llegar a la misma cuenta: la unicidad no debe romper el login.
                var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO auth."WelcomeEmailDeliveries"
                    ("Id", "UserId", "DeduplicationKey", "InvitationId", "CreatedAt", "ReadyAt", "Status", "Attempts", "NextAttemptAt")
                    VALUES ({queued.Id}, {userId}, {key}, {invitationId}, {queued.CreatedAt}, {queued.ReadyAt}, 'Pending', 0, {queued.NextAttemptAt})
                    ON CONFLICT ("DeduplicationKey") DO NOTHING
                    """,
                    ct
                );
                if (inserted == 1)
                    return;
                delivery = await db.WelcomeEmailDeliveries.SingleAsync(
                    x => x.DeduplicationKey == key,
                    ct
                );
            }
            else
                db.WelcomeEmailDeliveries.Add(queued);
        }
        // Los logins OTP repetidos no pueden quitar una invitación ni adelantar una confirmación ERP pendiente.
        if (delivery is not null && invitationId is null)
            return;
        if (
            delivery is not null
            && delivery.SentAt is null
            && delivery.Status is not ("Processing" or "Logged")
            && (delivery.Status != "Failed" || delivery.InvitationId != invitationId)
        )
        {
            if (delivery.InvitationId != invitationId)
                delivery.Attempts = 0;
            delivery.InvitationId = invitationId;
            delivery.ReadyAt = ready ? DateTime.UtcNow : null;
            delivery.Status = "Pending";
            delivery.NextAttemptAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }
}
