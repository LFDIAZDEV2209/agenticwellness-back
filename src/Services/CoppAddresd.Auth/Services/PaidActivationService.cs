using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using CoppAddresd.Auth.Configuration;
using CoppAddresd.Auth.Constants;
using CoppAddresd.Auth.Data;
using CoppAddresd.Auth.Entities;
using CoppAddresd.Auth.Interfaces;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Auth.Services;

public sealed class PaidActivationLinks(
    IDataProtectionProvider protection,
    PaidActivationDemoSettings settings)
{
    private readonly ITimeLimitedDataProtector _tokens = protection
        .CreateProtector("CoppAddresd.Auth.PaidActivationDemo.v1").ToTimeLimitedDataProtector();
    private readonly ITimeLimitedDataProtector _setup = protection
        .CreateProtector("CoppAddresd.Auth.PaidActivationSetupDemo.v1").ToTimeLimitedDataProtector();

    public string Issue(PaidActivation activation)
    {
        var path = activation.Language == "en" ? "/activate-membership" : "/activar-membresia";
        var token = _tokens.Protect(Payload(activation), settings.LinkLifetime);
        return new Uri(settings.WebOrigin!, path)
            + $"#paymentId={activation.PaymentId:D}&token={Uri.EscapeDataString(token)}&lang={activation.Language}";
    }

    public bool IsValid(PaidActivation activation, string token)
    {
        if (token.Length > 2048)
            return false;

        try
        {
            return _tokens.Unprotect(token) == Payload(activation);
        }
        catch (Exception error) when (error is CryptographicException or FormatException)
        {
            return false;
        }
    }

    public string IssueSetup(PaidSetupProof proof) =>
        _setup.Protect(JsonSerializer.Serialize(proof), proof.ExpiresAt);

    public PaidSetupProof? ReadSetup(string? cookie)
    {
        if (string.IsNullOrEmpty(cookie) || cookie.Length > 2048)
            return null;
        try
        {
            return JsonSerializer.Deserialize<PaidSetupProof>(_setup.Unprotect(cookie));
        }
        catch (Exception error) when (error is CryptographicException or FormatException or JsonException)
        {
            return null;
        }
    }

    private static string Payload(PaidActivation activation) =>
        FormattableString.Invariant($"{activation.PaymentId:D}:{activation.EmailRevision}");
}

public sealed record PaidSetupProof(Guid PaymentId, Guid SessionId, int Revision, DateTimeOffset ExpiresAt)
{
    public bool Matches(PaidActivation activation) =>
        activation.PaymentId == PaymentId && activation.EmailRevision == Revision
        && ExpiresAt > DateTimeOffset.UtcNow;

    public bool OwnsRedeemedSession(PaidActivation activation) =>
        Matches(activation) && activation.SetupSessionId == SessionId
        && activation.SetupSessionExpiresAt > DateTime.UtcNow;
}

public sealed record PaidActivationReceipt(Guid PaymentId, string Plan, int AmountCents, string Currency,
    string MembershipStatus, DateTime ExpiresAt, int MembershipRevision, bool MembershipPaid);

public enum PaidActivationState
{
    Invalid,
    SignInRequired,
    SetupRequired,
    Linked,
}

public sealed record PaidActivationResult(PaidActivationState State, string? Email = null);

