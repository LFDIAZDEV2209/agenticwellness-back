using System.ComponentModel.DataAnnotations;
using CoppAddresd.Api.Configuration;
using CoppAddresd.Application.Features.Motor365;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoppAddresd.Api.Controllers;

[ApiController]
[Authorize(Policy = Motor365Api.ReadParameterHistory)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/v1/erp/motor365/parameters/versions")]
public sealed class MotorParameterHistoryController(MotorParameterHistory history) : ControllerBase
{
    [HttpGet]
    [EndpointSummary("Consultar las revisiones publicadas de los parámetros del Motor 365")]
    public Task<MotorParameterHistoryPage> Get(CancellationToken ct, [FromQuery, Range(1, int.MaxValue)] int? before,
        [FromQuery, Range(1, 50)] int limit = 20) => history.ReadAsync(before, limit, ct);

    [HttpGet("{version:int:min(1)}")]
    [EndpointSummary("Consultar una revisión inmutable de los parámetros del Motor 365")]
    public async Task<ActionResult<MotorParameterVersionDetails>> GetVersion(int version, CancellationToken ct) =>
        await history.ReadVersionAsync(version, ct) is { } revision ? Ok(revision) : NotFound();
}
