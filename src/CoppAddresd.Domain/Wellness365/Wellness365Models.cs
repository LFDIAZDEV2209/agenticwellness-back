using System.Text.Json.Serialization;

namespace CoppAddresd.Domain.Wellness365;

public enum WellnessGoal { LoseWeight, GainMuscle, Tone }
public enum WellnessPace { Slow, Moderate, Maximum }
public enum WellnessSex { Female, Male }
public enum WellnessMode { Main, Break, Maintain }

public sealed record WellnessProfile(
    WellnessSex Sex,
    int Age,
    double HeightCm,
    double InitialWeightKg,
    double InitialWaistCm,
    double? TargetWeightKg,
    int ActivityLevel,
    WellnessGoal Goal,
    int CurrentDay,
    uint Seed)
{
    public WellnessParameters Parameters { get; init; } = WellnessParameters.Default;
    public WellnessPace Pace { get; init; } = WellnessPace.Moderate;
    public IReadOnlyDictionary<int, WellnessDailyLog> Logs { get; init; } =
        new Dictionary<int, WellnessDailyLog>();
    public bool Real { get; init; } = true;
    public bool Vegetarian { get; init; }
    public bool LactoseFree { get; init; }
    public int FitnessLevel { get; init; }
    public double DemoWeighProbability { get; init; } = .8;
    public double DemoHistoricalAdherence { get; init; } = .8;
    public double ProjectedAdherence { get; init; } = .85;
    public double MetabolicModifier { get; init; }
    public double? CurrentWeightKg { get; init; }
    public double? CurrentCalorieAdjustment { get; init; }
    public int? CurrentStreak { get; init; }
}

public sealed record WellnessDailyLog(
    double Adherence, double SleepHours, int Energy, int Pain,
    double? MeasuredWeightKg, bool CompletedExercise)
{
    public IReadOnlyList<string> Symptoms { get; init; } = [];
}

public sealed record WellnessText(string Es, string En);
public sealed record WellnessBlock(string Code, WellnessText Message);
public sealed record WellnessMacros(double Kcal, double Protein, double Carbs, double Fat)
{
    public static WellnessMacros operator +(WellnessMacros a, WellnessMacros b) =>
        new(a.Kcal + b.Kcal, a.Protein + b.Protein, a.Carbs + b.Carbs, a.Fat + b.Fat);

    internal static WellnessMacros Zero { get; } = new(0, 0, 0, 0);

    internal double For(char role) => role switch
    {
        'P' => Protein,
        'C' => Carbs,
        'F' => Fat,
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };
}

public sealed record WellnessMealItem(char Role, string FoodId, double Grams);
public sealed record WellnessMeal(int TemplateIndex, WellnessText Name,
    IReadOnlyList<WellnessMealItem> Items, WellnessMacros Totals);
public sealed record WellnessMealOptions(double TargetKcal, IReadOnlyList<WellnessMeal> Options);
public sealed record WellnessMenu(IReadOnlyList<WellnessMeal> Meals, WellnessMacros Totals, double Deviation);
public sealed record WellnessPlanDay(WellnessDay Recommendation, WellnessMenu Menu)
{
    [JsonPropertyName("engine_ver")]
    public string EngineVersion { get; init; } = WellnessEngine.ReferenceVersion;
    [JsonPropertyName("params_version")]
    public int ParametersVersion { get; init; } = WellnessEngine.ReferenceParametersVersion;
}

public sealed record WellnessExerciseDefinition(
    string Id,
    [property: JsonPropertyName("n")] string Name,
    [property: JsonPropertyName("cat")] string Category,
    [property: JsonPropertyName("imp")] string Impact,
    [property: JsonPropertyName("min")] int MinimumLevel,
    [property: JsonPropertyName("ic")] string Icon,
    string How);
public sealed record WellnessExerciseGuide(WellnessExerciseInstructions Es, WellnessExerciseInstructions En);
public sealed record WellnessExerciseInstructions(
    [property: JsonPropertyName("n")] string Name,
    [property: JsonPropertyName("s")] IReadOnlyList<string> Steps,
    [property: JsonPropertyName("b")] string Breathing,
    [property: JsonPropertyName("m")] string CommonMistakes,
    [property: JsonPropertyName("e")] string Easier,
    [property: JsonPropertyName("h")] string Harder);
public sealed record WellnessPhase(WellnessText Name, int FirstWeek, int LastWeek, int Level);
public sealed record WellnessStrengthDose(int Sets, int Reps, int RestSeconds, int HoldSeconds, string RepsInReserve);
public sealed record WellnessExerciseItem(string Id, string Tag, bool NewlyUnlocked, int Minutes,
    WellnessExerciseDefinition Definition, WellnessStrengthDose? Strength);
public sealed record WellnessExerciseDay(char Type, WellnessText Name, int Level, int Minutes,
    bool Deload, int Week, int Boost, WellnessPhase Phase, IReadOnlyList<WellnessExerciseItem> Items, bool Training);

public sealed record WellnessEvent(int Day, string Kind, double? CalorieDelta, double? RatePercent, WellnessText Message)
{
    [JsonPropertyName("engine_ver")]
    public string EngineVersion { get; init; } = WellnessEngine.ReferenceVersion;
    [JsonPropertyName("params_version")]
    public int ParametersVersion { get; init; } = WellnessEngine.ReferenceParametersVersion;
}

public sealed record WellnessSimulation(IReadOnlyList<WellnessDay> Days, IReadOnlyList<WellnessEvent> Events, bool TargetReached)
{
    [JsonPropertyName("engine_ver")]
    public string EngineVersion { get; init; } = WellnessEngine.ReferenceVersion;
    [JsonPropertyName("params_version")]
    public int ParametersVersion { get; init; } = WellnessEngine.ReferenceParametersVersion;
}

public sealed record WellnessDay(
    int Day, int Week, int DayOfWeek, WellnessMode Mode, WellnessMacros Targets, int FiberGrams,
    int WaterMl, double WeightKg, bool Training, char Type, bool SimpleMenu, bool Paused,
    IReadOnlyList<WellnessText> Reasons, int LevelDelta, bool SwapForPain, int Tdee, double CalorieAdjustment,
    WellnessExerciseDay Exercise, WellnessDailyLog Log, bool Past, bool Today, double EndWeightKg,
    double? WaistCm, int? Points, int? Streak)
{
    internal bool HasRecordedLog { get; init; }
    public bool LighterDinner { get; init; }
}
