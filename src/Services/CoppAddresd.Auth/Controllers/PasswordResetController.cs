using System.ComponentModel.DataAnnotations;
using System.Net.Mail;
using CoppAddresd.Auth.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CoppAddresd.Auth.Controllers;

[ApiController]
[AllowAnonymous]
[EnableRateLimiting("auth")]
[RequestSizeLimit(16_384)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/auth/account/password-reset")]
public sealed class PasswordResetController(
    PasswordResetEmailQueue queue,
    EmailPasswordResetService recovery
) : ControllerBase
{
    [HttpPost("request")]
    public IActionResult RequestLink([FromBody] RequestPasswordResetLink request)
    {
        var email = request.Email.Trim();
        if (!MailAddress.TryCreate(email, out var address) || address.Address != email)
            return BadRequest(new { code = "invalid_email" });

        var language = request.Language?.Split('-', '_')[0].Equals("en", StringComparison.OrdinalIgnoreCase) == true
            ? "en"
            : "es";
        return queue.TryEnqueue(email, language)
            ? StatusCode(StatusCodes.Status202Accepted)
            : Unavailable();
    }

    [HttpPost("confirm")]
    public async Task<IActionResult> Confirm(
        [FromBody] ConfirmPasswordResetLink request,
        CancellationToken ct
    )
    {
        if (queue.Destination is null)
            return Unavailable();

        if (!Guid.TryParse(request.UserId, out var userId) || userId == Guid.Empty)
            return BadRequest(new { code = "invalid_reset_link" });

        return await recovery.ConfirmAsync(userId, request.Token, request.NewPassword, ct) switch
        {
            PasswordResetResult.Completed => NoContent(),
            PasswordResetResult.PasswordPolicy => BadRequest(new { code = "password_policy" }),
            _ => BadRequest(new { code = "invalid_reset_link" }),
        };
    }

    private IActionResult Unavailable() =>
        StatusCode(StatusCodes.Status503ServiceUnavailable, new { code = "password_reset_unavailable" });
}

public sealed record RequestPasswordResetLink
{
    [Required, EmailAddress, StringLength(256)]
    public string Email { get; init; } = "";

    [StringLength(35)]
    public string? Language { get; init; }
}

public sealed record ConfirmPasswordResetLink(string? UserId, string? Token, string? NewPassword);
