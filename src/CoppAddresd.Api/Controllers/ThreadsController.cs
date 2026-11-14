using System.Security.Claims;
using CoppAddresd.Application.Features.Threads;
using CoppAddresd.Application.Common;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Features.Agents.Vita;
using CoppAddresd.Domain.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoppAddresd.Api.Controllers;

/// <summary>
/// Lectura y limpieza (proxy) del historial de un thread del paciente desde el AI Service.
/// El frontend NUNCA llama al AI Service directo: este controlador hace de
/// puente y la X-Internal-Key vive solo en el backend.
/// </summary>
[ApiController]
[Route("api/v1/threads")]
[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class ThreadsController(IAiServiceClient aiService, ILogger<ThreadsController> logger,
    VitaPatientAccess? patientAccess = null)
    : ControllerBase
{
    /// <summary>
    /// Vacía el historial y contexto conversacional de Vita del propietario del JWT (audiencia app).
    /// No requiere consentimientos de lectura y solo confirma lo que AI haya borrado.
    /// </summary>
    [HttpDelete("{threadId}/messages")]
    [ProducesResponseType(typeof(ThreadHistoryClearResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status504GatewayTimeout)]
    public async Task<ActionResult<ThreadHistoryClearResult>> ClearMessages(string threadId, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var ownerId) || ownerId == Guid.Empty)
            return Unauthorized(new { message = "Usuario no identificado." });
        if (!User.HasClaim("aud", "app")) return Forbid();
        ct.ThrowIfCancellationRequested();
        try
        {
            return Ok(await aiService.ClearThreadHistoryAsync(threadId, ownerId.ToString(), ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return HistoryUnavailable(StatusCodes.Status504GatewayTimeout); }
        catch (AiServiceException ex) when (ex.StatusCode == 404) { return NotFound(); }
        catch (AiServiceException ex) when (ex.StatusCode == 403) { return Forbid(); }
        catch (AiServiceException) { return HistoryUnavailable(StatusCodes.Status502BadGateway); }
        catch (HttpRequestException) { return HistoryUnavailable(StatusCodes.Status503ServiceUnavailable); }
    }

    private ObjectResult HistoryUnavailable(int status)
    {
        logger.LogWarning("No se pudo confirmar la limpieza del historial ({StatusCode})", status);
        return Problem(statusCode: status, title: "No se pudo vaciar la conversación.",
            detail: "No se pudo confirmar la limpieza del historial. Inténtalo de nuevo.");
    }

    /// <summary>
    /// Devuelve el historial de un thread (messageCount + lastMessage +
    /// messages en orden) que el paciente verá al abrir el chat tras un
    /// re-login. <c>messages</c> es aditivo: viaja la página solicitada
    /// (tope 100 visibles); un ai-service anterior no lo envía y viaja como
    /// lista vacía.
    /// Paginación desde el más reciente: <c>limit</c> (default 10 en el AI
    /// Service, 1..100) y <c>before</c> (offset desde el final del historial,
    /// devuelto como <c>nextCursor</c>); la respuesta incluye <c>hasMore</c> y
    /// <c>nextCursor</c>. Sin valor, se delegan los defaults del AI Service.
    /// Blindaje anti-IDOR: el dueño del thread proviene estricta y
    /// exclusivamente del JWT (<c>ClaimTypes.NameIdentifier</c>). El query
    /// param <c>userId</c> se conserva solo por compatibilidad y se IGNORA
    /// siempre: nunca define identidad ni alcance.
    /// Si el AI Service falla o el thread no existe se devuelve un estado
    /// vacío (nunca 500) para que el front degrade a chat vacío.
    /// </summary>
    [HttpGet("{threadId}/messages")]
    [ProducesResponseType(typeof(ThreadStateResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ThreadStateResult>> GetMessages(
        string threadId,
        [FromQuery] string? userId,
        [FromQuery] int? limit,
        [FromQuery] int? before,
        CancellationToken ct,
        [FromQuery] bool syncVoice = false
    )
    {
        // Anti-IDOR: el query param userId se ignora deliberadamente
        // (retenido solo por compatibilidad); la identidad sale solo del JWT.
        _ = userId;
        var ownerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(ownerId))
            return Unauthorized(new { message = "Usuario no identificado." });

        try
        {
            if (syncVoice && User.HasClaim("aud", "app") && Guid.TryParse(ownerId, out var syncUser)
                && patientAccess is not null)
            {
                try
                {
                    await patientAccess.RequireChatAndVoiceAsync(syncUser, ct);
                    // Propiedad en AI: ownerId del JWT + namespace de hilo y vínculos durables propios.
                    return Ok(await aiService.GetThreadStateWithVoiceSyncAsync(threadId, ownerId, limit, before, ct));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (AiConsentException) { /* Sin autorización de voz se conserva la lectura normal de chat. */ }
                catch (ForbiddenException) { /* No conceder sync por el bool ni por conocer un ID. */ }
                catch (Exception ex)
                {
                    logger.LogWarning("Sync de voz no disponible ({ErrorType}); se conserva lectura de chat", ex.GetType().Name);
                    var existing = await aiService.GetThreadStateAsync(threadId, ownerId, limit, before, ct);
                    return Ok(existing with { VoiceSyncStatus = "unavailable" });
                }
            }
            var state = await aiService.GetThreadStateAsync(threadId, ownerId, limit, before, ct);
            return Ok(state);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Degradación: el AI Service puede no estar disponible o el thread
            // puede no existir todavía (404). El cliente siempre recibe 200.
            logger.LogWarning(
                ex,
                "No se pudo leer el thread {ThreadId} en el AI Service",
                threadId
            );
            return Ok(new ThreadStateResult(threadId, 0, null, []));
        }
    }
}
