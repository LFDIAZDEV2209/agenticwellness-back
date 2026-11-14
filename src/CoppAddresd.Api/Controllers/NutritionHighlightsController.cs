using CoppAddresd.Api.Authorization;
using CoppAddresd.Api.Constants;
using CoppAddresd.Application.Features.Nutrition.Commands;
using CoppAddresd.Application.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoppAddresd.Api.Controllers;

[ApiController]
[Authorize(Policy = NutritionPolicies.Staff)]
[Route("api/v1/nutrition/cycles")]
public sealed class NutritionHighlightsController(IMediator mediator) : ControllerBase
{
    public sealed record PinRequest(string? Note = null);

    [HttpPost("{id:guid}/highlights/{intakeId:guid}")]
    [PatientScope(PatientResource.Cycle, "id", "Nutrition.Manage")]
    [PatientScope(PatientResource.Intake, "intakeId", "Nutrition.Manage")]
    public async Task<IActionResult> Pin(Guid id, Guid intakeId, [FromBody] PinRequest body, CancellationToken ct)
    { await mediator.Send(new PinIntakeExample(id, intakeId, body.Note), ct); return NoContent(); }

    [HttpDelete("{id:guid}/highlights/{intakeId:guid}")]
    [PatientScope(PatientResource.Cycle, "id", "Nutrition.Manage")]
    [PatientScope(PatientResource.Intake, "intakeId", "Nutrition.Manage")]
    public async Task<IActionResult> Unpin(Guid id, Guid intakeId, CancellationToken ct)
    { await mediator.Send(new UnpinIntakeExample(id, intakeId), ct); return NoContent(); }

    [HttpGet("{id:guid}/highlights")]
    [PatientScope(PatientResource.Cycle, "id", "Nutrition.View")]
    [PatientScope(PatientResource.Cycle, "id", "Nutrition.RecordsView")]
    public async Task<ActionResult<IntakeExamplesPage>> List(Guid id, CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 30)
        => Ok(await mediator.Send(new GetIntakeExamples(id, page, pageSize), ct));
}
