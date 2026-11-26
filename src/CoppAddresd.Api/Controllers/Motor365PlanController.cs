using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using CoppAddresd.Api.Features.Wellness365;
using CoppAddresd.Application.Features.Wellness365;
using CoppAddresd.Application.Features.Motor365;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoppAddresd.Api.Controllers;

[ApiController, Authorize, Route("api/v1/people/{personId}")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class Motor365PlanController(IPatientScopeGuard scope, IPatientScopeActor actor,
    MotorPeople people, IWellnessPlanGate gate) : ControllerBase
{
    public const string PatientWritePolicy = "Motor365PatientWrite";

    [HttpGet("plan"), HttpGet("plan/{day}")]
    [EndpointSummary("Consultar el día y estado del plan de Motor 365")]
    [ProducesResponseType<Motor365DayResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IResult> Get(string personId, string? day, CancellationToken ct, [FromQuery] bool includeSync = false,
        [FromQuery, Range(1, 365)] int historyDays = 28, [FromQuery] bool includeMenu = false)
    {
        var (patientId, failure) = await ResolveAsync(personId, write: false, ct);
        if (failure is not null) return failure;
        var block = await gate.CheckAsync(patientId, ct);
        if (block is not null) return Locked(block);
        int? requested = null;
        if (day is not null)
        {
            if (!int.TryParse(day, out var number)) return new WellnessPlanError(WellnessPlanErrorCode.BadDay).ToHttpResult();
            requested = number;
        }
        try
        {
            return Results.Ok(Motor365DayResponse.From(
                await people.ReadCalendarDayAsync(patientId, requested, ct, includeSync ? historyDays : null, includeMenu)));
        }
        catch (MotorException ex) { return MotorError(ex); }
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
        CancellationToken ct, [FromQuery] bool createIfAbsent = false)
    {
        var (patientId, failure) = await ResolveAsync(personId, write: true, ct);
        if (failure is not null) return failure;
        var block = await gate.CheckAsync(patientId, ct);
        if (block is not null) return Locked(block);
        if (!int.TryParse(day, out var number) || number is < 1 or > 365)
            return new WellnessPlanError(WellnessPlanErrorCode.BadDay).ToHttpResult();

        var input = request.ToInput();
        if (!input.IsValid()) return new WellnessPlanError(WellnessPlanErrorCode.BadLog).ToHttpResult();
        try
        {
            await people.CloseDayAsync(new(patientId, number,
                new(input.Adherence, input.SleepHours, input.Energy, input.Pain, input.Symptoms.ToArray(),
                    input.WeightKg, input.WaistCm, input.CompletedExercise ? 1 : 0), createIfAbsent), ct);
            return Results.NoContent();
        }
        catch (MotorException ex) { return MotorError(ex); }
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

    private static IResult Locked(CoppAddresd.Domain.Wellness365.WellnessBlock block) =>
        new WellnessPlanError(WellnessPlanErrorCode.PlanLocked, new("locked", block.Code, block.Message)).ToHttpResult();

    private IResult MotorError(MotorException ex) => ex.Code switch
    {
        "bad_day" => new WellnessPlanError(WellnessPlanErrorCode.BadDay).ToHttpResult(),
        "future_day" => new WellnessPlanError(WellnessPlanErrorCode.FutureDay).ToHttpResult(),
        "person_not_found" => Missing().Item2,
        "plan_locked" when ex.Status is { Code: { } code, Message: { } message } =>
            Locked(new(code, new(message.Es, message.En))),
        "forbidden" => Results.Forbid(),
        "version_conflict" or "log_already_recorded" => Results.Conflict(new { error = ex.Code }),
        "service_unavailable" => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
        _ => throw ex
    };

    private static (Guid, IResult) Missing() =>
        (default, new WellnessPlanError(WellnessPlanErrorCode.PersonNotFound).ToHttpResult());
}

public sealed record Motor365DayResponse(int Day, int Today, DateOnly TodayLocalDate, DateOnly StartDate,
    string TimeZoneId, bool Real, WellnessPlanStatus Status, string LogStatus, double? Adherence,
    double? MeasuredWeightKg, int Streak, MotorExerciseSessionDto? Exercise, MotorNutritionTargets Nutrition,
    [property: JsonPropertyName("engine_ver")] string EngineVersion,
    [property: JsonPropertyName("params_version")] int ParametersVersion)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MotorSyncSnapshot? Sync { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MotorMealPlanDto? MealPlan { get; init; }

    internal static Motor365DayResponse From(MotorCalendarDay day) =>
        new Motor365DayResponse(day.Day, day.Today, day.TodayLocalDate, day.StartDate, day.TimeZoneId, true, new(day.State), day.LogStatus,
            day.Adherence, day.MeasuredWeightKg, day.Streak, day.Exercise, day.Nutrition,
            CoppAddresd.Domain.Wellness365.WellnessEngine.ReferenceVersion,
            day.ParametersVersion) { Sync = day.Sync, MealPlan = day.MealPlan };
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
