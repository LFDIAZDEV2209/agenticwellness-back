using System.Security.Claims;
using CoppAddresd.Application.Common;
using CoppAddresd.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoppAddresd.Api.Controllers;

/// <summary>Cierre de sesión autorizado por JWT; revocar sigue permitido tras retirar consentimiento.</summary>
[ApiController]
[Authorize]
[Route("api/v1/chat/voice/session")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class VitaVoiceSessionsController(IAiServiceClient ai) : ControllerBase
{
    [HttpDelete("{sessionId}")]
    public async Task<IActionResult> Revoke(string sessionId, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Unauthorized();
        if (!User.HasClaim("aud", "app")) return Forbid();
        if (sessionId.Length is < 1 or > 200 || sessionId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            return BadRequest();
        try { await ai.RevokeVoiceSessionAsync(sessionId, userId.ToString(), ct); return NoContent(); }
        catch (AiServiceException ex) when (ex.StatusCode == 404) { return NotFound(); }
        catch (AiServiceException) { return StatusCode(502, new { code = "VITA_VOICE_UNAVAILABLE" }); }
        catch (HttpRequestException) { return StatusCode(503, new { code = "VITA_VOICE_UNAVAILABLE" }); }
    }
}
