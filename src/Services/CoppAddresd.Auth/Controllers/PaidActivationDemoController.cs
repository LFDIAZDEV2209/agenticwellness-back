using System.ComponentModel.DataAnnotations;
using System.Net.Mail;
using CoppAddresd.Auth.Configuration;
using CoppAddresd.Auth.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CoppAddresd.Auth.Controllers;

[ApiController]
[AllowAnonymous]
[EnableRateLimiting("auth")]
[RequestSizeLimit(8_192)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/auth/paid-activation/demo")]
public sealed class PaidActivationDemoController(
    PaidActivationDemoSettings settings,
    PaidActivationService activations,
    PaidActivationLinks links,
    PaidActivationSignupService signup) : ControllerBase
{
    private const string SetupCookie = "copp_paid_setup_demo";

    [HttpPost("confirm")]
    public async Task<IActionResult> Confirm(ConfirmPayment request, CancellationToken ct)
    {
        if (settings.WebOrigin is null)
            return NotFound();

        var email = request.Email.Trim();
        if (request.PaymentId == Guid.Empty
            || !MailAddress.TryCreate(email, out var parsed)
            || parsed.Address != email
            || request.Plan is not ("monthly" or "annual")
            || request.Language is not ("es" or "en"))
        {
            return BadRequest(new { code = "invalid_simulated_payment" });
        }

        return Accepted(await activations.ConfirmAsync(
            request.PaymentId, email, request.Plan, request.Language, ct));
    }

    [HttpGet("receipt/{paymentId:guid}")]
    public async Task<IActionResult> Receipt(Guid paymentId, CancellationToken ct)
    {
        if (settings.WebOrigin is null)
            return NotFound();
        var receipt = await activations.ReceiptAsync(paymentId, ct);
        return receipt is null ? NotFound() : Ok(receipt);
    }

    [HttpPost("resend")]
    public async Task<IActionResult> Resend(ActivationReceipt request, CancellationToken ct)
    {
        if (settings.WebOrigin is null)
            return NotFound();
        if (request.PaymentId == Guid.Empty)
            return BadRequest();

        await activations.ResendAsync(request.PaymentId, ct);
        return Accepted();
    }

    [HttpPost("prepare")]
    public async Task<IActionResult> Prepare(PrepareWelcomeLink request, CancellationToken ct)
    {
        if (settings.WebOrigin is null)
            return NotFound();
        if (!IsWebRequest())
            return Forbid();

        var proof = await activations.PrepareAsync(request.PaymentId, request.Token, ct);
        if (proof is null)
            return BadRequest(new { code = "invalid_activation_link" });
        Response.Cookies.Append(SetupCookie, links.IssueSetup(proof), new CookieOptions
        {
            HttpOnly = true,
            Secure = settings.WebOrigin.Scheme == "https",
            SameSite = SameSiteMode.Strict,
            Path = "/api/auth/paid-activation/demo",
            Expires = proof.ExpiresAt
        });
        return NoContent();
    }

    [HttpGet("setup")]
    public async Task<IActionResult> Setup(CancellationToken ct)
    {
        if (settings.WebOrigin is null || ReadSetup() is not { } proof)
            return NotFound();
        var view = await signup.GetAsync(proof, ct);
        if (view?.Status == "setup_required" && view.Documents?.Count != 2)
            return StatusCode(503, new { code = "consent_unavailable" });
        return view is null ? NotFound() : Ok(view);
    }

    [HttpPost("redeem")]
    public async Task<IActionResult> Redeem(RedeemWelcomeLink request, CancellationToken ct)
    {
        if (settings.WebOrigin is null)
            return NotFound();

        var proof = request.Token is null ? ReadSetup() : null;
        if (proof is not null && !IsWebRequest())
            return Forbid();
        var result = await activations.RedeemAsync(request.PaymentId, request.Token, proof, User, ct);
        return result.State switch
        {
            PaidActivationState.SignInRequired => Conflict(new { code = "sign_in_required", result.Email }),
            PaidActivationState.SetupRequired => Ok(new { status = "setup_required" }),
            PaidActivationState.Linked => Ok(new { status = "linked", appUrl = settings.AppUrl }),
            _ => BadRequest(new { code = "invalid_activation_link" }),
        };
    }

    [HttpPost("complete")]
    public async Task<IActionResult> Complete(CompleteSetup request, CancellationToken ct)
    {
        if (settings.WebOrigin is null || ReadSetup() is not { } proof)
            return NotFound();
        if (!IsWebRequest())
            return Forbid();

        var userAgent = Request.Headers.UserAgent.ToString();
        var result = await signup.CompleteAsync(proof, new(
                request.FirstName, request.LastName, request.EmergencyName, request.EmergencyPhone,
                request.Password, request.VersionIds),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            userAgent[..Math.Min(512, userAgent.Length)],
            ct);
        return result.ErrorCode switch
        {
            null => Ok(result.View),
            "document_version_changed" or "sign_in_required" => Conflict(new { code = result.ErrorCode }),
            "consent_unavailable" or "journey_unavailable" => StatusCode(503, new { code = result.ErrorCode }),
            _ => BadRequest(new { code = result.ErrorCode })
        };
    }

    private PaidSetupProof? ReadSetup() => links.ReadSetup(Request.Cookies[SetupCookie]);

    private bool IsWebRequest() => !Request.Headers.TryGetValue("Origin", out var origin)
        || string.Equals(origin.ToString(), settings.WebOrigin!.GetLeftPart(UriPartial.Authority),
            StringComparison.OrdinalIgnoreCase);

    public sealed record ConfirmPayment(
        Guid PaymentId,
        [Required, MaxLength(254)] string Email,
        [Required, MaxLength(7)] string Plan,
        [Required, MaxLength(2)] string Language);

    public sealed record ActivationReceipt(Guid PaymentId);

    public sealed record PrepareWelcomeLink(Guid PaymentId, [Required, MaxLength(2048)] string Token);
    public sealed record RedeemWelcomeLink(Guid PaymentId, [MaxLength(2048)] string? Token);
    public sealed record CompleteSetup(
        [Required, MaxLength(100)] string FirstName,
        [Required, MaxLength(100)] string LastName,
        [Required, MaxLength(100)] string EmergencyName,
        [Required, MaxLength(24)] string EmergencyPhone,
        [Required, MaxLength(128)] string Password,
        [Required, MaxLength(2)] Guid[] VersionIds);
}
