using System.Text.Json;
using System.Text.Json.Serialization;
using CoppAddresd.Domain.Wellness365;

namespace CoppAddresd.Application.Features.Motor365;

public sealed record MotorPersonInput(
    Guid? Id, Guid? SubmissionId, string? N, string? Sex, int? Age, decimal? H,
    decimal? W0, decimal? Waist0, decimal? Target, int? Act, string? Goal, string? Pace,
    int? Veg, int? Nolac, short?[]? ParqA, short?[]? NqA, bool? Cons, bool? Ncons,
    MotorTerms? Terms, DateOnly? StartDate, Dictionary<int, MotorLogInput>? Log, int? Ver);
public sealed record MotorTerms(DateTimeOffset? At, string? Version);
public sealed record MotorLogInput(
    decimal? Adh, decimal? Sleep, int? Energy, int? Pain, string[]? Sym, decimal? W, decimal? Waist, int? Ex);
public sealed record MotorText(string Es, string En);
public sealed record MotorStatus(bool Ok, string? Code = null, string? Msg = null, MotorText? Message = null);
public sealed record MotorPersonResult(Guid Id, MotorStatus Status, Guid? SubmissionId, int Ver);
public sealed record MotorPersonWrite(MotorPersonResult Result, bool Created);
public sealed record MotorPersonDto(
    Guid Id, string N, string Sex, int Age, decimal H, decimal W0, decimal Waist0, decimal? Target,
    int Act, string Goal, string Pace, int Veg, int Nolac, short?[]? ParqA, short?[]? NqA,
    bool Cons, bool Ncons, MotorTerms? Terms, DateOnly StartDate, int Day, int Ver,
    Guid? SubmissionId, MotorStatus Status, IReadOnlyDictionary<int, MotorLogInput> Log);

public sealed record MotorPlanDto(Guid Id, int Today, int Ver, IReadOnlyList<MotorDayDto> Days,
    [property: JsonPropertyName("engine_ver")] string EngineVersion,
    [property: JsonPropertyName("params_version")] int ParamsVersion);
public sealed record MotorDayDto(int D, int Wk, string Mode, double Kc,
    [property: JsonPropertyName("P")] double Protein,
    [property: JsonPropertyName("C")] double Carbs,
    [property: JsonPropertyName("F")] double Fat,
    int Fiber, int Water, double W, char Type, bool Train, bool Pause, bool Simple,
    double AdjK, MotorExerciseDto Exercise, IReadOnlyList<MotorText> Why,
    [property: JsonPropertyName("engine_ver")] string EngineVersion,
    [property: JsonPropertyName("params_version")] int ParamsVersion)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<MotorMealDto>? Menu { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MotorMacrosDto? Totals { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? DeviationPct { get; init; }
}
public sealed record MotorExerciseDto(int Level, int Minutes, bool Deload, string Phase,
    MotorText PhaseName, IReadOnlyList<MotorExerciseItemDto> Items);
public sealed record MotorExerciseItemDto(string Id, int Minutes, int? Sets, int? Reps, int? Rest, int? Hold, string? Rir);
public sealed record MotorMealDto(string Meal, MotorText Name, IReadOnlyList<MotorFoodPortion> Items, double Kcal,
    [property: JsonPropertyName("P")] double Protein, [property: JsonPropertyName("C")] double Carbs,
    [property: JsonPropertyName("F")] double Fat);
public sealed record MotorFoodPortion(char Role, string Food, double Grams);
public sealed record MotorMacrosDto([property: JsonPropertyName("k")] double Kcal,
    [property: JsonPropertyName("P")] double Protein, [property: JsonPropertyName("C")] double Carbs,
    [property: JsonPropertyName("F")] double Fat);
public sealed record MotorEventDto(int D, string K, double? Delta, double? Rate, string Es, string En,
    [property: JsonPropertyName("engine_ver")] string EngineVersion,
    [property: JsonPropertyName("params_version")] int ParamsVersion);
public sealed record MotorLogResult(bool Ok, int Ver);
public sealed record MotorParamsInput(JsonElement Params, string? Reason, int? ExpectedVersion);
public sealed record MotorParamsDto(WellnessParameters Params, WellnessParameters Defaults,
    [property: JsonPropertyName("engine_ver")] string EngineVersion,
    [property: JsonPropertyName("params_version")] int ParamsVersion);
public sealed record MotorAuditEntry(DateTimeOffset At, Guid? Who, string Action, string Ref);
public sealed record MotorHealth(bool Ok, string Engine);
public sealed record MotorExerciseCatalogDto(string Id, string N, string Cat, string Imp, int Min, string Ic, string How);
public sealed record MotorErrorIssue(string Code, string Field, int? Step = null);

public sealed class MotorException(string code, MotorStatus? status = null, IReadOnlyList<MotorErrorIssue>? issues = null)
    : Exception(code)
{
    public string Code { get; } = code;
    public MotorStatus? Status { get; } = status;
    public IReadOnlyList<MotorErrorIssue> Issues { get; } = issues ?? [];
}
