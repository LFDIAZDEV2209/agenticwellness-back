using System.Text.Json.Serialization;

namespace CoppAddresd.Domain.Wellness365;

/// <summary>Reglas configurables del kit, fijadas por revisión de plan.</summary>
public sealed record WellnessParameters
{
    public static WellnessParameters Default { get; } = new();
    public WellnessGoals<WellnessShares> Pct { get; init; } = new(new(.10, .15, .20), new(.05, .10, .15), new(.05, .10, .12));
    public WellnessGoals<double> Pro { get; init; } = new(1.6, 1.8, 2.0);
    public double ProCap { get; init; } = 2.2;
    public WellnessGoals<IReadOnlyList<double>> Band { get; init; } = new(
        Array.AsReadOnly(new[] { -.75, -.25 }), Array.AsReadOnly(new[] { .10, .35 }), Array.AsReadOnly(new[] { -.50, -.20 }));
    public WellnessFat Fat { get; init; } = new(.28, .24);
    [JsonPropertyName("floorF")] public int FloorFemale { get; init; } = 1200;
    [JsonPropertyName("floorM")] public int FloorMale { get; init; } = 1500;
    public int Step { get; init; } = 100;
    public double AdhMin { get; init; } = .70;
    public int WeighMin { get; init; } = 4;
    public WellnessGoals<int> BreakEvery { get; init; } = new(9, 13, 12);
    public int DefCut { get; init; } = 10;
    public int CarbMin { get; init; } = 80;
}

public sealed record WellnessGoals<T>(
    [property: JsonPropertyName("bajar")] T Lose,
    [property: JsonPropertyName("masa")] T Gain,
    [property: JsonPropertyName("def")] T Tone)
{
    public T For(WellnessGoal goal) => goal switch
    {
        WellnessGoal.LoseWeight => Lose,
        WellnessGoal.GainMuscle => Gain,
        WellnessGoal.Tone => Tone,
        _ => throw new ArgumentOutOfRangeException(nameof(goal))
    };
}

public sealed record WellnessShares(
    [property: JsonPropertyName("s")] double Slow,
    [property: JsonPropertyName("m")] double Moderate,
    [property: JsonPropertyName("max")] double Maximum);

public sealed record WellnessFat(double Rest, double Train);
