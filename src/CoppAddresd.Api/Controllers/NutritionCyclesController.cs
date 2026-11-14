using CoppAddresd.Api.Authorization;
using CoppAddresd.Api.Constants;
using CoppAddresd.Application.Features.Nutrition.Queries;
using CoppAddresd.Application.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoppAddresd.Api.Controllers;

[ApiController]
[Authorize(Policy = NutritionPolicies.Staff)]
[Route("api/v1/nutrition/cycles")]
public sealed class NutritionCyclesController(IMediator mediator) : ControllerBase
{
    [HttpGet("{id:guid}/adherence")]
    [PatientScope(PatientResource.Cycle, "id", "Nutrition.View")]
    public async Task<ActionResult<CycleAdherenceDto>> Adherence(Guid id, CancellationToken ct,
        [FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 30)
        => Ok(await mediator.Send(new GetCycleAdherenceQuery(id, from, to, page, pageSize), ct));
}
