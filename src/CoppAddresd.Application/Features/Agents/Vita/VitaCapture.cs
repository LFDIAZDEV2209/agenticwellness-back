using CoppAddresd.Application.Features.Medications;
using CoppAddresd.Application.Features.ProgramProgress.Commands.RecordWeight;
using CoppAddresd.Application.Features.ProgramProgress.Commands.LogNutrition;
using CoppAddresd.Application.Features.ProgramProgress.DTOs.Nutrition;
using CoppAddresd.Application.Features.ProgramProgress.Events;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Exceptions;
using FluentValidation;
using FluentValidation.Results;
using MediatR;

namespace CoppAddresd.Application.Features.Agents.Vita;

public sealed record ProposeVitaCaptureRequest(string Tool, string Channel, string TurnId, DateTimeOffset ObservedAt,
    string? MetricCode = null, decimal? Value = null, string? UnitCode = null, Guid? SupersedesId = null,
    Guid? DoseId = null, DateTimeOffset? ScheduledAt = null, string? Status = null,
    VitaMealCapture? Meal = null, short? MoodScore = null);

public sealed record ConfirmVitaCaptureRequest(string Token, string Channel, string TurnId,
    bool Confirmed, bool ConfirmedUnusual = false);

public sealed record VitaCaptureData(string Tool, string? MetricCode, decimal? Value, string? UnitCode,
    DateTimeOffset ObservedAt, Guid? SupersedesId, Guid? DoseId, DateTimeOffset? ScheduledAt, string? Status,
    VitaProgramCapture? Program = null);

public sealed record VitaMealCapture(string MealCode, decimal? Calories = null, decimal? ProteinG = null,
    decimal? CarbsG = null, decimal? FatG = null, decimal? FiberG = null)
{
    public NutritionIntakePayload ToIntake(string? source) => new(Calories, ProteinG, CarbsG, FatG, FiberG, null, source, null);
}

public sealed record VitaProgramCapture(Guid EnrollmentId, DateOnly LocalDate, VitaMealCapture? Meal, short? MoodScore);

public sealed record VitaCaptureProposal(Guid PatientId, Guid UserId, string Channel, string TurnId,
    VitaCaptureData Data, DateTimeOffset ExpiresAt, Guid Nonce);

public sealed record VitaCaptureProposalDto(string Token, DateTimeOffset ExpiresAt,
    bool RequiresAdditionalConfirmation, VitaCaptureData Data);

public sealed record VitaCaptureReceiptDto(Guid ReceiptId, string EntityType, Guid EntityId);
public sealed record VitaProgramCaptureEffects(DateOnly LocalDate, string? CompletedTask, string XpReason, int XpAwarded);
public sealed record VitaCaptureCommit(VitaCaptureReceiptDto Receipt, VitaProgramCaptureEffects? Effects = null);

public interface IVitaCaptureProposalTokens
{
    string Protect(VitaCaptureProposal proposal);
    VitaCaptureProposal Unprotect(string token);
}

public interface IVitaCaptureStore
{
    Task ValidateMeasurementAsync(Guid patientId, VitaCaptureData data, CancellationToken ct);
    Task<VitaCaptureCommit> ConfirmAsync(VitaCaptureProposal proposal, CancellationToken ct);
}

public sealed record ProposeVitaCaptureCommand(Guid UserId, ProposeVitaCaptureRequest Input) : IRequest<VitaCaptureProposalDto>;
public sealed record ConfirmVitaCaptureCommand(Guid UserId, ConfirmVitaCaptureRequest Input) : IRequest<VitaCaptureReceiptDto>;

