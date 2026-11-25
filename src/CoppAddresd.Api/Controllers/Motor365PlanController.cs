using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using CoppAddresd.Api.Features.Wellness365;
using CoppAddresd.Application.Features.Wellness365;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoppAddresd.Api.Controllers;

[ApiController, Authorize, Route("api/v1/people/{personId}")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class Motor365PlanController(IPatientScopeGuard scope, IPatientScopeActor actor,
    IWellnessPlanStore store, IWellnessPlanGate gate, WellnessPlanService plans) : ControllerBase
{
    public const string PatientWritePolicy = "Motor365PatientWrite";

    [HttpGet("plan"), HttpGet("plan/{day}")]
    [EndpointSummary("Consultar el día y estado del plan de Motor 365")]
    [ProducesResponseType<Motor365DayResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IResult> Get(string personId, string? day, CancellationToken ct)
    {
        var (patientId, failure) = await ResolveAsync(personId, write: false, ct);
        if (failure is not null) return failure;
        var block = await gate.CheckAsync(patientId, includeScreening: true, ct);
        var result = plans.GetDay(await store.ReadAsync(patientId, block, ct), day);
        return result.Error is { } error ? error.ToHttpResult() : Results.Ok(Motor365DayResponse.From(result.Value!));
    }

    [HttpPut("logs/{day}"), Authorize(Policy = PatientWritePolicy)]
    [EndpointSummary("Guardar el cierre de un día del Motor 365 para la persona autenticada")]
    [RequestSizeLimit(4096)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IResult> Close(string personId, string day, [FromBody] Motor365ClosureRequest request,
        CancellationToken ct)
    {
        var (patientId, failure) = await ResolveAsync(personId, write: true, ct);
        if (failure is not null) return failure;
        var block = await gate.CheckAsync(patientId, includeScreening: false, ct);
        if (block is not null)
            return new WellnessPlanError(WellnessPlanErrorCode.PlanLocked, new("locked", block.Code, block.Message)).ToHttpResult();
        var result = await store.CloseAsync(patientId, actor.UserId!.Value, day, request.ToInput(), ct);
        return result.Error is { } error ? error.ToHttpResult() : Results.NoContent();
    }

    private async Task<(Guid Patient, IResult? Failure)> ResolveAsync(string personId, bool write, CancellationToken ct)
    {
        try
        {
            if (actor.UserId is not { } user) return (default, Results.Forbid());
            if (User.HasClaim("aud", "app"))
            {
                var self = await scope.EnsureSelfAsync(user, ct);
                return personId == "me" || (Guid.TryParse(personId, out var requested) && requested == self)
                    ? (self, null) : Missing();
            }
            if (write || !User.HasClaim("aud", "erp") || personId == "me") return (default, Results.Forbid());
            if (!Guid.TryParse(personId, out var patient)) return Missing();
            await scope.EnsureAsync(patient, "Program.Profile360View", ct);
            return (patient, null);
        }
        catch (NotFoundException) { return Missing(); }
        catch (ForbiddenException) { return (default, Results.Forbid()); }
    }

    private static (Guid, IResult) Missing() =>
        (default, new WellnessPlanError(WellnessPlanErrorCode.PersonNotFound).ToHttpResult());
}

public sealed record Motor365DayResponse(int Day, int Today, DateOnly TodayLocalDate, DateOnly StartDate,
    string TimeZoneId, bool Real, WellnessPlanStatus Status, string LogStatus, double? Adherence,
    double? MeasuredWeightKg, int Streak,
    [property: JsonPropertyName("engine_ver")] string EngineVersion,
    [property: JsonPropertyName("params_version")] int ParametersVersion)
{
    internal static Motor365DayResponse From(WellnessPlanDayView day) =>
        new(day.Day, day.Today, day.TodayLocalDate, day.StartDate, day.TimeZoneId, day.Real, day.Status, day.LogStatus,
            day.Adherence, day.MeasuredWeightKg, day.Streak, day.EngineVersion, day.ParametersVersion);
}

public sealed record Motor365ClosureRequest(
    [Required] [property: JsonPropertyName("adh")] decimal? Adherence,
    [Required] [property: JsonPropertyName("sleep")] decimal? SleepHours,
    [Required] int? Energy,
    [Required] int? Pain,
    [property: JsonPropertyName("sym")] string[]? Symptoms,
    [property: JsonPropertyName("w")] decimal? WeightKg,
    [property: JsonPropertyName("waist")] decimal? WaistCm,
    [Required] [property: JsonPropertyName("ex")] bool? CompletedExercise)
{
    internal WellnessLogInput ToInput() =>
        new(Adherence!.Value, SleepHours!.Value, Energy!.Value, Pain!.Value, WeightKg, WaistCm,
            CompletedExercise!.Value, Symptoms ?? []);
}
