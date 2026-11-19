using System.Globalization;

namespace CoppAddresd.Application.Features.HealthTests;

public static class InitialBatteryMeasurementMapping
{
    public const string InstrumentCode = "historia-clinica";

    private static readonly Dictionary<string, (string Metric, string Unit)> Metrics = new(StringComparer.Ordinal)
    {
        ["peso"] = ("weight", "kg"),
        ["talla"] = ("height", "cm"),
    };

    public static IReadOnlyList<string> QuestionCodes { get; } = Array.AsReadOnly(Metrics.Keys.ToArray());

    public static (string Metric, string Unit, decimal Value)? Map(
        string code, string? questionUnit, decimal? min, decimal? max, string answer)
    {
        if (!Metrics.TryGetValue(code, out var target) || questionUnit != target.Unit)
            return null;

        // Acepta coma decimal, sin interpretar separadores de miles ni exponentes.
        if (!decimal.TryParse(answer.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            return null;

        var storedValue = Math.Round(value, 2, MidpointRounding.AwayFromZero);
        return storedValue is > 0 and < 10_000_000_000m && !(storedValue < min || storedValue > max)
            ? (target.Metric, target.Unit, storedValue)
            : null;
    }
}
