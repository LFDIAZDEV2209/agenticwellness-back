using CoppAddresd.Application.Features.HealthTests;

namespace CoppAddresd.UnitTests.Features.HealthTests;

public sealed class InitialBatteryMeasurementMappingTests
{
    [Theory]
    [InlineData("peso", "kg", 40, 220, " 75,255 ", "weight", "75.26")]
    [InlineData("talla", "cm", 140, 210, "140", "height", "140")]
    [InlineData("talla", "cm", 140, 210, "210", "height", "210")]
    public void Map_ValidAnswer_ReturnsCanonicalMeasurement(
        string code, string unit, int min, int max, string answer, string metric, string expected)
    {
        var mapped = Assert.IsType<(string Metric, string Unit, decimal Value)>(
            InitialBatteryMeasurementMapping.Map(code, unit, min, max, answer));
        Assert.Equal((metric, unit, decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture)), mapped);
    }

    [Theory]
    [InlineData("peso", "kg", "39.999")]
    [InlineData("peso", "kg", "220.001")]
    [InlineData("peso", "kg", "7.5e1")]
    [InlineData("peso", "kg", "75 kg")]
    [InlineData("peso", "kg", "1,000.00")]
    [InlineData("peso", "lb", "165")]
    [InlineData("cintura", "cm", "90")]
    public void Map_UnsupportedOrInvalidAnswer_OmitsMeasurement(string code, string unit, string answer)
        => Assert.Null(InitialBatteryMeasurementMapping.Map(code, unit, 40, 220, answer));
}
