using System.Text;
using CoppAddresd.Auth.Configuration;
using CoppAddresd.Auth.Constants;
using CoppAddresd.Auth.Data;
using CoppAddresd.Auth.Entities;
using CoppAddresd.Auth.Interfaces;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoppAddresd.Auth.Services;

public enum PasswordResetResult
{
    InvalidLink,
    PasswordPolicy,
    Completed,
}

public sealed class EmailPasswordResetService(
    AuthDbContext db,
    UserManager<ApplicationUser> users,
    IPatientAccessGuard patientAccess,
    PasswordResetEmailLink links,
    IEmailSender sender,
    IOptions<EmailSettings> emailSettings
)
{
    internal async Task DeliverAsync(
        string normalizedEmail,
        string language,
        Uri destination,
        CancellationToken ct
    )
    {
        var accounts = await db.Users.AsNoTracking()
            .Where(u => u.NormalizedEmail == normalizedEmail)
            .Take(2)
            .ToListAsync(ct);
        if (accounts.Count != 1 || !await CanRecoverAsync(accounts[0], ct))
            return;

        var user = accounts[0];
        var link = await links.IssuePublicAsync(user, destination, language);
        var message = TransactionalEmailTemplate.Build(
            new("PWD-01", user.Email!, language, new Dictionary<string, string?>
            {
                ["enlace_restablecer"] = link,
            }),
            emailSettings.Value);
        await sender.SendAsync(message, ct);
    }

    public async Task<PasswordResetResult> ConfirmAsync(
        Guid userId,
        string? encodedToken,
        string? newPassword,
        CancellationToken ct
    )
    {
        var token = Decode(encodedToken);
        if (token is null)
            return PasswordResetResult.InvalidLink;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Identity invalida el sello tras cambiar la contraseña. El bloqueo
        // obliga a la segunda redención a leer ese sello ya actualizado.
        var user = await db.Users.FromSqlInterpolated(
                $"""SELECT * FROM auth."Users" WHERE "Id" = {userId} FOR UPDATE""")
            .SingleOrDefaultAsync(ct);
        if (user is null || !await CanRecoverAsync(user, ct))
            return PasswordResetResult.InvalidLink;

        if (string.IsNullOrEmpty(newPassword) || newPassword.Length > 128)
        {
            var valid = await users.VerifyUserTokenAsync(
                user,
                users.Options.Tokens.PasswordResetTokenProvider,
                UserManager<ApplicationUser>.ResetPasswordTokenPurpose,
                token);
            return valid ? PasswordResetResult.PasswordPolicy : PasswordResetResult.InvalidLink;
        }

        if (user.LockoutEnabled)
        {
            user.LockoutEnd = null;
            user.AccessFailedCount = 0;
        }

        var reset = await users.ResetPasswordAsync(user, token, newPassword);
        if (!reset.Succeeded)
            return reset.Errors.All(e => e.Code.StartsWith("Password", StringComparison.Ordinal))
                ? PasswordResetResult.PasswordPolicy
                : PasswordResetResult.InvalidLink;

        var revokedAt = DateTime.UtcNow;
        await db.RefreshTokens.Where(t => t.UserId == user.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, revokedAt), ct);
        // Un refresh concurrente puede insertar después de la revocación. La
        // versión desactualizada impide que esa sesión se renueve de nuevo.
        await db.UserApplications.Where(a => a.UserId == user.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.SessionVersion, a => a.SessionVersion + 1), ct);
        await transaction.CommitAsync(ct);
        return PasswordResetResult.Completed;
    }

    private async Task<bool> CanRecoverAsync(ApplicationUser user, CancellationToken ct) =>
        user.IsActive
        && user.EmailConfirmed
        && !string.IsNullOrWhiteSpace(user.Email)
        && await db.UserApplications.AnyAsync(
            a => a.UserId == user.Id
                && !a.IsSuspended
                && a.Application.IsActive
                && a.Application.Code == ApplicationCodes.App,
            ct)
        && !await patientAccess.IsBlockedAsync(user.Id, ct);

    private static string? Decode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096)
            return null;

        try
        {
            return Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(value));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