public sealed class ProposeVitaCaptureValidator : AbstractValidator<ProposeVitaCaptureCommand>
{
    public ProposeVitaCaptureValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Input.Tool).Must(x => x is VitaCaptureRules.Measurement or VitaCaptureRules.Medication
            || VitaCaptureRules.IsProgramTool(x));
        RuleFor(x => x.Input.Channel).Must(VitaCaptureRules.IsChannel);
        RuleFor(x => x.Input.TurnId).Must(VitaCaptureRules.IsTurnId);
        RuleFor(x => x.Input.ObservedAt).NotEmpty();
    }
}

public sealed class ConfirmVitaCaptureValidator : AbstractValidator<ConfirmVitaCaptureCommand>
{
    public ConfirmVitaCaptureValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Input.Token).NotEmpty().MaximumLength(4096);
        RuleFor(x => x.Input.Channel).Must(VitaCaptureRules.IsChannel);
        RuleFor(x => x.Input.TurnId).Must(VitaCaptureRules.IsTurnId);
        RuleFor(x => x.Input.Confirmed).Equal(true);
    }
}

public static class VitaCaptureRules
{
    public const string Measurement = "record_measurement";
    public const string Medication = "record_medication_dose";
    public const string Meal = "record_meal";
    public const string CheckIn = "record_checkin";
    private static readonly NutritionIntakePayloadValidator IntakeValidator = new();

    public static bool IsChannel(string? value) => value is "vita_chat" or "vita_voice" or "vita_whatsapp";
    public static bool IsProgramTool(string? value) => value is Meal or CheckIn;
    public static bool IsTurnId(string? value) => value is { Length: > 0 and <= 256 }
        && value == value.Trim() && !value.Any(char.IsControl);

    public static bool RequiresAdditionalConfirmation(VitaCaptureData data) =>
        data is { Tool: Measurement, MetricCode: "temperature_c", Value: 45m };

    public static ValidationException Invalid(string field, string code) => new([new ValidationFailure(field, code)]);

    public static void Validate(VitaCaptureData data, DateTimeOffset now)
    {
        if (data.ObservedAt == default || data.ObservedAt > now)
            throw Invalid("observedAt", "VITA_CAPTURE_INVALID_DATE");

        if (IsProgramTool(data.Tool))
        {
            ValidateProgramCapture(data);
            return;
        }
        if (data.Program is not null)
            throw Invalid("tool", "VITA_CAPTURE_INVALID_PROGRAM");

        if (data.Tool == Medication)
        {
            if (data.DoseId is null || data.DoseId == Guid.Empty || data.ScheduledAt is null || data.ScheduledAt == default(DateTimeOffset)
                || data.Status is not ("taken" or "omitted") || data.SupersedesId is not null
                || data.MetricCode is not null || data.UnitCode is not null || data.Value is not null)
                throw Invalid("doseId", "VITA_CAPTURE_INVALID_DOSE");
            return;
        }

        if (data.Tool != Measurement || data.DoseId is not null || data.ScheduledAt is not null
            || data.Status is not null || data.SupersedesId == Guid.Empty)
            throw Invalid("tool", "VITA_CAPTURE_INVALID_MEASUREMENT");

        (string? Unit, decimal Min, decimal Max, int Scale) limits = data.MetricCode switch
        {
            "weight" => ("kg", WeightInputLimits.Min, WeightInputLimits.Max, 2),
            "heart_rate" => ("bpm", VitalCaptureLimits.HeartRateMin, VitalCaptureLimits.HeartRateMax, 0),
            "systolic_bp" => ("mmhg", VitalCaptureLimits.SystolicMin, VitalCaptureLimits.SystolicMax, 0),
            "diastolic_bp" => ("mmhg", VitalCaptureLimits.DiastolicMin, VitalCaptureLimits.DiastolicMax, 0),
            "o2_saturation" => ("pct", VitalCaptureLimits.OxygenMin, VitalCaptureLimits.OxygenMax, 0),
            "temperature_c" => ("celsius", VitalCaptureLimits.TemperatureMin, VitalCaptureLimits.TemperatureMax, 2),
            _ => default,
        };
        if (limits.Unit is null || data.UnitCode != limits.Unit)
            throw Invalid("unitCode", "VITA_CAPTURE_INVALID_METRIC_OR_UNIT");
        if (data.Value is not { } value || value < limits.Min || value > limits.Max || decimal.Round(value, limits.Scale) != value)
            throw Invalid("value", "VITA_CAPTURE_VALUE_OUT_OF_RANGE");
    }

