using System.Security.Claims;
using CoppAddresd.Application.Features.Agents.Vita;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoppAddresd.Api.Controllers;

/// <summary>Fachada de Vita para el paciente autenticado; no acepta identidades del cliente.</summary>
[ApiController]
[Route("api/v1/vita/me")]
[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class VitaController(ISender sender, IConfiguration configuration) : ControllerBase
{
    private Guid? UserId => User.HasClaim("aud", "app")
        && Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    /// <summary>Lee una capacidad actual, sin completar tareas ni materializar contenido.</summary>
    [HttpGet("context")]
    [ProducesResponseType(typeof(VitaContextDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<VitaContextDto>> Context(CancellationToken ct,
        [FromQuery] string scope = "day", [FromQuery] string? resourceVersion = null)
    {
        if (UserId is not { } userId) return Forbid();
        var result = await sender.Send(new GetVitaContextQuery(userId, scope, resourceVersion), ct);
        return StatusCode(VitaHttpStatus.For(result.Status), result);
    }

    /// <summary>Especialistas asignados, activos y con versión activa; sin prompts ni herramientas.</summary>
    [HttpGet("agents")]
    public async Task<ActionResult<IReadOnlyList<VitaAgentDto>>> Agents(CancellationToken ct)
    {
        if (UserId is not { } userId) return Forbid();
        return Ok(await sender.Send(new GetVitaAgentsQuery(userId), ct));
    }

    /// <summary>Propone completar podcast o ejercicio; no modifica datos.</summary>
    [HttpPost("task-completion-proposals")]
    public async Task<ActionResult<VitaTaskProposalDto>> Prepare([FromBody] PrepareVitaTaskRequest request, CancellationToken ct)
    {
        if (UserId is not { } userId) return Forbid();
        if (!configuration.GetValue<bool>("Vita:TaskCompletionEnabled")) return NotFound();
        try { return Ok(await sender.Send(new PrepareVitaTaskCommand(userId, request), ct)); }
        catch (VitaUnavailableException) { return StatusCode(503, new { code = "VITA_UNAVAILABLE" }); }
    }

    /// <summary>Confirma la propuesta vigente; el reintento conserva la clave de idempotencia del servidor.</summary>
    [HttpPost("task-completion-confirmations")]
    public async Task<ActionResult<CoppAddresd.Application.DTOs.ProgramProgress.CompleteTaskResponseDto>> Confirm(
        [FromBody] ConfirmVitaTaskRequest request, CancellationToken ct)
    {
        if (UserId is not { } userId) return Forbid();
        if (!configuration.GetValue<bool>("Vita:TaskCompletionEnabled")) return NotFound();
        try { return Ok(await sender.Send(new ConfirmVitaTaskCommand(userId, request), ct)); }
        catch (VitaUnavailableException) { return StatusCode(503, new { code = "VITA_UNAVAILABLE" }); }
    }
}

public static class VitaHttpStatus
{
    public static int For(string status) => status switch
    {
        "forbidden" => StatusCodes.Status403Forbidden,
        "unavailable" => StatusCodes.Status503ServiceUnavailable,
        "stale" => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status200OK
    };
}
