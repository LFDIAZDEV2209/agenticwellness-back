using CoppAddresd.Api.Authorization;
using CoppAddresd.Api.Constants;
using CoppAddresd.Application.Features.Nutrition.Queries;
using CoppAddresd.Application.Features.Nutrition.Commands;
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
    [HttpPost("{id:guid}/days/{day}/review")]
    [PatientScope(PatientResource.Cycle, "id", "Nutrition.Manage")]
    public async Task<ActionResult<IntakeDayReviewDto>> MarkDay(Guid id, DateOnly day, CancellationToken ct)
        => Ok(await mediator.Send(new MarkIntakeDayReviewed(id, day), ct));

    [HttpDelete("{id:guid}/days/{day}/review")]
    [PatientScope(PatientResource.Cycle, "id", "Nutrition.Manage")]
    public async Task<ActionResult<IntakeDayReviewDto>> UnmarkDay(Guid id, DateOnly day, CancellationToken ct)
        => Ok(await mediator.Send(new UnmarkIntakeDayReviewed(id, day), ct));

    [HttpGet("{id:guid}/days/reviews")]
    [PatientScope(PatientResource.Cycle, "id", "Nutrition.View")]
    [PatientScope(PatientResource.Cycle, "id", "Nutrition.RecordsView")]
    public async Task<ActionResult<IReadOnlyList<IntakeDayReviewDto>>> DayReviews(Guid id, [FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken ct)
        => Ok(await mediator.Send(new GetIntakeDayReviews(id, from, to), ct));

    [HttpGet("{id:guid}/adherence")]
    [PatientScope(PatientResource.Cycle, "id", "Nutrition.View")]
    public async Task<ActionResult<CycleAdherenceDto>> Adherence(Guid id, CancellationToken ct,
        [FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 30)
        => Ok(await mediator.Send(new GetCycleAdherenceQuery(id, from, to, page, pageSize), ct));
}
