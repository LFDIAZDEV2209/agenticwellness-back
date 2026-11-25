using System.Security.Claims;
using CoppAddresd.Auth.Constants;
using CoppAddresd.Auth.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CoppAddresd.Auth.Controllers;

[ApiController]
[Authorize]
[EnableRateLimiting("auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/auth/privacy/requests")]
public sealed class PrivacyRequestsController(PrivacyRequestService requests, PrivacyDownloadService downloads,
    PrivacyFulfillmentService fulfillment, IConfiguration configuration)
    : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PendingPrivacyRequests>> List(CancellationToken ct)
    {
        var (userId, denied) = await AuthorizeRequestAsync(ct);
        if (denied is not null)
            return denied;
        var result = await requests.ListPendingAsync(userId, ct);
        return Ok(result with
        {
            DownloadAvailable = IsDownloadEnabled,
            FullExport = fulfillment.Enabled ? await fulfillment.GetReadyAsync(userId, ct) : null
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PrivacyReceipt>> Get(Guid id, CancellationToken ct)
    {
        var (userId, denied) = await AuthorizeRequestAsync(ct);
        if (denied is not null)
            return denied;
        var receipt = await requests.FindAsync(userId, id, ct);
        return receipt is null ? NotFound() : Ok(receipt);
    }

    [HttpGet("{id:guid}/download")]
    public async Task<ActionResult<PreparedPrivacyDownload>> Download(Guid id, CancellationToken ct)
    {
        var (userId, denied) = await AuthorizeRequestAsync(ct);
        if (denied is not null)
            return denied;
        if (!IsDownloadEnabled)
            return NotFound();
        var receipt = await requests.FindAsync(userId, id, ct);
        if (receipt is not { Kind: PrivacyRequestService.Export, Format: "json" or "csv" })
            return NotFound();

        var copy = await downloads.BuildAsync(userId, receipt.Format, ct);
        if (copy is null)
            return Conflict(new { code = "PRIVACY_COPY_REQUIRES_REVIEW" });
        if (!await requests.HasActiveAccessAsync(userId, ct))
            return Forbid();
        return await requests.RecordPreparationAsync(userId, id, ct) == 1 ? Ok(copy) : NotFound();
    }

    [HttpGet("{id:guid}/full-download")]
    public async Task<ActionResult<PreparedPrivacyDownload>> FullDownload(Guid id, CancellationToken ct)
    {
        var (userId, denied) = await AuthorizeRequestAsync(ct);
        if (denied is not null)
            return denied;
        if (!fulfillment.Enabled)
            return NotFound();
        var file = await fulfillment.DeliverAsync(userId, id, ct);
        return file is null ? NotFound() : Ok(file);
    }

    private bool IsDownloadEnabled => configuration.GetValue<bool>("PrivacyRequests:DownloadEnabled");

    [HttpPost]
    [RequestSizeLimit(2048)]
    public async Task<ActionResult<PrivacyReceipt>> Submit(SubmitPrivacyRequest request, CancellationToken ct)
    {
        var (userId, denied) = await AuthorizeRequestAsync(ct);
        if (denied is not null)
            return denied;
        var result = await requests.SubmitAsync(userId, request, ct);
        if (result is not { } submitted)
            return BadRequest(new { code = "INVALID_PRIVACY_REQUEST" });
        return submitted.Created
            ? CreatedAtAction(nameof(Get), new { id = submitted.Receipt.Id }, submitted.Receipt)
            : Ok(submitted.Receipt);
    }

    private async Task<(Guid UserId, ActionResult? Denied)> AuthorizeRequestAsync(CancellationToken ct)
    {
        if (!configuration.GetValue<bool>("PrivacyRequests:Enabled"))
            return (Guid.Empty, NotFound());
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ||
            User.FindFirstValue("aud") != ApplicationCodes.App ||
            !await requests.HasActiveAccessAsync(userId, ct))
            return (Guid.Empty, Forbid());
        return (userId, null);
    }
}
