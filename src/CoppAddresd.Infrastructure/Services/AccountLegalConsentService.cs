using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoppAddresd.Application.Features.LegalDocuments;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoppAddresd.Infrastructure.Services;

public sealed class AccountLegalConsentService(AppDbContext db, IOptionsMonitor<AccountLegalConsentSettings> settings,
    TimeProvider clock) : IAccountLegalConsentService
{
    private static readonly JsonSerializerOptions MetadataJson = new(JsonSerializerDefaults.Web);

    public Task<AccountLegalConsentStatus> GetStatusAsync(Guid userId, string locale, CancellationToken ct)
    {
        ValidateSubject(userId, locale);
        var config = settings.CurrentValue;
        return config.EnforcementEnabled
            ? GetPublishedStatusAsync(userId, locale, Codes(config, locale), ct)
            : Task.FromResult(new AccountLegalConsentStatus(locale, false, []));
    }

    public Task<AccountLegalConsentStatus> GetJourneyStatusAsync(Guid userId, string locale, CancellationToken ct)
    {
        ValidateSubject(userId, locale);
        var config = settings.CurrentValue;
        var codes = config.EnforcementEnabled || config.Documents.Count != 0
            ? Codes(config, locale)
            : new AccountLegalDocumentCodes { Terms = $"paid-demo-terms-{locale}", Privacy = $"paid-demo-privacy-{locale}" };
        return GetPublishedStatusAsync(userId, locale, codes, ct);
    }

    private async Task<AccountLegalConsentStatus> GetPublishedStatusAsync(Guid userId, string locale,
        AccountLegalDocumentCodes codes, CancellationToken ct)
    {
        var current = await ReadPublishedAsync(codes, locale, lockPublication: false, ct);
        var termsId = current[0].Document.VersionId;
        var privacyId = current[1].Document.VersionId;
        var legacyTerms = CompatibleDemoScope(current[0]);
        var legacyPrivacy = CompatibleDemoScope(current[1]);
        var receipts = await db.LegalDocumentAcceptances.AsNoTracking()
            .Where(receipt => receipt.UserId == userId && receipt.Locale == locale && receipt.WithdrawnAt == null
                && ((receipt.Scope == AccountLegalScopes.Terms || receipt.Scope == legacyTerms)
                    && receipt.LegalDocumentVersionId == termsId
                    || (receipt.Scope == AccountLegalScopes.Privacy || receipt.Scope == legacyPrivacy)
                    && receipt.LegalDocumentVersionId == privacyId))
            .OrderBy(receipt => receipt.AcceptedAt).ThenBy(receipt => receipt.Id)
            .Select(receipt => new { receipt.Id, receipt.LegalDocumentVersionId, receipt.AcceptedAt }).ToListAsync(ct);
        return new(locale, true, current.Select(document =>
        {
            var receipt = receipts.FirstOrDefault(item => item.LegalDocumentVersionId == document.Document.VersionId);
            return document with { ReceiptId = receipt?.Id, AcceptedAt = receipt?.AcceptedAt };
        }).ToArray());
    }

    public Task<LegalConsentReceiptDto> AcceptAsync(Guid userId, AcceptAccountLegalDocument request,
        AccountLegalAcceptanceMetadata metadata, CancellationToken ct)
    {
        ValidateSubject(userId, request.Locale);
        if (request.VersionId == Guid.Empty || request.IdempotencyKey == Guid.Empty)
            throw new AccountLegalConsentException("ACCOUNT_LEGAL_REQUEST_INVALID", 400);

        var config = settings.CurrentValue;
        if (!config.EnforcementEnabled) throw Unavailable();

        return LegalAcceptanceTransaction.RunAsync(db, userId, async () =>
        {
            var prior = await db.LegalDocumentAcceptances.AsNoTracking()
                .SingleOrDefaultAsync(receipt => receipt.UserId == userId && receipt.IdempotencyKey == request.IdempotencyKey, ct);
            if (prior is not null)
            {
                if (prior.Scope is not (AccountLegalScopes.Terms or AccountLegalScopes.Privacy)
                    || prior.LegalDocumentVersionId != request.VersionId || prior.Locale != request.Locale)
                    throw new AccountLegalConsentException("ACCOUNT_LEGAL_REQUEST_CONFLICT", 409);
                return Receipt(prior);
            }

            var current = await ReadPublishedAsync(Codes(config, request.Locale), request.Locale, lockPublication: true, ct);
            var accepted = current.SingleOrDefault(item => item.Document.VersionId == request.VersionId)
                ?? throw new AccountLegalConsentException("ACCOUNT_LEGAL_VERSION_CHANGED", 409);

            var legacyScope = CompatibleDemoScope(accepted);
            var existing = await db.LegalDocumentAcceptances.AsNoTracking().Where(receipt =>
                receipt.UserId == userId && (receipt.Scope == accepted.Scope || receipt.Scope == legacyScope)
                && receipt.Locale == request.Locale
                && receipt.LegalDocumentVersionId == request.VersionId && receipt.WithdrawnAt == null)
                .OrderBy(receipt => receipt.AcceptedAt).ThenBy(receipt => receipt.Id).FirstOrDefaultAsync(ct);
            if (existing is not null) return Receipt(existing) with { Scope = accepted.Scope };

            var now = clock.GetUtcNow().UtcDateTime;
            // Igualar la precisión de PostgreSQL para devolver el mismo recibo al reintentar.
            var acceptedAt = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
            var receipt = new LegalDocumentAcceptance
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                LegalDocumentVersionId = request.VersionId,
                Scope = accepted.Scope,
                Locale = request.Locale,
                DisclosureId = Convert.ToHexStringLower(
                    SHA256.HashData(Encoding.UTF8.GetBytes($"{accepted.Document.Code}:{request.VersionId:D}"))),
                DisclosureSnapshot = JsonSerializer.Serialize(metadata, MetadataJson),
                IdempotencyKey = request.IdempotencyKey,
                AcceptedAt = acceptedAt
            };
            db.LegalDocumentAcceptances.Add(receipt);
            await db.SaveChangesAsync(ct);
            return Receipt(receipt);
        }, ct);
    }

    private async Task<AccountLegalDocumentStatus[]> ReadPublishedAsync(AccountLegalDocumentCodes codes,
        string locale, bool lockPublication, CancellationToken ct)
    {
        if (lockPublication && db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT id FROM erp.legal_documents WHERE code IN ({codes.Terms}, {codes.Privacy}) ORDER BY code FOR SHARE", ct);
        }

        var published = await (from document in db.LegalDocuments.AsNoTracking()
            where document.Code == codes.Terms || document.Code == codes.Privacy
            join version in db.LegalDocumentVersions.AsNoTracking()
                on new { VersionId = document.CurrentVersionId, document.Id }
                equals new { VersionId = (Guid?)version.Id, Id = version.DocumentId }
            where version.IsPublished
            select new { document.Code, document.Title, Version = version }).ToListAsync(ct);

        if (published.Count != 2 || published.Any(item => string.IsNullOrWhiteSpace(item.Version.Content)))
            throw Unavailable();

        return published.OrderBy(item => item.Code == codes.Terms ? 0 : 1).Select(item => new AccountLegalDocumentStatus(
            item.Code == codes.Terms ? AccountLegalScopes.Terms : AccountLegalScopes.Privacy,
            new(item.Code, item.Version.Id, item.Version.VersionLabel, item.Title, item.Version.Content,
                locale, item.Version.CreatedAt), null, null)).ToArray();
    }

    private static AccountLegalDocumentCodes Codes(AccountLegalConsentSettings config, string locale)
    {
        if (!config.Documents.TryGetValue(locale, out var codes)
            || string.IsNullOrWhiteSpace(codes.Terms) || string.IsNullOrWhiteSpace(codes.Privacy)
            || string.Equals(codes.Terms, codes.Privacy, StringComparison.Ordinal))
            throw Unavailable();
        return codes;
    }

    private static void ValidateSubject(Guid userId, string locale)
    {
        if (userId == Guid.Empty || locale is not ("es" or "en"))
            throw new AccountLegalConsentException("ACCOUNT_LEGAL_REQUEST_INVALID", 400);
    }

    private static string CompatibleDemoScope(AccountLegalDocumentStatus document)
    {
        var kind = document.Scope == AccountLegalScopes.Terms ? "terms" : "privacy";
        var demoCode = $"paid-demo-{kind}-{document.Document.Locale}";
        return document.Document.Code == demoCode ? demoCode : document.Scope;
    }

    private static LegalConsentReceiptDto Receipt(LegalDocumentAcceptance receipt) => new(receipt.Id, receipt.Scope,
        receipt.LegalDocumentVersionId, receipt.Locale, receipt.DisclosureId, receipt.AcceptedAt, receipt.WithdrawnAt);

    private static AccountLegalConsentException Unavailable() => new("ACCOUNT_LEGAL_DOCUMENT_UNAVAILABLE", 503);
}
