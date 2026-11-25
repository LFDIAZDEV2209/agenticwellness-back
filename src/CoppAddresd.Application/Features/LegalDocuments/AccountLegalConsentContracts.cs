namespace CoppAddresd.Application.Features.LegalDocuments;

public static class AccountLegalScopes
{
    public const string Terms = "account-terms";
    public const string Privacy = "account-privacy";
}

public sealed class AccountLegalConsentSettings
{
    public const string SectionName = "AccountLegalConsents";
    public bool EnforcementEnabled { get; set; }
    public Dictionary<string, AccountLegalDocumentCodes> Documents { get; set; } = [];
}

public sealed class AccountLegalDocumentCodes
{
    public string Terms { get; set; } = "";
    public string Privacy { get; set; } = "";
}

public sealed record AccountLegalDocumentStatus(string Scope, LegalConsentDocumentDto Document,
    Guid? ReceiptId, DateTime? AcceptedAt);

public sealed record AccountLegalConsentStatus(string Locale, bool EnforcementEnabled,
    IReadOnlyList<AccountLegalDocumentStatus> Documents)
{
    public bool AcceptanceRequired => EnforcementEnabled && Documents.Any(document => document.ReceiptId is null);
}

public sealed record AcceptAccountLegalDocument(Guid VersionId, string Locale, Guid IdempotencyKey);

public sealed record AccountLegalAcceptanceMetadata(string? Ip, string? UserAgent)
{
    public static AccountLegalAcceptanceMetadata Capture(string? ip, string? userAgent) =>
        new(ip, userAgent is { Length: > 512 } ? userAgent[..512] : userAgent);
}
