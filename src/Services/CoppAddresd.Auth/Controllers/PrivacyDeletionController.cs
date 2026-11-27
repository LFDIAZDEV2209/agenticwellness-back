using System.Security.Claims;
using CoppAddresd.Auth.Authorization;
using CoppAddresd.Auth.Constants;
using CoppAddresd.Auth.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CoppAddresd.Auth.Controllers;

[ApiController]
[Authorize]
[RequireErpAudience]
[RequirePermission(PermissionCodes.PrivacyReviewDeletion)]
[EnableRateLimiting("auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/auth/privacy/deletions/{requestId:guid}")]
public sealed class PrivacyDeletionController(PrivacyDeletionService service, PrivacyDeletionCoverageService coverage) : ControllerBase
{
    [HttpPut("verification")]
    [RequestSizeLimit(2048)]
    public async Task<IActionResult> Verify(Guid requestId, VerifyPrivacyDeletion input, CancellationToken ct)
    {
        if (!service.Enabled)
            return NotFound();
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId))
            return Forbid();

        return Map(await service.VerifyAsync(requestId, actorId, input, ct));
    }

    [HttpPut("preferences")]
    [RequestSizeLimit(2048)]
    public async Task<IActionResult> ErasePreferences(Guid requestId, ErasePrivacyPreferences input, CancellationToken ct)
    {
        if (!service.Enabled)
            return NotFound();
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId))
            return Forbid();

        return Map(await service.ErasePreferencesAsync(requestId, actorId, input, ct));
    }

    [HttpPut("device-tokens")]
    [RequestSizeLimit(2048)]
    public async Task<IActionResult> EraseDeviceTokens(Guid requestId, ErasePrivacyPreferences input, CancellationToken ct)
    {
        if (!service.Enabled)
            return NotFound();
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId))
            return Forbid();

        return Map(await coverage.EraseDeviceTokensAsync(requestId, actorId, input, ct));
    }

    [HttpPut("sources/{source}")]
    [RequestSizeLimit(2048)]
    public async Task<IActionResult> RecordSource(Guid requestId, string source, RecordPrivacyDisposition input,
        CancellationToken ct)
    {
        if (!service.Enabled)
            return NotFound();
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId))
            return Forbid();

        return Map(await coverage.RecordAsync(requestId, actorId, source, input, ct));
    }

    [HttpPut("completion")]
    [RequestSizeLimit(2048)]
    public async Task<IActionResult> Complete(Guid requestId, CompletePrivacyDeletion input, CancellationToken ct)
    {
        if (!service.Enabled)
            return NotFound();
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId))
            return Forbid();

        return Map(await coverage.CompleteAsync(requestId, actorId, input, ct));
    }

    private IActionResult Map<T>(DeletionOperation<T> result) where T : class => result.Status switch
    {
        DeletionOperationStatus.Ok => Ok(result.Value),
        DeletionOperationStatus.Invalid => BadRequest(new { code = "INVALID_PRIVACY_DELETION_REVIEW" }),
        DeletionOperationStatus.NotFound => NotFound(),
        DeletionOperationStatus.Conflict when result.Value is PrivacyDeletionCompletion detail => Conflict(detail),
        _ => Conflict(new { code = "PRIVACY_DELETION_REVIEW_CONFLICT" })
    };
}
