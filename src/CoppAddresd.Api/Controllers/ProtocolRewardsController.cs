using System.ComponentModel.DataAnnotations;
using CoppAddresd.Application.Features.Wellness365;
using CoppAddresd.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoppAddresd.Api.Controllers;

[ApiController, Authorize(Policy = Motor365PlanController.PatientWritePolicy)]
[Route("api/v1/people/me/protocol-rewards")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ProtocolRewardsController(IPatientScopeActor actor, IPatientScopeGuard scope,
    IWellnessPlanGate gate, IProtocolRewards rewards) : ControllerBase
{
    [HttpGet]
    public Task<IResult> Get(CancellationToken ct) => WithPatient(rewards.ReadAsync, ct);

    [HttpGet("legacy-history")]
    public Task<IResult> History(CancellationToken ct, [FromQuery, Range(1, 1_000_000)] int page = 1) =>
        WithPatient((patient, token) => rewards.ReadLegacyHistoryAsync(patient, page, token), ct);

    [HttpPost("steps"), RequestSizeLimit(1024)]
    public Task<IResult> Complete(StepInput input, CancellationToken ct) =>
        WithPatient((patient, token) => rewards.CompleteAsync(patient, input.LocalDate, input.Step, token), ct);

    [HttpPost("close"), RequestSizeLimit(1024)]
    public Task<IResult> Close(CloseInput input, CancellationToken ct) =>
        WithPatient((patient, token) => rewards.CloseAsync(patient, input.LocalDate, token), ct);

    private async Task<IResult> WithPatient<T>(Func<Guid, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        if (actor.UserId is not { } user) return Results.Forbid();
        var patient = await scope.EnsureSelfAsync(user, ct);
        var block = await gate.CheckAsync(patient, ct);
        return block is null ? Results.Ok(await action(patient, ct)) : Results.Conflict(new { error = block.Code });
    }

    public sealed record StepInput(DateOnly LocalDate, [Required, StringLength(24)] string Step);
    public sealed record CloseInput(DateOnly LocalDate);
}
