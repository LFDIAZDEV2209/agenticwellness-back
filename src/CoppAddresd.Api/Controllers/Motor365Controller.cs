using CoppAddresd.Api.Configuration;
using CoppAddresd.Application.Features.Motor365;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoppAddresd.Api.Controllers;

/// <summary>Contrato v1 del Motor 365 para la app y el ERP.</summary>
[ApiController]
[ApiExplorerSettings(GroupName = "motor365")]
[Route("v1")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Produces("application/json")]
[ProducesResponseType<MotorApiError>(500)]
[ProducesResponseType<MotorApiError>(503)]
public sealed class Motor365Controller(ISender sender) : ControllerBase
{
    [HttpPost("people", Name = "Motor365UpsertPerson")]
    [Authorize(Policy = Motor365Api.People)]
    [RequestSizeLimit(131_072)]
    [ProducesResponseType<MotorPersonResult>(200)]
    [ProducesResponseType<MotorPersonResult>(201)]
    [ProducesResponseType<MotorApiError>(400)]
    [ProducesResponseType<MotorApiError>(401)]
    [ProducesResponseType<MotorApiError>(403)]
    [ProducesResponseType<MotorApiError>(404)]
    [ProducesResponseType<MotorApiError>(409)]
    [ProducesResponseType<MotorApiError>(413)]
    [ProducesResponseType<MotorApiError>(415)]
    [ProducesResponseType<MotorApiError>(422)]
    public async Task<ActionResult<MotorPersonResult>> UpsertPerson(MotorPersonInput input, CancellationToken ct)
    {
        var result = await sender.Send(new WriteMotorPerson(null, input), ct);
        return result.Created ? CreatedAtAction(nameof(GetPerson), new { id = result.Result.Id }, result.Result) : Ok(result.Result);
    }

    [HttpGet("people/{id}", Name = "Motor365GetPerson")]
    [Authorize(Policy = Motor365Api.People)]
    [ProducesResponseType<MotorPersonDto>(200)]
    [ProducesResponseType<MotorApiError>(400)]
    [ProducesResponseType<MotorApiError>(401)]
    [ProducesResponseType<MotorApiError>(403)]
    [ProducesResponseType<MotorApiError>(404)]
    public Task<MotorPersonDto> GetPerson(Guid id, CancellationToken ct) => sender.Send(new GetMotorPerson(id), ct);

    [HttpPut("people/{id}", Name = "Motor365UpdatePerson")]
    [Authorize(Policy = Motor365Api.People)]
    [RequestSizeLimit(131_072)]
    [ProducesResponseType<MotorPersonResult>(200)]
    [ProducesResponseType<MotorApiError>(400)]
    [ProducesResponseType<MotorApiError>(401)]
    [ProducesResponseType<MotorApiError>(403)]
    [ProducesResponseType<MotorApiError>(404)]
    [ProducesResponseType<MotorApiError>(409)]
    [ProducesResponseType<MotorApiError>(413)]
    [ProducesResponseType<MotorApiError>(415)]
    [ProducesResponseType<MotorApiError>(422)]
    public Task<MotorPersonResult> UpdatePerson(Guid id, MotorPersonInput input, CancellationToken ct) =>
        sender.Send(new UpdateMotorPerson(id, input), ct);

    [HttpGet("people/{id}/plan", Name = "Motor365GetPlan")]
    [Authorize(Policy = Motor365Api.People)]
    [ProducesResponseType<MotorPlanDto>(200)]
    [ProducesResponseType<MotorApiError>(400)]
    [ProducesResponseType<MotorApiError>(401)]
    [ProducesResponseType<MotorApiError>(403)]
    [ProducesResponseType<MotorApiError>(404)]
    [ProducesResponseType<MotorApiError>(409)]
    public Task<MotorPlanDto> GetPlan(Guid id, CancellationToken ct) => sender.Send(new GetMotorPlan(id), ct);

    [HttpGet("people/{id}/plan/{day}", Name = "Motor365GetPlanDay")]
    [Authorize(Policy = Motor365Api.People)]
    [ProducesResponseType<MotorDayDto>(200)]
    [ProducesResponseType<MotorApiError>(400)]
    [ProducesResponseType<MotorApiError>(401)]
    [ProducesResponseType<MotorApiError>(403)]
    [ProducesResponseType<MotorApiError>(404)]
    [ProducesResponseType<MotorApiError>(409)]
    [ProducesResponseType<MotorApiError>(422)]
    public Task<MotorDayDto> GetPlanDay(Guid id, int day, CancellationToken ct) => sender.Send(new GetMotorDay(id, day), ct);

