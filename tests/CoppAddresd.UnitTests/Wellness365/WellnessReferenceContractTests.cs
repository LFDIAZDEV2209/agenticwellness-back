using System.Security.Cryptography;
using System.Text.Json;
using CoppAddresd.Domain.Wellness365;

namespace CoppAddresd.UnitTests.Wellness365;

public sealed class WellnessReferenceContractTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("catalog.json", "0061df83ed3f6a2804a594679fc26ced07a561657ed1cd0f45fa154ade241f64")]
    [InlineData("guide.json", "e3a6cb94655cda96e745c9c09f7fb167081243d5ab9a82698807045149a357f5")]
    public void ReferenceFiles_AreEmbeddedUnchangedFromV52(string file, string hash)
    {
        using var stream = typeof(WellnessEngine).Assembly.GetManifestResourceStream(
            $"CoppAddresd.Domain.Wellness365.Reference.{file}");
        Assert.True(stream is not null, $"No se encontró el recurso {file}.");
        Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(stream!)));
    }

    [Fact]
    public void Guides_LoadBothLanguagesForEveryCatalogExercise()
    {
        var guides = WellnessExercise.Guides;
        Assert.Equal(26, guides.Count);
        Assert.Equal(WellnessExercise.Catalog.Keys.Order(StringComparer.Ordinal), guides.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("Caminata", guides["walk"].Es.Name);
        Assert.Equal("Walking", guides["walk"].En.Name);

        foreach (var (id, guide) in guides)
        foreach (var instructions in new[] { guide.Es, guide.En })
        {
            Assert.True(!string.IsNullOrWhiteSpace(instructions.Name), id);
            Assert.NotEmpty(instructions.Steps);
            Assert.All(instructions.Steps, step => Assert.False(string.IsNullOrWhiteSpace(step), id));
            Assert.False(string.IsNullOrWhiteSpace(instructions.Breathing), id);
            Assert.False(string.IsNullOrWhiteSpace(instructions.CommonMistakes), id);
            Assert.False(string.IsNullOrWhiteSpace(instructions.Easier), id);
            Assert.False(string.IsNullOrWhiteSpace(instructions.Harder), id);
        }
    }

    [Fact]
    public void Plans_IncludeEngineAndParameterVersions()
    {
        var profile = WellnessEngineTests.Profile("laura");
        var simulation = WellnessEngine.Simulate(profile);

        AssertVersion(JsonSerializer.SerializeToElement(simulation, WebJson), "plan anual");
        AssertVersion(JsonSerializer.SerializeToElement(WellnessEngine.PlanDay(profile, 1), WebJson), "día por índice");
        AssertVersion(JsonSerializer.SerializeToElement(WellnessEngine.PlanDay(profile, simulation.Days[0]), WebJson),
            "día por recomendación");
    }

    [Fact]
    public void Events_IncludeEngineAndParameterVersions()
    {
        var events = WellnessEngine.Simulate(WellnessEngineTests.Profile("laura")).Events;
        Assert.Equal(new[] { "ajuste", "meta", "modo", "sin" },
            events.Select(e => e.Kind).Distinct().Order(StringComparer.Ordinal));
        foreach (var entry in events)
            AssertVersion(JsonSerializer.SerializeToElement(entry, WebJson), $"evento {entry.Kind}, día {entry.Day}");
    }

    private static void AssertVersion(JsonElement value, string context)
    {
        Assert.True(value.TryGetProperty("engine_ver", out var engine), $"{context}: falta engine_ver.");
        Assert.Equal("5.2", engine.GetString());
        Assert.True(value.TryGetProperty("params_version", out var parameters), $"{context}: falta params_version.");
        Assert.Equal(1, parameters.GetInt32());
    }
}
