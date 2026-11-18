using CoppAddresd.Api.Controllers;
using CoppAddresd.Application.Features.Agents.Vita;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace CoppAddresd.Api.Internal;

/// <summary>Lecturas y capturas de Vita para el runtime de confianza.</summary>
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

    [HttpGet("nutrition")]
    public async Task<ActionResult<VitaNutritionDto>> Nutrition([FromQuery, BindRequired] Guid userId,
        [FromQuery] DateOnly? date, CancellationToken ct)
    {
        if (!configuration.GetValue<bool>("Vita:InternalEnabled")) return NotFound();
        return Ok(await sender.Send(new GetVitaNutritionQuery(userId, date), ct));
    }

    [HttpGet("assessments")]
    public async Task<ActionResult<VitaAssessmentsDto>> Assessments([FromQuery, BindRequired] Guid userId, CancellationToken ct)
    {
        if (!configuration.GetValue<bool>("Vita:InternalEnabled")) return NotFound();
        return Ok(await sender.Send(new GetVitaAssessmentsQuery(userId), ct));
    }

    [HttpGet("documents")]
    public async Task<ActionResult<VitaDocumentsDto>> Documents([FromQuery, BindRequired] Guid userId,
        CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        if (!configuration.GetValue<bool>("Vita:InternalEnabled")) return NotFound();
        return Ok(await sender.Send(new GetVitaDocumentsQuery(userId, page, pageSize), ct));
    }

    [HttpGet("documents/{id:guid}")]
    public async Task<ActionResult<VitaDocumentReadDto>> Document(Guid id, [FromQuery, BindRequired] Guid userId,
        CancellationToken ct)
    {
        if (!configuration.GetValue<bool>("Vita:InternalEnabled")) return NotFound();
        return Ok(await sender.Send(new GetVitaDocumentQuery(userId, id), ct));
    }

    [HttpPost("capture/propose")]
    public async Task<ActionResult<VitaCaptureProposalDto>> ProposeCapture([FromQuery, BindRequired] Guid userId,
        [FromBody] ProposeVitaCaptureRequest input, CancellationToken ct)
    {
        if (!CaptureEnabled) return NotFound();
        return Ok(await sender.Send(new ProposeVitaCaptureCommand(userId, input), ct));
    }

    [HttpPost("capture/confirm")]
    public async Task<ActionResult<VitaCaptureReceiptDto>> ConfirmCapture([FromQuery, BindRequired] Guid userId,
        [FromBody] ConfirmVitaCaptureRequest input, CancellationToken ct)
    {
        if (!CaptureEnabled) return NotFound();
        return Ok(await sender.Send(new ConfirmVitaCaptureCommand(userId, input), ct));
    }

    private bool CaptureEnabled => configuration.GetValue<bool>("Vita:InternalEnabled")
        && configuration.GetValue<bool>("Vita:CaptureEnabled");
}