    [HttpPut("people/{id}/logs/{day}", Name = "Motor365CloseDay")]
    [Authorize(Policy = Motor365Api.People)]
    [RequestSizeLimit(4096)]
    [ProducesResponseType<MotorLogResult>(200)]
    [ProducesResponseType<MotorApiError>(400)]
    [ProducesResponseType<MotorApiError>(401)]
    [ProducesResponseType<MotorApiError>(403)]
    [ProducesResponseType<MotorApiError>(404)]
    [ProducesResponseType<MotorApiError>(409)]
    [ProducesResponseType<MotorApiError>(413)]
    [ProducesResponseType<MotorApiError>(415)]
    [ProducesResponseType<MotorApiError>(422)]
    public Task<MotorLogResult> CloseDay(Guid id, int day, MotorLogInput input, CancellationToken ct) =>
        sender.Send(new PutMotorLog(id, day, input), ct);

    [HttpGet("people/{id}/events", Name = "Motor365GetEvents")]
    [Authorize(Policy = Motor365Api.People)]
    [ProducesResponseType<IReadOnlyList<MotorEventDto>>(200)]
    [ProducesResponseType<MotorApiError>(400)]
    [ProducesResponseType<MotorApiError>(401)]
    [ProducesResponseType<MotorApiError>(403)]
    [ProducesResponseType<MotorApiError>(404)]
    [ProducesResponseType<MotorApiError>(409)]
    public Task<IReadOnlyList<MotorEventDto>> GetEvents(Guid id, CancellationToken ct) => sender.Send(new GetMotorEvents(id), ct);

    [HttpGet("engine/params", Name = "Motor365GetParameters")]
    [Authorize(Policy = Motor365Api.ReadParams)]
    [ProducesResponseType<MotorParamsDto>(200)]
    [ProducesResponseType<MotorApiError>(401)]
    [ProducesResponseType<MotorApiError>(403)]
    public Task<MotorParamsDto> GetParameters(CancellationToken ct) => sender.Send(new GetMotorParams(), ct);

    [HttpPut("engine/params", Name = "Motor365UpdateParameters")]
    [Authorize(Policy = Motor365Api.WriteParams)]
    [RequestSizeLimit(8192)]
    [ProducesResponseType<MotorParamsDto>(200)]
    [ProducesResponseType<MotorApiError>(400)]
    [ProducesResponseType<MotorApiError>(401)]
    [ProducesResponseType<MotorApiError>(403)]
    [ProducesResponseType<MotorApiError>(409)]
    [ProducesResponseType<MotorApiError>(413)]
    [ProducesResponseType<MotorApiError>(415)]
    [ProducesResponseType<MotorApiError>(422)]
    public Task<MotorParamsDto> UpdateParameters(MotorParamsInput input, CancellationToken ct) =>
        sender.Send(new PutMotorParams(input), ct);

    [HttpGet("catalog/foods", Name = "Motor365GetFoods")]
    [Authorize(Policy = Motor365Api.People)]
    [ProducesResponseType<IReadOnlyDictionary<string, IReadOnlyList<object>>>(200)]
    [ProducesResponseType<MotorApiError>(401)]
    [ProducesResponseType<MotorApiError>(403)]
    public Task<IReadOnlyDictionary<string, IReadOnlyList<object>>> GetFoods(CancellationToken ct) => sender.Send(new GetMotorFoods(), ct);

    [HttpGet("catalog/exercises", Name = "Motor365GetExercises")]
    [Authorize(Policy = Motor365Api.People)]
    [ProducesResponseType<IReadOnlyList<MotorExerciseCatalogDto>>(200)]
    [ProducesResponseType<MotorApiError>(401)]
    [ProducesResponseType<MotorApiError>(403)]
    public Task<IReadOnlyList<MotorExerciseCatalogDto>> GetExercises(CancellationToken ct) => sender.Send(new GetMotorExercises(), ct);

    [HttpGet("audit", Name = "Motor365GetAudit")]
    [Authorize(Policy = Motor365Api.Audit)]
    [ProducesResponseType<IReadOnlyList<MotorAuditEntry>>(200)]
    [ProducesResponseType<MotorApiError>(400)]
    [ProducesResponseType<MotorApiError>(401)]
    [ProducesResponseType<MotorApiError>(403)]
    public Task<IReadOnlyList<MotorAuditEntry>> GetAudit(CancellationToken ct, int page = 1, int pageSize = 20) =>
        sender.Send(new GetMotorAudit(page, pageSize), ct);

    [HttpGet("health", Name = "Motor365Health")]
    [AllowAnonymous]
    [ProducesResponseType<MotorHealth>(200)]
    public Task<MotorHealth> Health(CancellationToken ct) => sender.Send(new GetMotorHealth(), ct);
}
