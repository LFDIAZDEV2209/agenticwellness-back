using System.Net;
using System.Net.Http.Json;
using System.Text;
using CoppAddresd.Application.Common;
using CoppAddresd.Application.Features.Nutrition.Commands;
using CoppAddresd.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace CoppAddresd.UnitTests.Nutrition;

public sealed class NutritionCycleAiClientTests
{
    private sealed class Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; return reply(request, ct); }
    }
    private static NutritionCycleSummaryInput Input() => new(1, new(2026, 11, 1), new(2026, 11, 30), "es",
        new(1, .0333m, 500, null, null, null, null, null), new(1, 0, 0, 0, 0), null, null, []);
    private static NutritionCycleSummaryClient Client(HttpClient http) => new(http, Options.Create(new AiServiceSettings
    { BaseUrl = "http://ai.invalid", InternalApiKey = "synthetic-key", TimeoutSeconds = 25 }));

    [Theory]
    [InlineData("{\"status\":\"invented\"}")]
    [InlineData("{\"status\":\"ok\",\"summary\":\"x\",\"patterns\":[],\"gaps\":[],\"draft\":{\"es\":\"x\",\"en\":\"x\"},\"model\":\"mock\",\"generatedAt\":\"2026-11-14T00:00:00Z\",\"patientId\":\"forbidden\"}")]
    [InlineData("{\"status\":\"insufficient\",\"summary\":\"\",\"patterns\":[],\"gaps\":[],\"draft\":{\"es\":\"text\",\"en\":\"\"},\"model\":\"\",\"generatedAt\":\"2026-11-14T00:00:00Z\"}")]
    [InlineData("{\"status\":\"ok\",\"summary\":\"x\",\"patterns\":[\"\"],\"gaps\":[],\"draft\":{\"es\":\"x\",\"en\":\"x\"},\"model\":\"mock\",\"generatedAt\":\"2026-11-14T01:00:00+01:00\"}")]
    public async Task ContratoInvalido_NoDevuelveTextoNiDetalleDelProveedor(string json)
    {
        using var transport = new Transport((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") }));
        using var http = new HttpClient(transport);
        var ex = await Assert.ThrowsAsync<NutritionCycleAiUnavailableException>(() => Client(http).GenerateAsync(Input(), default));
        Assert.Equal("ai_unavailable", ex.Code); Assert.Null(ex.InnerException); Assert.DoesNotContain(json, ex.Message);
    }

    [Fact]
    public async Task CuerpoDemasiadoGrande_NoSeAceptaComoBorrador()
    {
        using var transport = new Transport((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(new string('x', 65537), Encoding.UTF8, "application/json") }));
        using var http = new HttpClient(transport);
        Assert.Equal("ai_unavailable", (await Assert.ThrowsAsync<NutritionCycleAiUnavailableException>(() => Client(http).GenerateAsync(Input(), default))).Code);
    }

    [Fact]
    public async Task EntradaIncompatible_NoLlamaAIA_NiTruncaValoresReales()
    {
        using var transport = new Transport((_, _) => throw new InvalidOperationException("No debe llamar"));
        using var http = new HttpClient(transport);
        foreach (var input in new[] { Input() with { Measurements = new(-1) }, Input() with { Aggregates = new(1, 1.1m, 500, null, null, null, null, null) }, Input() with { EndDate = new(2027, 11, 2) } })
            Assert.Equal("ai_input_invalid", (await Assert.ThrowsAsync<NutritionCycleAiUnavailableException>(() => Client(http).GenerateAsync(input, default))).Code);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task PoliticaExistente_ComparteFallosEntreClientes_CircuitoAbiertoNoEnviaHttp()
    {
        var resilience = new ProgramNarrativeResilience(NullLogger<ProgramNarrativeResilience>.Instance);
        using var transport = new Transport((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var http = new HttpClient(new PolicyHttpMessageHandler(resilience.Policy) { InnerHandler = transport });
        Assert.Equal("ai_unavailable", (await Assert.ThrowsAsync<NutritionCycleAiUnavailableException>(() => Client(http).GenerateAsync(Input(), default))).Code);
        Assert.Equal(4, transport.Calls);
        Assert.Equal("ai_circuit_open", (await Assert.ThrowsAsync<NutritionCycleAiUnavailableException>(() => Client(http).GenerateAsync(Input(), default))).Code);
        Assert.Equal(5, transport.Calls);
        Assert.Equal("ai_circuit_open", (await Assert.ThrowsAsync<NutritionCycleAiUnavailableException>(() => Client(http).GenerateAsync(Input(), default))).Code);
        Assert.Equal(5, transport.Calls);
    }
}
