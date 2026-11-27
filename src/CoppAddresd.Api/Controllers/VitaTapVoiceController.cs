using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using CoppAddresd.Application.Common;
using CoppAddresd.Application.Features.Agents.Vita;
using CoppAddresd.Domain.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace CoppAddresd.Api.Controllers;

[ApiController, Authorize, Route("api/v1/chat/voice/turn")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class VitaTapVoiceController(
    VitaPatientAccess access, IHttpClientFactory clients, IOptions<AiServiceSettings> ai,
    IConfiguration configuration) : ControllerBase
{
    private const int MaxBytes = 2 * 1024 * 1024;

    [HttpPost]
    [EnableRateLimiting("vita-tap-voice")]
    [RequestSizeLimit(MaxBytes)]
    public async Task<IActionResult> Turn(CancellationToken ct)
    {
        if (!User.HasClaim("aud", "app")) return Forbid();
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) || userId == Guid.Empty)
            return Unauthorized();
        if (!configuration.GetValue<bool>("Vita:PrivateTapVoiceEnabled")) return NotFound();
        var language = Request.Headers["X-Vita-Language"].ToString();
        if (language is not ("es" or "en")) return BadRequest();
        if (Request.ContentLength is 0) return UnprocessableEntity();
        if (Request.ContentLength > MaxBytes) return StatusCode(413);
        if (!MediaTypeHeaderValue.TryParse(Request.ContentType, out var inbound) ||
            inbound.MediaType is not ("audio/mp4" or "audio/webm" or "audio/wav"))
            return StatusCode(415);
        if (string.IsNullOrEmpty(ai.Value.InternalApiKey)) return StatusCode(503);

        try { await access.RequireChatAndVoiceAsync(userId, ct); }
        catch (AiConsentException ex) { return StatusCode(ex.StatusCode, new { code = ex.Code, scope = ex.Scope }); }
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/vita/voice/turn")
        {
            Content = new StreamContent(Request.Body)
        };
        if (Request.ContentLength is { } length) request.Content.Headers.ContentLength = length;
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(inbound.MediaType);
        request.Headers.Add("X-Vita-Language", language);
        request.Headers.Add("X-Internal-Key", ai.Value.InternalApiKey);
        try
        {
            using var upstream = await clients.CreateClient("VitaTapVoice").SendAsync(
                request, HttpCompletionOption.ResponseContentRead, ct);
            if (upstream.StatusCode is HttpStatusCode.UnprocessableEntity or HttpStatusCode.RequestEntityTooLarge
                or HttpStatusCode.UnsupportedMediaType) return StatusCode((int)upstream.StatusCode);
            if (upstream.StatusCode != HttpStatusCode.OK ||
                upstream.Content.Headers.ContentType?.MediaType != "application/json") return StatusCode(503);
            Response.ContentType = "application/json";
            Response.Headers.CacheControl = "no-store";
            await upstream.Content.CopyToAsync(Response.Body, ct);
            return new EmptyResult();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return StatusCode(503); }
        catch (HttpRequestException) { return StatusCode(503); }
    }
}
