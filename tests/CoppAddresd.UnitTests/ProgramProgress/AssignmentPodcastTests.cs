using System.Text.Json;
using System.Text.Json.Nodes;
using CoppAddresd.Application.Features.ProgramProgress.Assignment;
using CoppAddresd.Domain.Entities.ProgramProgress;

namespace CoppAddresd.UnitTests.ProgramProgress;

public sealed class AssignmentPodcastTests
{
    private static readonly AssignmentContent First = new(Guid.NewGuid(), "Episodio base");
    private static readonly AssignmentContent Alternative = new(Guid.NewGuid(), "Episodio adaptado");
    private static readonly AssignmentProfileData Profile = new(Guid.NewGuid(), Guid.NewGuid(), null, null, [], []);
    private static readonly DateTime Captured = new(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc);
    private const string Override = """{"podcastReason":"Adaptación justificada al perfil medido","podcastOverrides":[{"weekNumber":1,"weekday":1,"mediaTitle":"Episodio adaptado"}]}""";

    private static AssignmentCatalog Catalog(params ProgramAssignmentRule[] rules) => new([], rules, [], [])
    {
        // La genérica no sustituye la específica; se conservan repeticiones y huecos.
        TemplatePodcasts = [new(null, 1, First.Id), new(1, 1, First.Id), new(2, 1, First.Id), new(12, 6, null)],
        Podcasts = [Alternative, First],
    };

    private static JsonNode Build(AssignmentCatalog catalog) => JsonNode.Parse(
        new AssignmentEngine().Build(Profile, catalog, new(), Captured).Proposal)!;

    private static ProgramAssignmentRule Rule(string target = Override) => new()
    {
        Id = Guid.NewGuid(), Code = "perfil-podcast", Criteria = "{}", Target = target,
    };