public sealed class PaidActivationService(
    AuthDbContext db,
    UserManager<ApplicationUser> users,
    IPatientAccessGuard patientAccess,
    PaidActivationLinks links)
{
    public async Task<PaidActivationReceipt> ConfirmAsync(
        Guid paymentId, string email, string plan, string language, bool trial, Guid? checkoutKey, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var amountCents = plan == "annual" ? 11976 : 1996;
        var hash = checkoutKey is { } key ? PaidMembershipDemoService.HashCheckoutKey(key) : null;
        var state = trial ? "trial" : "active";
        var expiresAt = trial ? now.AddDays(PaidMembershipDemoService.TrialDays)
            : PaidMembershipDemoService.AddPeriod(now, plan);
        DateTime? emailAttempt = trial ? null : now;
        // El registro original conserva el pagador y encarga el correo en la misma escritura.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO auth."PaidActivations"
                ("PaymentId", "Email", "Plan", "AmountCents", "Language", "ConfirmedAt", "LastEmailRequestedAt",
                 "NextEmailAttemptAt", "EmailRevision", "EmailAttempts", "MembershipState",
                 "MembershipExpiresAt", "MembershipPaid", "MembershipRevision", "CheckoutKeyHash")
            VALUES ({paymentId}, {email}, {plan}, {amountCents}, {language}, {now}, {now}, {emailAttempt}, 1, 0,
                    {state}, {expiresAt}, {!trial}, 0, {hash})
            ON CONFLICT ("PaymentId") DO NOTHING
            """, ct);
        return (await ReceiptAsync(paymentId, ct))!;
    }

    public async Task<PaidActivationReceipt?> ReceiptAsync(Guid paymentId, CancellationToken ct)
    {
        var row = await db.PaidActivations.AsNoTracking().SingleOrDefaultAsync(x => x.PaymentId == paymentId, ct);
        return row is null ? null : PaidMembershipDemoService.Receipt(row, DateTime.UtcNow);
    }

    public async Task<PaidSetupProof?> PrepareAsync(Guid paymentId, string token, CancellationToken ct)
    {
        var activation = await db.PaidActivations.AsNoTracking()
            .SingleOrDefaultAsync(row => row.PaymentId == paymentId, ct);
        return activation is null || !PaidMembershipDemoService.IsActive(activation, DateTime.UtcNow)
            || activation.RedeemedAt is not null || !links.IsValid(activation, token)
            ? null
            : new(paymentId, Guid.NewGuid(), activation.EmailRevision, DateTimeOffset.UtcNow.AddMinutes(30));
    }

    public async Task ResendAsync(Guid paymentId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var eligibleSince = now.AddSeconds(-5);
        await db.PaidActivations
            .Where(activation =>
                activation.PaymentId == paymentId && activation.MembershipPaid && activation.MembershipState == "active"
                && (activation.MembershipExpiresAt ?? activation.ConfirmedAt.AddMonths(activation.Plan == "annual" ? 12 : 1)) > now
                && (activation.RedeemedAt == null
                    || activation.LinkedUserId == null && activation.SetupSessionExpiresAt <= now)
                && activation.LastEmailRequestedAt <= eligibleSince)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(activation => activation.LastEmailRequestedAt, now)
                .SetProperty(activation => activation.NextEmailAttemptAt, now)
                .SetProperty(activation => activation.EmailLeaseUntil, (DateTime?)null)
                .SetProperty(activation => activation.RedeemedAt, (DateTime?)null)
                .SetProperty(activation => activation.SetupSessionId, (Guid?)null)
                .SetProperty(activation => activation.SetupSessionExpiresAt, (DateTime?)null)
                .SetProperty(activation => activation.EmailRevision, activation => activation.EmailRevision + 1)
                .SetProperty(activation => activation.EmailAttempts, 0),
                ct);
    }

    public async Task<PaidActivationResult> RedeemAsync(
        Guid paymentId, string? token, PaidSetupProof? proof, ClaimsPrincipal principal, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var activation = await db.PaidActivations
            .FromSqlInterpolated($"""SELECT * FROM auth."PaidActivations" WHERE "PaymentId" = {paymentId} FOR UPDATE""")
            .SingleOrDefaultAsync(ct);
        if (activation is null || !PaidMembershipDemoService.IsActive(activation, DateTime.UtcNow))
            return new(PaidActivationState.Invalid);
        var ownsSession = proof?.OwnsRedeemedSession(activation) == true;
        if (ownsSession && activation.LinkedUserId is not null)
            return new(PaidActivationState.Linked);
        if (!ownsSession && (activation.RedeemedAt is not null
            || (proof is not null ? !proof.Matches(activation) : token is null || !links.IsValid(activation, token))))
            return new(PaidActivationState.Invalid);

        var normalizedEmail = users.NormalizeEmail(activation.Email);
        // El correo no puede cambiar mientras se vincula el pago a esa cuenta.
        var accounts = await db.Users
            .FromSqlInterpolated($"""
                SELECT * FROM auth."Users"
                WHERE "NormalizedEmail" = {normalizedEmail}
                LIMIT 2 FOR SHARE
                """)
            .AsNoTracking()
            .Select(user => new { user.Id, user.Email, user.IsActive, user.SecurityStamp })
            .ToListAsync(ct);
        if (accounts.Count > 1)
            return new(PaidActivationState.Invalid);

        var account = accounts.SingleOrDefault();
        if (account is null && ownsSession)
            return new(PaidActivationState.SetupRequired);
        if (account is not null)
        {
            if (principal.Identity?.IsAuthenticated != true || !principal.HasClaim("aud", ApplicationCodes.App))
                return new(PaidActivationState.SignInRequired, account.Email);

            if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var actor)
                || actor != account.Id
                || !account.IsActive
                || account.SecurityStamp != principal.FindFirstValue("security_stamp")
                || !long.TryParse(principal.FindFirstValue("application_session_version"), out var sessionVersion)
                || !await db.UserApplications.AnyAsync(access =>
                    access.UserId == actor
                    && access.Application.Code == ApplicationCodes.App
                    && access.Application.IsActive
                    && !access.IsSuspended
                    && access.SessionVersion == sessionVersion, ct)
                || await patientAccess.IsBlockedAsync(actor, ct))
            {
                return new(PaidActivationState.Invalid);
            }

            activation.LinkedUserId = actor;
        }

        activation.RedeemedAt = DateTime.UtcNow;
        activation.SetupSessionId = proof?.SessionId;
        activation.SetupSessionExpiresAt = proof?.ExpiresAt.UtcDateTime;
        activation.NextEmailAttemptAt = null;
        activation.EmailLeaseUntil = null;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(account is null ? PaidActivationState.SetupRequired : PaidActivationState.Linked);
    }
}
