using CoppAddresd.Application.Features.LegalDocuments;

namespace CoppAddresd.Application.Interfaces;

public interface IAccountLegalConsentService
{
    Task<AccountLegalConsentStatus> GetStatusAsync(Guid userId, string locale, CancellationToken ct);
    Task<AccountLegalConsentStatus> GetJourneyStatusAsync(Guid userId, string locale, CancellationToken ct);
    Task<LegalConsentReceiptDto> AcceptAsync(Guid userId, AcceptAccountLegalDocument request,
        AccountLegalAcceptanceMetadata metadata, CancellationToken ct);
}