    private static void ValidateProgramCapture(VitaCaptureData data)
    {
        if (data.Program is not { } program || program.EnrollmentId == Guid.Empty || program.LocalDate == default
            || data.MetricCode is not null || data.Value is not null || data.UnitCode is not null
            || data.SupersedesId is not null || data.DoseId is not null || data.ScheduledAt is not null || data.Status is not null)
            throw Invalid("tool", "VITA_CAPTURE_INVALID_PROGRAM");

        if (data.Tool == CheckIn)
        {
            if (program.Meal is not null || program.MoodScore is not (>= 1 and <= 5))
                throw Invalid("moodScore", "VITA_CAPTURE_INVALID_MOOD");
            return;
        }

        if (program.MoodScore is not null || program.Meal is not { MealCode: "des" or "alm" or "mer" or "cen" } meal)
            throw Invalid("meal", "VITA_CAPTURE_INVALID_MEAL");
        var values = new[] { meal.Calories, meal.ProteinG, meal.CarbsG, meal.FatG, meal.FiberG };
        if (!IntakeValidator.Validate(meal.ToIntake(null)).IsValid
            || values.Any(v => v is { } number && decimal.Round(number, 2) != number))
            throw Invalid("meal", "VITA_CAPTURE_INVALID_NUTRIENT");
    }

    public static DateTimeOffset NormalizeInstant(DateTimeOffset value)
    {
        var utcTicks = value.UtcTicks;
        return new(utcTicks - utcTicks % 10, TimeSpan.Zero);
    }
}

