using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CoppAddresd.Auth.Configuration;
using CoppAddresd.Auth.Constants;
using CoppAddresd.Auth.Data;
using CoppAddresd.Auth.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoppAddresd.Auth.Services;

public sealed record PaidSetupDocument(string Code, string Title, string Content, Guid VersionId);
public sealed record PaidSetupView(Guid PaymentId, string Status, string Email,
    IReadOnlyList<PaidSetupDocument>? Documents = null, string? AppUrl = null);
public sealed record PaidSignupDetails(
    string FirstName, string LastName, string EmergencyName, string EmergencyPhone,
    string Password, IReadOnlyList<Guid> VersionIds);
public sealed record PaidSignupResult(PaidSetupView? View, string? ErrorCode = null);

public sealed partial class PaidActivationSignupService(
    AuthDbContext db,
    UserManager<ApplicationUser> users,
    PaidActivationDemoSettings settings)
{
    public async Task<PaidSetupView?> GetAsync(PaidSetupProof proof, CancellationToken ct)
    {
        var activation = await db.PaidActivations.AsNoTracking()
            .SingleOrDefaultAsync(row => row.PaymentId == proof.PaymentId, ct);
        if (activation is null || !proof.Matches(activation))
            return null;
        if (activation.RedeemedAt is null)
            return new(activation.PaymentId, "welcome", activation.Email);
        if (!proof.OwnsRedeemedSession(activation))
            return null;
        if (activation.LinkedUserId is not null)
            return Linked(activation);
        var normalized = users.NormalizeEmail(activation.Email);
        if (await db.Users.AsNoTracking().AnyAsync(user => user.NormalizedEmail == normalized, ct))
            return new(activation.PaymentId, "sign_in_required", activation.Email);
        var documents = await ReadDocumentsAsync(activation.Language, lockPublication: false, ct);
        return new(activation.PaymentId, "setup_required", activation.Email, documents);
    }

    public async Task<PaidSignupResult> CompleteAsync(
        PaidSetupProof proof, PaidSignupDetails request, string? ip, string userAgent, CancellationToken ct)
    {
        var firstName = request.FirstName.Trim();
        var lastName = request.LastName.Trim();
        var emergencyName = request.EmergencyName.Trim();
        var emergencyPhone = request.EmergencyPhone.Trim().Replace(" ", "", StringComparison.Ordinal);
        if (firstName.Length == 0 || lastName.Length == 0 || emergencyName.Length == 0
            || !LocalPhone().IsMatch(emergencyPhone))
            return new(null, "invalid_contact_details");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var activation = await db.PaidActivations
            .FromSqlInterpolated($"""SELECT * FROM auth."PaidActivations" WHERE "PaymentId" = {proof.PaymentId} FOR UPDATE""")
            .SingleOrDefaultAsync(ct);
        if (activation is null || !proof.OwnsRedeemedSession(activation))
            return new(null, "invalid_activation_link");
        if (activation.LinkedUserId is not null)
            return new(Linked(activation));

        var documents = await ReadDocumentsAsync(activation.Language, lockPublication: true, ct);
        if (documents.Count != 2)
            return new(null, "consent_unavailable");
        if (request.VersionIds.Count != 2 || !request.VersionIds.ToHashSet()
                .SetEquals(documents.Select(document => document.VersionId)))
            return new(null, "document_version_changed");

        var normalizedEmail = users.NormalizeEmail(activation.Email);
        if (await db.Users.AnyAsync(user => user.NormalizedEmail == normalizedEmail, ct))
            return new(null, "sign_in_required");
        var appId = await db.Applications.AsNoTracking()
            .Where(app => app.Code == ApplicationCodes.App && app.IsActive)
            .Select(app => (Guid?)app.Id).SingleOrDefaultAsync(ct);
        if (appId is null || settings.AppUrl is null)
            return new(null, "journey_unavailable");

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = activation.Email,
            Email = activation.Email,
            EmailConfirmed = true,
            FirstName = firstName,
            LastName = lastName
        };
        try
        {
            var result = await users.CreateAsync(user, request.Password);
            if (!result.Succeeded)
                return new(null, result.Errors.Any(error => error.Code.StartsWith("Duplicate", StringComparison.Ordinal))
                    ? "sign_in_required" : "password_policy");
        }
        catch (DbUpdateException error) when (
            error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return new(null, "sign_in_required");
        }

        db.UserApplications.Add(new UserApplication { UserId = user.Id, ApplicationId = appId.Value });
        var contact = JsonSerializer.Serialize(new { name = emergencyName, phone = emergencyPhone });
        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO app.patient_profiles
                (id, user_id, first_name, last_name, email, emergency_contact, status, created_at)
            VALUES ({Guid.NewGuid()}, {user.Id}, {firstName}, {lastName}, {activation.Email}, {contact}, 'Activo', {now})
            """, ct);
        foreach (var document in documents)
        {
            var disclosureId = Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes($"{document.Code}:{document.VersionId:D}")));
            var metadata = JsonSerializer.Serialize(new { source = "paid-journey-demo", ip, userAgent });
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO app.legal_document_acceptances
                    (id, user_id, legal_document_version_id, scope, locale, disclosure_id,
                     disclosure_snapshot, idempotency_key, accepted_at)
                VALUES ({Guid.NewGuid()}, {user.Id}, {document.VersionId}, {document.Code}, {activation.Language},
                        {disclosureId}, CAST({metadata} AS jsonb), {Guid.NewGuid()}, {now})
                """, ct);
        }

        activation.LinkedUserId = user.Id;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(Linked(activation));
    }

    private PaidSetupView Linked(PaidActivation activation) =>
        new(activation.PaymentId, "linked", activation.Email, AppUrl: settings.AppUrl);

    private async Task<List<PaidSetupDocument>> ReadDocumentsAsync(
        string language, bool lockPublication, CancellationToken ct)
    {
        var privacy = $"paid-demo-privacy-{language}";
        var terms = $"paid-demo-terms-{language}";
        if (lockPublication)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT id FROM erp.legal_documents
                WHERE code IN ({privacy}, {terms}) ORDER BY code FOR SHARE
                """, ct);
        return await db.Database.SqlQuery<PaidSetupDocument>($"""
            SELECT d.code AS "Code", d.title AS "Title", v.content AS "Content", v.id AS "VersionId"
            FROM erp.legal_documents d
            JOIN erp.legal_document_versions v ON v.id = d.current_version_id AND d.id = v.document_id
            WHERE d.code IN ({privacy}, {terms}) AND v.is_published
            ORDER BY d.code
            """).ToListAsync(ct);
    }

    [GeneratedRegex(@"^\+[1-9][0-9]{7,14}$", RegexOptions.CultureInvariant)]
    private static partial Regex LocalPhone();
}
