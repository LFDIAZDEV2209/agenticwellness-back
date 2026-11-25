using System.Globalization;
using System.Text.Json;

namespace CoppAddresd.Domain.Wellness365;

internal static class WellnessRules
{
    internal const double AdjustmentCap = .12;

    internal static double ActivityMultiplier(int level) => level switch
    {
        1 => 1.2, 2 => 1.375, 3 => 1.55, 4 => 1.725,
        _ => throw new ArgumentOutOfRangeException(nameof(level))
    };

    internal static double TargetShare(WellnessProfile profile)
    {
        var goal = profile.Parameters.Pct.For(profile.Goal);
        return profile.Pace switch
        {
            WellnessPace.Slow => goal.Slow,
            WellnessPace.Moderate => goal.Moderate,
            WellnessPace.Maximum => goal.Maximum,
            _ => throw new ArgumentOutOfRangeException(nameof(profile))
        };
    }

    internal static WellnessMode Mode(WellnessGoal goal, int week, bool reached, WellnessParameters parameters) =>
        reached ? WellnessMode.Maintain : (goal switch
        {
            WellnessGoal.LoseWeight => week % parameters.BreakEvery.Lose == 0,
            WellnessGoal.GainMuscle => week % parameters.BreakEvery.Gain == 0,
            WellnessGoal.Tone => (week - 1) % parameters.BreakEvery.Tone >= parameters.DefCut,
            _ => throw new ArgumentOutOfRangeException(nameof(goal))
        }) ? WellnessMode.Break : WellnessMode.Main;

    internal static WellnessText ModeName(WellnessMode mode) => mode switch
    {
        WellnessMode.Main => new("Principal", "Main"),
        WellnessMode.Break => new("Pausa / mantenimiento corto", "Break / short maintenance"),
        WellnessMode.Maintain => new("Mantener", "Maintain"),
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    internal static double Round(double value)
    {
        var floor = Math.Floor(value);
        return value - floor >= .5 ? floor + 1 : floor;
    }

    internal static double RoundTo(double value, double unit) => Round(value / unit) * unit;

    // toFixed usa el valor binario original y resuelve los empates alejándose de cero.
    // El residuo FMA distingue un empate real de un producto redondeado a .5.
    internal static double Fixed(double value, int digits)
    {
        var factor = digits == 1 ? 10 : 100;
        var magnitude = Math.Abs(value);
        var scaled = magnitude * factor;
        var half = Math.Floor(scaled) + .5;
        var roundUp = scaled > half ||
                      (scaled == half && Math.FusedMultiplyAdd(magnitude, factor, -half) >= 0);
        return Math.CopySign((Math.Floor(scaled) + (roundUp ? 1 : 0)) / factor, value);
    }

    internal static string FixedText(double value, int digits) =>
        Fixed(value, digits).ToString(digits == 1 ? "F1" : "F2", CultureInfo.InvariantCulture);

    internal static T ReadReference<T>(string file)
    {
        using var stream = typeof(WellnessRules).Assembly.GetManifestResourceStream(
            $"CoppAddresd.Domain.Wellness365.Reference.{file}")
            ?? throw new InvalidOperationException($"No se encontró el recurso Wellness365 {file}.");
        return JsonSerializer.Deserialize<T>(stream, ReferenceJson)
            ?? throw new InvalidOperationException($"El recurso Wellness365 {file} está vacío.");
    }

    private static readonly JsonSerializerOptions ReferenceJson = new(JsonSerializerDefaults.Web);
}

internal struct WellnessRandom(uint seed)
{
    private uint state = seed;

    internal double Next()
    {
        state = unchecked(state * 1664525u + 1013904223u);
        return state / 4294967296d;
    }
}