public sealed class VitaCaptureHandlers(VitaPatientAccess access, IVitaCaptureProposalTokens tokens,
    IVitaCaptureStore store, MedicationControlService medications, ICacheService cache, TimeProvider clock,
    IProgramRepository programs, IProgramMetricsQueue? metrics = null)
    : IRequestHandler<ProposeVitaCaptureCommand, VitaCaptureProposalDto>,
      IRequestHandler<ConfirmVitaCaptureCommand, VitaCaptureReceiptDto>
{
    public async Task<VitaCaptureProposalDto> Handle(ProposeVitaCaptureCommand request, CancellationToken ct)
    {
        var patient = await access.RequireAsync(request.UserId, "chat", ct);
        var input = request.Input;
        var now = clock.GetUtcNow();
        var observedAt = VitaCaptureRules.NormalizeInstant(input.ObservedAt);
        if (input.ObservedAt == default || input.ObservedAt > now)
            throw VitaCaptureRules.Invalid("observedAt", "VITA_CAPTURE_INVALID_DATE");
        VitaProgramCapture? program = null;
        if (VitaCaptureRules.IsProgramTool(input.Tool))
        {
            var enrollment = await programs.GetActiveEnrollmentForPatientAsync(patient.Id, ct)
                ?? throw new NotFoundException("VITA_CAPTURE_ENROLLMENT_UNAVAILABLE");
            var zone = TimeZoneInfo.FindSystemTimeZoneById(enrollment.Timezone);
            var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(observedAt, zone).DateTime);
            if (localDate < enrollment.StartLocalDate)
                throw VitaCaptureRules.Invalid("observedAt", "VITA_CAPTURE_INVALID_DATE");
            program = new VitaProgramCapture(enrollment.Id, localDate, input.Meal, input.MoodScore);
        }
        else if (input.Meal is not null || input.MoodScore is not null)
            throw VitaCaptureRules.Invalid("tool", "VITA_CAPTURE_INVALID_PROGRAM");

        var data = new VitaCaptureData(input.Tool, input.MetricCode, input.Value,
            input.UnitCode, observedAt, input.SupersedesId,
            input.DoseId, input.ScheduledAt?.ToUniversalTime(), input.Status, program);
        VitaCaptureRules.Validate(data, now);
        // Las unidades y códigos vienen del catálogo canónico. Nunca se convierten por inferencia.
        if (data.Tool == VitaCaptureRules.Measurement)
            await store.ValidateMeasurementAsync(patient.Id, data, ct);
        else if (data.Tool == VitaCaptureRules.Medication)
        {
            var zone = MedicationControlService.Zone(patient.Timezone);
            var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
            var day = await medications.ReadCurrentDayAsync(patient.Id, localDate, zone, now, ct);
            var dose = day.Doses.SingleOrDefault(x => x.Id == data.DoseId && x.ScheduledAt == data.ScheduledAt!.Value.UtcDateTime)
                ?? throw new NotFoundException("VITA_CAPTURE_DOSE_UNAVAILABLE");
            if (dose.ScheduledAt > now.UtcDateTime || dose.Status != "pending" && dose.Status != data.Status)
                throw new BusinessRuleViolationException("VITA_CAPTURE_DOSE_CHANGED");
        }

        var expiresAt = now.AddMinutes(5);
        var proposal = new VitaCaptureProposal(patient.Id, request.UserId, input.Channel, input.TurnId, data, expiresAt, Guid.NewGuid());
        return new(tokens.Protect(proposal), expiresAt, VitaCaptureRules.RequiresAdditionalConfirmation(data), data);
    }

    public async Task<VitaCaptureReceiptDto> Handle(ConfirmVitaCaptureCommand request, CancellationToken ct)
    {
        var input = request.Input;
        if (!input.Confirmed)
            throw VitaCaptureRules.Invalid("confirmed", "VITA_CAPTURE_CONFIRMATION_REQUIRED");
        var proposal = tokens.Unprotect(input.Token);
        var patient = await access.RequireAsync(request.UserId, "chat", ct);
        if (patient.Id != proposal.PatientId || request.UserId != proposal.UserId)
            throw new ForbiddenException("VITA_CAPTURE_OWNER_MISMATCH");
        if (input.Channel != proposal.Channel || input.TurnId != proposal.TurnId)
            throw new BusinessRuleViolationException("VITA_CAPTURE_TURN_MISMATCH");
        var now = clock.GetUtcNow();
        if (proposal.ExpiresAt <= now)
            throw VitaCaptureRules.Invalid("token", "VITA_CAPTURE_EXPIRED");
        VitaCaptureRules.Validate(proposal.Data, now);
        if (VitaCaptureRules.RequiresAdditionalConfirmation(proposal.Data) && !input.ConfirmedUnusual)
            throw new BusinessRuleViolationException("VITA_CAPTURE_ADDITIONAL_CONFIRMATION_REQUIRED");
        if (proposal.Data.Tool == VitaCaptureRules.Medication)
            medications.EnsureEnabled();

        var result = await store.ConfirmAsync(proposal, ct);
        if (proposal.Data.Tool == VitaCaptureRules.Measurement)
        {
            await cache.RemoveAsync(CacheKeys.MetricsHistory(patient.Id), ct);
            await cache.RemoveAsync(CacheKeys.MyMetricsHistory(patient.Id), ct);
        }
        if (metrics is not null && result.Effects is { } effects)
        {
            if (effects.CompletedTask is { } task)
                await metrics.EnqueueAsync(new TaskCompletedMetricEvent(effects.LocalDate, task, effects.XpAwarded), ct);
            if (effects.XpAwarded > 0)
                await metrics.EnqueueAsync(new XpAwardedMetricEvent(effects.LocalDate, effects.XpReason, effects.XpAwarded), ct);
        }
        return result.Receipt;
    }
}