    private static void AssertTemplate(AssignmentCatalog catalog, JsonNode proposal)
    {
        var expected = JsonSerializer.SerializeToNode(catalog.TemplatePodcasts, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(JsonNode.DeepEquals(expected, proposal["podcastSequence"]));
        Assert.True(proposal["useTemplatePodcastDefault"]!.GetValue<bool>());
        Assert.DoesNotContain(proposal["matchedRules"]!.AsArray(), r => r!["appliesPodcasts"]!.GetValue<bool>());
    }

    [Fact]
    public void SinRegla_ConservaSecuenciaCompletaYBaseTrazableSinElegirDelCatalogo()
    {
        var catalog = Catalog();
        var decision = new AssignmentEngine().Build(Profile, catalog, new(), Captured);
        var proposal = JsonNode.Parse(decision.Proposal)!;
        AssertTemplate(catalog, proposal);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(decision.ProfileSnapshot)!["templatePodcastSequence"], proposal["podcastSequence"]));
        Assert.Empty(proposal["matchedRules"]!.AsArray());
    }

    [Fact]
    public void ReglaSoloNutricion_NoCambiaLosPodcasts()
    {
        var plan = new AssignmentContent(Guid.NewGuid(), "Plan medido");
        var catalog = Catalog(Rule("""{"nutritionPlanName":"Plan medido"}""")) with { NutritionPlans = [plan] };
        var proposal = Build(catalog);
        AssertTemplate(catalog, proposal);
        Assert.Equal(plan.Id, proposal["nutritionPlan"]!["id"]!.GetValue<Guid>());
    }

    [Fact]
    public void ReglaAplicable_OverrideSoloEnSuCeldaConRazonYAntesDespues()
    {
        var rule = Rule();
        var catalog = Catalog(rule);
        var proposal = Build(catalog);
        var sequence = proposal["podcastSequence"]!.AsArray();
        Assert.Equal(4, sequence.Count);
        Assert.Equal(new Guid?[] { First.Id, Alternative.Id, First.Id, null },
            sequence.Select(p => p!["mediaId"]?.GetValue<Guid>()));
        Assert.False(proposal["useTemplatePodcastDefault"]!.GetValue<bool>());
        var match = Assert.Single(proposal["matchedRules"]!.AsArray())!;
        Assert.Equal(rule.Id, match["ruleId"]!.GetValue<Guid>());
        Assert.Equal(rule.Code, match["ruleCode"]!.GetValue<string>());
        Assert.Equal("Adaptación justificada al perfil medido", match["reason"]!.GetValue<string>());
        Assert.True(match["appliesPodcasts"]!.GetValue<bool>());
        var change = Assert.Single(match["podcastChanges"]!.AsArray())!;
        Assert.Equal(1, change["weekNumber"]!.GetValue<int>());
        Assert.Equal(First.Id, change["previousMediaId"]!.GetValue<Guid>());
        Assert.Equal(Alternative.Id, change["mediaId"]!.GetValue<Guid>());
        Assert.Equal(First.Id, catalog.TemplatePodcasts[1].MediaId);
    }

    [Theory]
    [InlineData(false, "{}")]
    [InlineData(true, "{\"field\":\"bmi\",\"op\":\"gte\",\"value\":30}")]
    public void ReglaInactivaOSinPerfilCoincidente_NoModificaLaPlantilla(bool active, string criteria)
    {
        var rule = Rule(); rule.IsActive = active; rule.Criteria = criteria;
        var catalog = Catalog(rule);
        AssertTemplate(catalog, Build(catalog));
    }

    [Theory]
    [InlineData("{\"podcastOverrides\":[{\"weekNumber\":1,\"weekday\":1,\"mediaTitle\":\"Episodio adaptado\"}]}")]
    [InlineData("{\"podcastReason\":\"  \",\"podcastOverrides\":[{\"weekNumber\":1,\"weekday\":1,\"mediaTitle\":\"Episodio adaptado\"}]}")]
    [InlineData("{\"podcastReason\":42,\"podcastOverrides\":[]}")]
    [InlineData("{\"podcastReason\":\"Perfil\",\"podcastOverrides\":[{\"weekNumber\":1,\"weekday\":1,\"mediaTitle\":\"No publicado\"}]}")]
    [InlineData("{\"podcastReason\":\"Perfil\",\"podcastOverrides\":[{\"weekNumber\":3,\"weekday\":1,\"mediaTitle\":\"Episodio adaptado\"}]}")]
    [InlineData("{\"podcastReason\":\"Perfil\",\"podcastOverrides\":[{\"weekday\":1,\"mediaTitle\":\"Episodio adaptado\"}]}")]
    [InlineData("{\"podcastReason\":\"Perfil\",\"podcastOverrides\":[{\"weekNumber\":1,\"weekday\":1,\"mediaTitle\":\"Episodio adaptado\"},{\"weekNumber\":2,\"weekday\":1,\"mediaTitle\":\"No publicado\"}]}")]
    public void OverrideSinJustificacionODestinosValidos_ConservaTodaLaSecuencia(string target)
    {
        var catalog = Catalog(Rule(target));
        AssertTemplate(catalog, Build(catalog));
    }

    [Fact]
    public void ReglaPrioritariaQueConservaBase_NoPermiteOverrideDeMenorPrioridad()
    {
        var preserve = Rule(Override.Replace("Episodio adaptado", "Episodio base"));
        preserve.Priority = 10;
        var catalog = Catalog(Rule(), preserve);
        AssertTemplate(catalog, Build(catalog));
    }

    [Fact]
    public void ReglasSolapadas_RespetanPrioridadPesoYCodigoSinDependerDelOrden()
    {
        var winner = Rule(); winner.Priority = 10; winner.Weight = 2; winner.Code = "a";
        var loser = Rule(Override.Replace("Episodio adaptado", "Otro episodio")); loser.Priority = 10; loser.Weight = 2; loser.Code = "z";
        var lowerWeight = Rule(loser.Target); lowerWeight.Priority = 10; lowerWeight.Weight = 1; lowerWeight.Code = "0";
        var lowerPriority = Rule(loser.Target); lowerPriority.Priority = 9; lowerPriority.Weight = 100;
        var catalog = Catalog(loser, lowerWeight, lowerPriority, winner) with
        { Podcasts = [First, Alternative, new(Guid.NewGuid(), "Otro episodio")] };
        var proposal = Build(catalog);
        Assert.Equal(proposal.ToJsonString(), Build(catalog with
        { Rules = catalog.Rules.Reverse().ToArray(), TemplatePodcasts = catalog.TemplatePodcasts.Reverse().ToArray(), Podcasts = catalog.Podcasts.Reverse().ToArray() }).ToJsonString());
        Assert.Equal(Alternative.Id, proposal["podcastSequence"]![1]!["mediaId"]!.GetValue<Guid>());
        Assert.Single(proposal["matchedRules"]!.AsArray(), r => r!["appliesPodcasts"]!.GetValue<bool>());
    }
}
