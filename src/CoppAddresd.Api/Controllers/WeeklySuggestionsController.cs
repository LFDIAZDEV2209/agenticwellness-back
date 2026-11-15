using CoppAddresd.Api.Authorization;
using CoppAddresd.Api.Constants;
using CoppAddresd.Application.Features.ProgramProgress.WeeklySuggestions;
using CoppAddresd.Application.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoppAddresd.Api.Controllers;

[ApiController]
[Authorize(Policy = NutritionPolicies.Staff)]
[Route("api/v1/program/erp/enrollments/{enrollmentId:guid}/weeks/{weekNumber:int}/ai-suggestions")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class WeeklySuggestionsController(IMediator mediator) : ControllerBase
{
    public sealed record GenerateRequest(string Language = "es");
    public sealed record ApplyRequest(bool Confirmed);
    [HttpGet]
    [EndpointSummary("Consultar las últimas sugerencias semanales sin generar ni aplicar cambios")]
    [PatientScope(PatientResource.Enrollment, "enrollmentId", "Program.Profile360View")]
    [PatientScope(PatientResource.Enrollment, "enrollmentId", "Program.AiView")]
    public async Task<ActionResult<WeeklySuggestionsResult>> Get(Guid enrollmentId, int weekNumber, [FromQuery] string language = "es", CancellationToken ct = default)
        => Ok(await mediator.Send(new GetWeeklySuggestions(enrollmentId, weekNumber, language), ct));
    [HttpPost]
    [EndpointSummary("Generar sugerencias estimadas para revisión profesional")]
    [PatientScope(PatientResource.Enrollment, "enrollmentId", "Program.Profile360View")]
    [PatientScope(PatientResource.Enrollment, "enrollmentId", "Program.AiView")]
    [PatientScope(PatientResource.Enrollment, "enrollmentId", "Program.AiManage")]
    [PatientScope(PatientResource.Enrollment, "enrollmentId", "Program.Edit")]
    public async Task<ActionResult<WeeklySuggestionsResult>> Generate(Guid enrollmentId, int weekNumber, [FromBody] GenerateRequest body, CancellationToken ct)
        => Ok(await mediator.Send(new GenerateWeeklySuggestions(enrollmentId, weekNumber, body.Language), ct));
    [HttpPost("{briefId:guid}/suggestions/{index:int}/apply")]
    [EndpointSummary("Confirmar y aplicar una sugerencia individual con bitácora")]
    [PatientScope(PatientResource.Enrollment, "enrollmentId", "Program.Profile360View")]
    [PatientScope(PatientResource.Enrollment, "enrollmentId", "Program.AiView")]
    [PatientScope(PatientResource.Enrollment, "enrollmentId", "Program.AiManage")]
    [PatientScope(PatientResource.Enrollment, "enrollmentId", "Program.Edit")]
    public async Task<ActionResult<ApplyWeeklySuggestionResult>> Apply(Guid enrollmentId, int weekNumber, Guid briefId, int index, [FromBody] ApplyRequest body, CancellationToken ct)
        => Ok(await mediator.Send(new ApplyWeeklySuggestion(enrollmentId, weekNumber, briefId, index, body.Confirmed), ct));
}
