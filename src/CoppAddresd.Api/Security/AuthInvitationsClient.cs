using System.Net.Http.Json;
using System.Text.Json;
using CoppAddresd.Api.Configuration;
using CoppAddresd.Application.Interfaces;
using Microsoft.Extensions.Options;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Api.Security;

/// <summary>
/// Cliente del endpoint interno de invitaciones del Auth Service
/// (<c>POST /api/auth/internal/invitations</c>, header X-Internal-Key).
/// Soporta adoptExisting: vincula usuarios existentes sin duplicar.
/// </summary>
public class AuthInvitationsClient(
    HttpClient httpClient,
    IOptions<AuthServiceSettings> settings,
    AppDbContext db,
    ILogger<AuthInvitationsClient> logger) : IAuthInvitationsClient
{
    private readonly AuthServiceSettings _settings = settings.Value;

    public async Task<InvitationCreationResult> CreateInvitationAsync(
        string email,
        string firstName,
        string lastName,
        bool adoptExisting = false,
        CancellationToken ct = default)
    {
        var payload = new { email, firstName, lastName, adoptExisting, deferWelcome = true };

        var response = await httpClient.PostAsJsonAsync(
            "/api/auth/internal/invitations", payload, JsonSerializerOptions.Default, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("Auth invitación falló: {Status} {Body}",
                (int)response.StatusCode, body);
            throw new HttpRequestException(
                $"El Auth Service rechazó la invitación ({response.StatusCode}).",
                null,
                response.StatusCode);
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        var adopted = root.TryGetProperty("adopted", out var adoptedProp) && adoptedProp.GetBoolean();
        var hasPassword = root.TryGetProperty("hasPassword", out var hpProp) && hpProp.GetBoolean();

        // InvitationId y ExpiresAt pueden ser null en el caso adopt + hasPassword.
        var invitationId = Guid.Empty;
        if (root.TryGetProperty("invitationId", out var invIdProp) && invIdProp.ValueKind != JsonValueKind.Null)
        {
            invitationId = invIdProp.GetGuid();
        }

        var expiresAt = DateTime.MinValue;
        if (root.TryGetProperty("expiresAt", out var expProp) && expProp.ValueKind != JsonValueKind.Null)
        {
            expiresAt = expProp.GetDateTimeOffset().UtcDateTime;
        }

        var link = root.TryGetProperty("link", out var linkProp) && linkProp.ValueKind == JsonValueKind.String
            ? linkProp.GetString()
            : null;

        return new InvitationCreationResult(
            root.GetProperty("userId").GetGuid(),
            invitationId,
            expiresAt,
            link,
            adopted,
            hasPassword);
    }

    public async Task CompleteWelcomeAsync(Guid userId, Guid invitationId, CancellationToken ct = default)
    {
        // El registro se guarda DESPUÉS del perfil y los scopes definitivos. Sobrevive a reinicios y fallos HTTP.
        var pending = await db.WelcomeEmailFinalizations.FindAsync([userId], ct);
        if (pending is null)
        {
            pending = new WelcomeEmailFinalization { UserId = userId, InvitationId = invitationId == Guid.Empty ? null : invitationId };
            if (db.Database.IsNpgsql())
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO erp.welcome_email_finalizations
                    (user_id, invitation_id, created_at, next_attempt_at, attempts)
                    VALUES ({pending.UserId}, {pending.InvitationId}, {pending.CreatedAt}, {pending.NextAttemptAt}, 0)
                    ON CONFLICT (user_id) DO NOTHING
                    """, ct);
            }
            else
            {
                db.WelcomeEmailFinalizations.Add(pending);
                await db.SaveChangesAsync(ct);
            }
        }
        else if (pending.InvitationId != (invitationId == Guid.Empty ? (Guid?)null : invitationId))
        {
            pending.InvitationId = invitationId == Guid.Empty ? null : invitationId;
            pending.CompletedAt = null;
            db.Entry(pending).Property(x => x.CompletedAt).IsModified = true;
            pending.NextAttemptAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        await FlushWelcomeAsync(userId, ct);
    }

    public async Task FlushWelcomeAsync(Guid userId, CancellationToken ct)
    {
        var pending = await db.WelcomeEmailFinalizations.SingleAsync(x => x.UserId == userId, ct);
        if (pending.CompletedAt != null) return;
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                $"/api/auth/internal/users/{userId}/welcome/complete", new { pending.InvitationId }, ct);
            response.EnsureSuccessStatusCode();
            pending.CompletedAt = DateTime.UtcNow;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            pending.Attempts++;
            pending.NextAttemptAt = DateTime.UtcNow.AddSeconds(Math.Min(3600, 15 * Math.Pow(2, Math.Min(pending.Attempts, 8))));
            logger.LogWarning("Confirmación de bienvenida pendiente para {UserId}: {ErrorType}", userId, ex.GetType().Name);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task RevokeAsync(Guid invitationId, CancellationToken ct = default)
    {
        var response = await httpClient.PostAsync(
            $"/api/auth/internal/invitations/{invitationId}/revoke", null, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("Auth revocación falló: {Status} {Body}",
                (int)response.StatusCode, body);
            throw new HttpRequestException(
                $"El Auth Service rechazó la revocación ({response.StatusCode}).",
                null,
                response.StatusCode);
        }
    }
}
