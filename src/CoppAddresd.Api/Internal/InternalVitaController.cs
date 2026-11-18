using CoppAddresd.Api.Controllers;
using CoppAddresd.Application.Features.Agents.Vita;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace CoppAddresd.Api.Internal;

/// <summary>Lecturas mínimas para el runtime de confianza; ninguna escritura disponible por clave interna.</summary>
[ApiController]
[Route("api/v1/internal/vita")]
[AllowAnonymous]
[RequireAiInternalKey]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class InternalVitaController(ISender sender, IConfiguration configuration) : ControllerBase
{
    /// <summary>El userId lo inyecta el runtime autenticado; no se registra como argumento de herramienta.</summary>
    [HttpGet("context")]
    public async Task<ActionResult<VitaContextDto>> Context([FromQuery] Guid userId, CancellationToken ct,
        [FromQuery] string scope = "day", [FromQuery] string? resourceVersion = null)
    {
        if (!configuration.GetValue<bool>("Vita:InternalEnabled")) return NotFound();
        var result = await sender.Send(new GetVitaContextQuery(userId, scope, resourceVersion), ct);
        return StatusCode(VitaHttpStatus.For(result.Status), result);
    }

    [HttpGet("agents")]
    public async Task<ActionResult<IReadOnlyList<VitaAgentDto>>> Agents([FromQuery] Guid userId, CancellationToken ct)
    {
        if (!configuration.GetValue<bool>("Vita:InternalEnabled")) return NotFound();
        return Ok(await sender.Send(new GetVitaAgentsQuery(userId), ct));
    }

    [HttpGet("profile")]
    public async Task<ActionResult<VitaProfileDto>> Profile([FromQuery, BindRequired] Guid userId, CancellationToken ct)
    {
        if (!configuration.GetValue<bool>("Vita:InternalEnabled")) return NotFound();
        return Ok(await sender.Send(new GetVitaProfileQuery(userId), ct));
    }

    [HttpGet("vitals")]
    public async Task<ActionResult<VitaVitalsDto>> Vitals([FromQuery, BindRequired] Guid userId,
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
    {
        if (!configuration.GetValue<bool>("Vita:InternalEnabled")) return NotFound();
        return Ok(await sender.Send(new GetVitaVitalsQuery(userId, from, to), ct));
    }

    [HttpGet("medications")]
    public async Task<ActionResult<VitaMedicationsDto>> Medications([FromQuery, BindRequired] Guid userId,
        CancellationToken ct)
    {
        if (!configuration.GetValue<bool>("Vita:InternalEnabled")) return NotFound();
        return Ok(await sender.Send(new GetVitaMedicationsQuery(userId), ct));
    }
}
