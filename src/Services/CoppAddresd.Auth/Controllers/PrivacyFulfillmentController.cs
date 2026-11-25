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
[RequirePermission(PermissionCodes.PrivacyPublishExport)]
[EnableRateLimiting("auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/auth/privacy/fulfillment")]
public sealed class PrivacyFulfillmentController(PrivacyFulfillmentService service, PrivacyRequestService requests)
    : ControllerBase
{
    [HttpPut("{requestId:guid}")]
    [RequestSizeLimit(2_097_152)]
    public async Task<IActionResult> Publish(Guid requestId, ReviewedExport bundle, CancellationToken ct)
    {
        if (!service.Enabled)
            return NotFound();
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var operatorId))
            return Forbid();
        if (!await requests.HasActiveAccessAsync(bundle.UserId, ct))
            return NotFound();
        return (await service.PublishAsync(requestId, operatorId, bundle, ct)) switch
        {
            PublishPrivacyResult.Published => NoContent(),
            PublishPrivacyResult.MissingRequest => NotFound(),
            PublishPrivacyResult.AlreadyAvailable => Conflict(new { code = "PRIVACY_EXPORT_ALREADY_AVAILABLE" }),
            _ => BadRequest(new { code = "INVALID_PRIVACY_COVERAGE" })
        };
    }
}
