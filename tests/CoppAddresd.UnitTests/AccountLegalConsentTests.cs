using CoppAddresd.Application.Features.LegalDocuments;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Persistence;
using CoppAddresd.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CoppAddresd.UnitTests;

public sealed class AccountLegalConsentTests : IDisposable
{
    private readonly AppDbContext db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"account-legal-{Guid.NewGuid():N}").Options);
    private readonly AccountLegalConsentSettings settings = new() { EnforcementEnabled = true };
    private readonly IOptionsMonitor<AccountLegalConsentSettings> monitor = Substitute.For<IOptionsMonitor<AccountLegalConsentSettings>>();
    private readonly Guid user = Guid.NewGuid();
    private static readonly AccountLegalAcceptanceMetadata Metadata = new("192.0.2.10", "Test browser");

    public AccountLegalConsentTests() => monitor.CurrentValue.Returns(settings);
    public void Dispose() => db.Dispose();

    private AccountLegalConsentService Service() => new(db, monitor, TimeProvider.System);

    private async Task<(LegalDocumentVersion Terms, LegalDocumentVersion Privacy)> SeedAsync(string locale, string prefix = "synthetic")
    {
        settings.Documents[locale] = new() { Terms = $"{prefix}-terms-{locale}", Privacy = $"{prefix}-privacy-{locale}" };
        LegalDocumentVersion Add(string code)
        {
            var doc = new LegalDocument { Id = Guid.NewGuid(), Code = code, Title = code };
            var version = new LegalDocumentVersion { Id = Guid.NewGuid(), DocumentId = doc.Id,
                Major = 1, IsPublished = true, Content = "Synthetic text." };
            doc.CurrentVersionId = version.Id;
            db.LegalDocuments.Add(doc);
            db.LegalDocumentVersions.Add(version);
            return version;
        }
        var terms = Add(settings.Documents[locale].Terms);
        var privacy = Add(settings.Documents[locale].Privacy);
        await db.SaveChangesAsync();
        return (terms, privacy);
    }

    [Fact]
    public async Task Existing_demo_receipt_counts_for_its_exact_document_without_duplication()
    {
        var (terms, _) = await SeedAsync("es", "paid-demo");
        var old = new LegalDocumentAcceptance
        {
            Id = Guid.NewGuid(),
            Scope = "paid-demo-terms-es",
            UserId = user,
            LegalDocumentVersionId = terms.Id,
            Locale = "es",
            DisclosureId = new string('1', 64),
            DisclosureSnapshot = "{}",
            AcceptedAt = DateTime.UtcNow,
            IdempotencyKey = Guid.NewGuid()
        };
        db.LegalDocumentAcceptances.Add(old);
        await db.SaveChangesAsync();
        var service = Service();
        var status = await service.GetStatusAsync(user, "es", default);
        Assert.Equal(old.Id, status.Documents[0].ReceiptId);
        Assert.Null(status.Documents[1].ReceiptId);
        var retry = await service.AcceptAsync(user, new(terms.Id, "es", Guid.NewGuid()), Metadata, default);
        Assert.Equal(old.Id, retry.Id);
        Assert.Equal(AccountLegalScopes.Terms, retry.Scope);
        Assert.Equal("paid-demo-terms-es", (await db.LegalDocumentAcceptances.SingleAsync()).Scope);
    }

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    public async Task Resolves_exact_published_pair_and_records_idempotent_receipts(string locale)
    {
        var (terms, privacy) = await SeedAsync(locale);
        var service = Service();
        var before = await service.GetStatusAsync(user, locale, default);
        Assert.True(before.AcceptanceRequired);
        Assert.Equal(new[] { terms.Id, privacy.Id }, before.Documents.Select(item => item.Document.VersionId));
        var request = new AcceptAccountLegalDocument(terms.Id, locale, Guid.NewGuid());
        var receipt = await service.AcceptAsync(user, request, Metadata, default);
        Assert.Equal(receipt, await service.AcceptAsync(user, request with { IdempotencyKey = Guid.NewGuid() }, Metadata, default));
        Assert.Equal(receipt, await service.AcceptAsync(user, request, Metadata, default));
        Assert.Equal("account-terms", receipt.Scope);
        Assert.Equal(locale, receipt.Locale);
        var persisted = await db.LegalDocumentAcceptances.SingleAsync();
        Assert.Equal(user, persisted.UserId);
        Assert.Contains("\"ip\":\"192.0.2.10\"", persisted.DisclosureSnapshot);
        Assert.Contains("\"userAgent\":\"Test browser\"", persisted.DisclosureSnapshot);
        var conflict = await Assert.ThrowsAsync<AccountLegalConsentException>(() =>
            service.AcceptAsync(user, request with { VersionId = privacy.Id }, Metadata, default));
        Assert.Equal(409, conflict.StatusCode);

        await service.AcceptAsync(user, new(privacy.Id, locale, Guid.NewGuid()), Metadata, default);
        Assert.False((await service.GetStatusAsync(user, locale, default)).AcceptanceRequired);
        Assert.True((await service.GetStatusAsync(Guid.NewGuid(), locale, default)).AcceptanceRequired);

        var contextual = new LegalConsentService(db, Substitute.For<IOptionsMonitor<LegalConsentSettings>>(), TimeProvider.System);
        await Assert.ThrowsAsync<NotFoundException>(() => contextual.WithdrawAsync(user, receipt.Id, default));
        Assert.Null((await db.LegalDocumentAcceptances.SingleAsync(item => item.Id == receipt.Id)).WithdrawnAt);
    }

    [Fact]
    public async Task Republishing_one_document_requires_only_that_document_and_preserves_history()
    {
        var (terms, privacy) = await SeedAsync("es");
        var service = Service();
        await service.AcceptAsync(user, new(terms.Id, "es", Guid.NewGuid()), Metadata, default);
        await service.AcceptAsync(user, new(privacy.Id, "es", Guid.NewGuid()), Metadata, default);
        var replacement = new LegalDocumentVersion { Id = Guid.NewGuid(), DocumentId = terms.DocumentId,
            Major = 2, IsPublished = true, Content = "Synthetic replacement." };
        db.LegalDocumentVersions.Add(replacement);
        var document = await db.LegalDocuments.FindAsync(terms.DocumentId);
        document!.CurrentVersionId = replacement.Id;
        await db.SaveChangesAsync();
        var status = await service.GetStatusAsync(user, "es", default);
        var pending = Assert.Single(status.Documents, item => item.ReceiptId is null);
        Assert.Equal(replacement.Id, pending.Document.VersionId);
        Assert.Equal(2, await db.LegalDocumentAcceptances.CountAsync());
        var stale = await Assert.ThrowsAsync<AccountLegalConsentException>(() =>
            service.AcceptAsync(user, new(terms.Id, "es", Guid.NewGuid()), Metadata, default));
        Assert.Equal("ACCOUNT_LEGAL_VERSION_CHANGED", stale.Code);
    }

    [Fact]
    public async Task Incomplete_publication_is_unavailable_and_disabled_gate_is_inert()
    {
        var (terms, _) = await SeedAsync("en");
        terms.IsPublished = false;
        await db.SaveChangesAsync();
        var service = Service();
        var missing = await Assert.ThrowsAsync<AccountLegalConsentException>(() => service.GetStatusAsync(user, "en", default));
        Assert.Equal(503, missing.StatusCode);
        Assert.Empty(await db.LegalDocumentAcceptances.ToListAsync());

        settings.EnforcementEnabled = false;
        var disabled = await service.GetStatusAsync(user, "en", default);
        Assert.False(disabled.EnforcementEnabled);
        Assert.False(disabled.AcceptanceRequired);
        Assert.Empty(disabled.Documents);
    }
}
