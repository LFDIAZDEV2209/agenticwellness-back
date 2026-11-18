using CoppAddresd.Auth.Authorization;
using CoppAddresd.Auth.Configuration;
using CoppAddresd.Auth.Constants;
using CoppAddresd.Auth.Data;
using CoppAddresd.Auth.Entities;
using CoppAddresd.Auth.Interfaces;
using CoppAddresd.Auth.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoppAddresd.Auth.Controllers;

/// <summary>
/// Invitaciones de primer acceso (onboarding del profesional). El ERP crea el
/// usuario sin password vía el endpoint interno; el profesional acepta con un
/// enlace de un solo uso y establece su propia contraseña (nunca se envían
/// credenciales por correo).
/// </summary>
[ApiController]
public class InvitationsController(
    IInvitationService invitations,
    UserManager<ApplicationUser> userManager,
    AuthDbContext dbContext,
    IOptions<EmailSettings> emailSettings) : ControllerBase
{
    private readonly EmailSettings _emailSettings = emailSettings.Value;

    /// <summary>
    /// Crea el usuario (sin password) + acceso ERP + invitación y encola el
    /// correo. DeferWelcome espera la confirmación del ERP tras aplicar sus scopes. Lo invoca el ERP con <c>X-Internal-Key</c>. El token se
    /// devuelve SOLO con el provider "Log" (dev); en producción la única vía
    /// es el correo.
    ///
    /// Si <c>adoptExisting</c> es true y el usuario ya existe:
    ///   - Si está inactivo → 409 (reactivar antes de usar).
    ///   - Si está activo sin password (estado invitado) → se reutiliza la invitación pendiente
    ///     o crea otra; la entrega usa el outbox.
    ///   - Si está activo con password → se vincula directamente sin nueva
    ///     invitación.
    /// </summary>
    [HttpPost("api/auth/internal/invitations")]
    [AllowAnonymous]
    [RequireInternalKey]
    public async Task<ActionResult<object>> CreateInvitation(
        [FromBody] CreateInvitationInternalRequest request,
        CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var existingUser = await userManager.FindByEmailAsync(email);

        // Fallar antes de crear credenciales/invitaciones si no se podrá entrar.
        if (!await dbContext.Applications.AnyAsync(a => a.Code == ApplicationCodes.Erp && a.IsActive, ct))
            return Conflict(new { message = "La aplicación ERP no está disponible. No se creó la invitación." });

        if (existingUser is not null)
        {
            if (!request.AdoptExisting)
            {
                return Conflict(new { message = $"Ya existe un usuario con el correo '{email}'." });
            }

            // adoptExisting: usuario ya registrado — verificar estado.
            if (!existingUser.IsActive)
            {
                return Conflict(new
                {
                    message = $"Ya existe un usuario inactivo con el correo '{email}'. Reactívalo antes de asignarle un puesto."
                });
            }

            await using var existingTransaction = await dbContext.Database.BeginTransactionAsync(ct);
            // Asegurar acceso ERP (el usuario existente podría no tenerlo).
            var accessError = await ApplicationAccessProvisioning.EnsureAsync(dbContext, existingUser.Id, ApplicationCodes.Erp, ct);
            if (accessError is not null) return Conflict(new { message = accessError });

            if (!await userManager.HasPasswordAsync(existingUser))
            {
                // Un reintento HTTP/adopción no revoca un enlace ya entregado ni duplica su correo.
                // Para emitir otro enlace existe la acción explícita Resend.
                var pending = await dbContext.Invitations.AsNoTracking().Where(i => i.UserId == existingUser.Id &&
                    i.UsedAt == null && i.RevokedAt == null && i.ExpiresAt > DateTime.UtcNow)
                    .OrderByDescending(i => i.CreatedAt).FirstOrDefaultAsync(ct);
                if (pending is not null && await dbContext.WelcomeEmailDeliveries.AnyAsync(x => x.InvitationId == pending.Id, ct))
                {
                    await existingTransaction.CommitAsync(ct);
                    return Ok(new { userId = existingUser.Id, adopted = true, hasPassword = false,
                        invitationId = pending.Id, expiresAt = pending.ExpiresAt, link = (string?)null });
                }
                // Estado invitado (sin password): crear nueva invitación y enviar correo.
                var (success, error, invitation, token) = await invitations.CreateAsync(existingUser.Id, request.CreatedBy, ct);
                if (!success || invitation is null || token is null)
                {
                    return BadRequest(new { message = error ?? "No se pudo crear la invitación." });
                }

                var link = $"{_emailSettings.FrontendUrl}/invitaciones?token={token}";
                await WelcomeEmailQueue.EnqueueAsync(dbContext, existingUser.Id, invitation.Id, !request.DeferWelcome, ct: ct);
                await existingTransaction.CommitAsync(ct);

                return Ok(new
                {
                    userId = existingUser.Id,
                    adopted = true,
                    hasPassword = false,
                    invitationId = invitation.Id,
                    expiresAt = invitation.ExpiresAt,
                    link = IsLogProvider() ? link : null,
                });
            }

            // Ya tiene password: bienvenida sin enlace para crear otra contraseña.
            await WelcomeEmailQueue.EnqueueAsync(dbContext, existingUser.Id, ready: !request.DeferWelcome, ct: ct);
            await existingTransaction.CommitAsync(ct);
            return Ok(new
            {
                userId = existingUser.Id,
                adopted = true,
                hasPassword = true,
                invitationId = (Guid?)null,
                expiresAt = (DateTime?)null,
                link = (string?)null,
            });
        }

        // ── Usuario nuevo ──────────────────────────────────────────────

        await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            FirstName = request.FirstName.Trim(),
            LastName = request.LastName.Trim(),
            IsActive = true,
            EmailConfirmed = false,
        };

        var createResult = await userManager.CreateAsync(user);
        if (!createResult.Succeeded)
        {
            var errors = string.Join(", ", createResult.Errors.Select(e => e.Description));
            return BadRequest(new { message = errors });
        }

        // Acceso a la aplicación ERP (puede iniciar sesión tras aceptar).
        var newAccessError = await ApplicationAccessProvisioning.EnsureAsync(dbContext, user.Id, ApplicationCodes.Erp, ct);
        if (newAccessError is not null) return Conflict(new { message = newAccessError });

        var (newSuccess, newError, newInvitation, newToken) = await invitations.CreateAsync(user.Id, request.CreatedBy, ct);
        if (!newSuccess || newInvitation is null || newToken is null)
        {
            return BadRequest(new { message = newError ?? "No se pudo crear la invitación." });
        }

        await WelcomeEmailQueue.EnqueueAsync(dbContext, user.Id, newInvitation.Id, !request.DeferWelcome, ct: ct);
        await transaction.CommitAsync(ct);

        var newLink = $"{_emailSettings.FrontendUrl}/invitaciones?token={newToken}";

        return Ok(new
        {
            userId = user.Id,
            adopted = false,
            hasPassword = false,
            invitationId = newInvitation.Id,
            expiresAt = newInvitation.ExpiresAt,
            // Solo en dev (provider Log) se expone el enlace; en producción el
            // correo es la única vía.
            link = IsLogProvider() ? newLink : null,
        });
    }

    /// <summary>Valida el token (página pública de aceptación). No lo consume.</summary>
    [HttpGet("api/auth/invitations/validate")]
    [AllowAnonymous]
    public async Task<ActionResult<object>> Validate([FromQuery] string token, CancellationToken ct)
    {
        var (valid, error, invitation) = await invitations.ValidateAsync(token, ct);
        if (!valid || invitation is null)
        {
            return Ok(new { valid = false, error });
        }

        var user = await userManager.FindByIdAsync(invitation.UserId.ToString());
        return Ok(new
        {
            valid = true,
            email = user?.Email,
            firstName = user?.FirstName,
            lastName = user?.LastName,
            expiresAt = invitation.ExpiresAt,
        });
    }

    /// <summary>Acepta la invitación: establece el password y la marca usada.</summary>
    [HttpPost("api/auth/invitations/accept")]
    [AllowAnonymous]
    public async Task<ActionResult<object>> Accept(
        [FromBody] AcceptInvitationRequest request,
        CancellationToken ct)
    {
        var (success, error) = await invitations.AcceptAsync(request.Token, request.Password, ct);
        if (!success)
        {
            return BadRequest(new { message = error });
        }

        return Ok(new { success = true });
    }

    /// <summary>Revoca la invitación pendiente. Lo invoca el ERP con X-Internal-Key
    /// como compensación del flujo de creación de profesionales si falla la
    /// aplicación de scopes.</summary>
    [HttpPost("api/auth/internal/invitations/{id:guid}/revoke")]
    [AllowAnonymous]
    [RequireInternalKey]
    public async Task<IActionResult> RevokeInternal(Guid id, CancellationToken ct)
    {
        var (success, error) = await invitations.RevokeAsync(id, ct);
        if (!success)
            return BadRequest(new { message = error });

        return NoContent();
    }

    [HttpPost("api/auth/invitations/{id:guid}/resend")]
    [Authorize]
    [RequireErpAudience]
    [RequirePermission(PermissionCodes.UsersUpdate)]
    public async Task<ActionResult<object>> Resend(Guid id, CancellationToken ct)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
        var (success, error, invitation, token) = await invitations.ResendAsync(id, GetCallerId(), ct);
        if (!success || invitation is null || token is null)
        {
            return BadRequest(new { message = error ?? "No se pudo reenviar la invitación." });
        }

        var user = await userManager.FindByIdAsync(invitation.UserId.ToString());
        var link = $"{_emailSettings.FrontendUrl}/invitaciones?token={token}";
        if (user is not null)
        {
            await WelcomeEmailQueue.EnqueueAsync(dbContext, user.Id, invitation.Id, resend: true, ct: ct);
        }

        await transaction.CommitAsync(ct);
        return Ok(new
        {
            invitationId = invitation.Id,
            expiresAt = invitation.ExpiresAt,
            link = IsLogProvider() ? link : null,
        });
    }

    [HttpPost("api/auth/invitations/{id:guid}/revoke")]
    [Authorize]
    [RequireErpAudience]
    [RequirePermission(PermissionCodes.UsersUpdate)]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        var (success, error) = await invitations.RevokeAsync(id, ct);
        if (!success)
        {
            return BadRequest(new { message = error });
        }

        return NoContent();
    }

    private bool IsLogProvider()
        => _emailSettings.Provider.Equals("Log", StringComparison.OrdinalIgnoreCase);

    private Guid? GetCallerId()
    {
        var claim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }
}

public record CreateInvitationInternalRequest(
    string Email,
    string FirstName,
    string LastName,
    Guid? CreatedBy = null,
    bool AdoptExisting = false,
    bool DeferWelcome = false);

public record AcceptInvitationRequest(string Token, string Password);
