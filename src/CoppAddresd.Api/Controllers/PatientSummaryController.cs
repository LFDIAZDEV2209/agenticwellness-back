using CoppAddresd.Api.Authorization;
using CoppAddresd.Api.Constants;
using CoppAddresd.Application.Features.ProgramProgress.PatientSummary;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoppAddresd.Api.Controllers;

[ApiController]
[Authorize(Policy = NutritionPolicies.Staff)]
[Route("api/v1/program/erp/patients/{patientId:guid}/ai-summary")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PatientSummaryController(IMediator mediator) : ControllerBase
{
    public sealed record GenerateRequest(string Language = "es");
    [HttpGet]
    [PatientScope("patientId", "Program.Profile360View")]
    [PatientScope("patientId", "Program.AiView")]
    [EndpointSummary("Leer el último resumen IA de la persona sin generar otro")]
    public async Task<ActionResult<PatientSummaryResult>> Get(Guid patientId, [FromQuery] string language = "es", CancellationToken ct = default)
        => Ok(await mediator.Send(new GetPatientSummary(patientId, language), ct));

    [HttpPost]
    [PatientScope("patientId", "Program.Profile360View")]
    [PatientScope("patientId", "Program.AiView")]
    [PatientScope("patientId", "Program.AiManage")]
    [EndpointSummary("Generar a pedido un borrador de resumen IA de los últimos siete días locales")]
    public async Task<ActionResult<PatientSummaryResult>> Generate(Guid patientId, [FromBody] GenerateRequest body, CancellationToken ct)
        => Ok(await mediator.Send(new GeneratePatientSummary(patientId, body.Language), ct));
}
