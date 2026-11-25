using CoppAddresd.Api.Configuration;
using CoppAddresd.Api.Context;
using CoppAddresd.Application.Features.LegalDocuments;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoppAddresd.Api.Controllers;

[ApiController]
[Route("api/v1/me/account-legal-consents")]
[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AccountLegalConsentsController(IAccountLegalConsentService consents, ICurrentContext current,
    WellnessJourneyDemoOptions journeyOptions, WellnessJourneyStore journey) : ControllerBase
{
    private Guid? AccountId => User.HasClaim("aud", "app") ? current.UserId : null;

    [HttpGet]
    public async Task<ActionResult<AccountLegalConsentStatus>> Status(
        [FromQuery] string locale = "es", CancellationToken ct = default)
    {
        if (AccountId is not { } userId) return Forbid();
        var requiredLocale = await LegalLocaleAsync(userId, locale, ct);
        return Ok(await consents.GetStatusAsync(userId, requiredLocale, ct));
    }

    [HttpPost("acceptances")]
    public async Task<ActionResult<LegalConsentReceiptDto>> Accept([FromBody] AcceptAccountLegalDocument request,
        CancellationToken ct)
    {
        if (AccountId is not { } userId) return Forbid();
        var requiredLocale = await LegalLocaleAsync(userId, request.Locale, ct);
        if (!string.Equals(requiredLocale, request.Locale, StringComparison.Ordinal))
            return Conflict(new { code = "ACCOUNT_LEGAL_VERSION_CHANGED" });
        var metadata = AccountLegalAcceptanceMetadata.Capture(HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.ToString());
        return Ok(await consents.AcceptAsync(userId, request, metadata, ct));
    }

    private async Task<string> LegalLocaleAsync(Guid userId, string requested, CancellationToken ct) =>
        journeyOptions.Enabled ? await journey.GetLegalLocaleAsync(userId, ct) ?? requested : requested;
}
