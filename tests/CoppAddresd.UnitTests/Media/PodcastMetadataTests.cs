using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CoppAddresd.Application.Common;
using CoppAddresd.Application.Features.Media.PodcastMetadata;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Auth.Seeders;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CoppAddresd.UnitTests.Media;

public sealed class PodcastMetadataTests
{
    internal static PodcastMetadataResponse Ready() => new("ok", true, new("Descripción editorial.", "Habitos", [" Hábitos ", "constancia", "hábitos"]),
        "synthetic-model", new(2026, 11, 15, 23, 0, 0, TimeSpan.Zero));
    private sealed class Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => reply(request, ct);
    }
    private static PodcastMetadataClient Client(HttpClient http, int timeout = 2, string key = "simulation-only-key") => new(http,
        Options.Create(new AiServiceSettings { BaseUrl = Environment.GetEnvironmentVariable("COP_PODCAST_METADATA_SIM_URL") ?? "http://ai.invalid", InternalApiKey = key, TimeoutSeconds = timeout }));

    [Theory]
    [InlineData("empty-title")]
    [InlineData("long-title")]
    [InlineData("long-context")]
    [InlineData("null-context")]
    [InlineData("language")]
    [InlineData("html")]
    [InlineData("url")]
    public void Entrada_LimitesYTextoEditorialSinUrlNiHtml(string kind)
    {
        var input = new PodcastMetadataInput("Título");
        input = kind switch
        {
            "empty-title" => input with { Title = " " }, "long-title" => input with { Title = new('t', 201) },
            "long-context" => input with { Context = new('c', 16001) }, "null-context" => input with { Context = null! },
            "language" => input with { Language = "fr" }, "html" => input with { Title = "<b>Título</b>" },
            _ => input with { Context = "https://private.invalid?X-Amz-Signature=synthetic" },
        };
        Assert.False(new SuggestPodcastMetadataCommandValidator().Validate(new SuggestPodcastMetadataCommand(input)).IsValid);
    }
    [Fact]
    public void Entrada_BordesCompatiblesConContratoYPeticionMinima()
    {
        var validator = new SuggestPodcastMetadataCommandValidator();
        Assert.True(validator.Validate(new SuggestPodcastMetadataCommand(new("Tema"))).IsValid);
        Assert.True(validator.Validate(new SuggestPodcastMetadataCommand(new(new('t', 200), "en", new('c', 16000)))).IsValid);
        Assert.False(validator.Validate(new SuggestPodcastMetadataCommand(null!)).IsValid);
    }
    [Theory]
    [InlineData("1")]
    [InlineData("Hábitos")]
    [InlineData("Unknown")]
    public void Salida_CategoriaDebeSerCodigoExistenteExacto(string category)
        => Assert.False(PodcastMetadataContract.ValidResponse(Ready() with { Draft = Ready().Draft with { Category = category } }));
    [Theory]
    [InlineData("no-estimate")]
    [InlineData("status")]
    [InlineData("html")]
    [InlineData("url-tag")]
    [InlineData("long-description")]
    [InlineData("empty-description")]
    [InlineData("many-tags")]
    [InlineData("long-tag")]
    [InlineData("empty-tag")]
    [InlineData("null-tags")]
    [InlineData("non-utc")]
    [InlineData("long-model")]
    public void Salida_InvalidaNoSePresentaComoEstimacion(string kind)
    {
        var response = Ready();
        response = kind switch
        {
            "no-estimate" => response with { IsEstimate = false }, "status" => response with { Status = "published" },
            "html" => response with { Draft = response.Draft with { Description = "<b>Texto</b>" } },
            "url-tag" => response with { Draft = response.Draft with { Tags = ["https://evil.invalid"] } },
            "long-description" => response with { Draft = response.Draft with { Description = new('x', 2001) } },
            "empty-description" => response with { Draft = response.Draft with { Description = " " } },
            "many-tags" => response with { Draft = response.Draft with { Tags = Enumerable.Repeat("x", 33).ToArray() } },
            "long-tag" => response with { Draft = response.Draft with { Tags = [new('x', 65)] } },
            "empty-tag" => response with { Draft = response.Draft with { Tags = [" "] } },
            "null-tags" => response with { Draft = response.Draft with { Tags = null! } },
            "non-utc" => response with { GeneratedAt = response.GeneratedAt.ToOffset(TimeSpan.FromHours(1)) },
            _ => response with { Model = new('m', 201) },
        };
        Assert.False(PodcastMetadataContract.ValidResponse(response));
    }
    [Fact]
    public void Salida_ModeloNullYCategoriasDelCatalogoSonValidos()
    {
        foreach (var category in Enum.GetNames<CoppAddresd.Domain.Enums.MediaCategory>())
            Assert.True(PodcastMetadataContract.ValidResponse(Ready() with { Model = null, Draft = Ready().Draft with { Category = category, Tags = [] } }));
        Assert.True(PodcastMetadataContract.ValidResponse(Ready() with { Model = new('m', 200) }));
    }
    [Theory]
    [InlineData("Admin", true)]
    [InlineData("ClinicalDirector", true)]
    [InlineData("NutritionReviewer", false)]
    [InlineData("CareCoordinator", false)]
    public async Task Matriz10_1_SoloQuienCreaBibliotecaYGestionaIA(string role, bool allowed)
    {
        var actor = Substitute.For<IPatientScopeActor>(); actor.UserId.Returns(Guid.NewGuid()); actor.IsAdmin.Returns(role == "Admin");
        actor.HasPermissionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => WellbeingRolePermissions.ForRole(role).Contains(call.Arg<string>()));
        var client = Substitute.For<IPodcastMetadataClient>(); client.SuggestAsync(Arg.Any<PodcastMetadataInput>(), Arg.Any<CancellationToken>()).Returns(Ready());
        var handler = new SuggestPodcastMetadataCommandHandler(actor, client, NullLogger<SuggestPodcastMetadataCommandHandler>.Instance);
        if (allowed) Assert.Equal("ok", (await handler.Handle(new(new("Título")), default)).Status);
        else
        {
            await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(new(new("Título")), default));
            await client.DidNotReceiveWithAnyArgs().SuggestAsync(default!, default);
        }
        await actor.DidNotReceiveWithAnyArgs().ProfessionalIdAsync(default); // Sin datos ni alcance de pacientes en catálogo.
    }
    [Fact]
    public async Task Handler_RevalidaPermisoTrasIA_YErrorNoConservaUnBorradorFalso()
    {
        var actor = Substitute.For<IPatientScopeActor>(); actor.UserId.Returns(Guid.NewGuid()); actor.IsAdmin.Returns(true);
        actor.HasPermissionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var client = Substitute.For<IPodcastMetadataClient>();
        client.SuggestAsync(Arg.Any<PodcastMetadataInput>(), Arg.Any<CancellationToken>()).Returns(_ =>
        { actor.HasPermissionAsync("Program.AiManage", Arg.Any<CancellationToken>()).Returns(false); return Ready(); });
        var handler = new SuggestPodcastMetadataCommandHandler(actor, client, NullLogger<SuggestPodcastMetadataCommandHandler>.Instance);
        await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(new(new("Título")), default));
        actor.HasPermissionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        client.SuggestAsync(Arg.Any<PodcastMetadataInput>(), Arg.Any<CancellationToken>()).Returns(Task.FromException<PodcastMetadataResponse>(new PodcastMetadataUnavailableException()));
        var unavailable = await handler.Handle(new(new("Título")), default);
        Assert.Equal("unavailable", unavailable.Status); Assert.Null(unavailable.Draft); Assert.Null(unavailable.Model); Assert.Null(unavailable.GeneratedAt);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.Handle(new(new("Título")), cancelled.Token));
    }
    [Fact]
    public async Task PodcastCliente_ContratoYNormalizacion_SimulacionOpcional()
    {
        var simulation = Environment.GetEnvironmentVariable("COP_PODCAST_METADATA_SIM_URL");
        var wires = new List<string>();
        using var transport = new Transport(async (request, ct) =>
        {
            Assert.Equal(PodcastMetadataClient.Endpoint, request.RequestUri!.AbsolutePath);
            Assert.Equal("simulation-only-key", request.Headers.GetValues("X-Internal-Key").Single());
            wires.Add(await request.Content!.ReadAsStringAsync(ct));
            return request.Headers.Contains("X-Mock-Response-Code") ? new(HttpStatusCode.ServiceUnavailable)
                : new(HttpStatusCode.OK) { Content = JsonContent.Create(Ready()) };
        });
        using var http = string.IsNullOrEmpty(simulation) ? new HttpClient(transport) : new HttpClient();
        http.DefaultRequestHeaders.Add("X-Mock-Slug", "podcast");
        var input = new PodcastMetadataInput("Crear hábitos sostenibles", "es", "Introducción a la constancia.");
        var response = await Client(http).SuggestAsync(input, default);
        Assert.Equal("ok", response.Status); Assert.Equal(response.Draft.Tags.Order().Distinct(), response.Draft.Tags);
        Assert.All(response.Draft.Tags, tag => Assert.Equal(tag.Trim().ToLowerInvariant(), tag));
        if (string.IsNullOrEmpty(simulation))
        {
            using var json = JsonDocument.Parse(Assert.Single(wires));
            Assert.Equal(new[] { "title", "language", "context" }, json.RootElement.EnumerateObject().Select(p => p.Name));
            Assert.Equal(new[] { "constancia", "hábitos" }, response.Draft.Tags);
        }
        Assert.Equal("ok", (await Client(http).SuggestAsync(new("Editorial", "en"), default)).Status);
        http.DefaultRequestHeaders.Add("X-Mock-Response-Code", "503");
        await Assert.ThrowsAsync<PodcastMetadataUnavailableException>(() => Client(http).SuggestAsync(input, default));
    }
    [Theory]
    [InlineData("extra")]
    [InlineData("nested-extra")]
    [InlineData("large")]
    [InlineData("malformed")]
    [InlineData("503")]
    public async Task Cliente_FronteraEstrictaSinFiltrarCuerpoDelProveedor(string kind)
    {
        var json = JsonSerializer.Serialize(Ready(), PodcastMetadataContract.Json);
        if (kind == "extra") json = json[..^1] + ",\"publish\":true}";
        if (kind == "nested-extra") json = json.Replace("\"category\":", "\"status\":\"Published\",\"category\":");
        if (kind == "large") json = new('x', 65537);
        if (kind == "malformed") json = "synthetic-private-provider-error";
        using var transport = new Transport((_, _) => Task.FromResult(new HttpResponseMessage(kind == "503" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") }));
        using var http = new HttpClient(transport);
        var error = await Assert.ThrowsAsync<PodcastMetadataUnavailableException>(() => Client(http).SuggestAsync(new("Tema"), default));
        Assert.Null(error.InnerException); Assert.DoesNotContain("synthetic-private", error.Message);
    }
    [Fact]
    public async Task Cliente_TimeoutYCancelacionReal_NoDevuelvenEstimacion()
    {
        using var transport = new Transport(async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return new(HttpStatusCode.OK); });
        using var http = new HttpClient(transport);
        await Assert.ThrowsAsync<PodcastMetadataUnavailableException>(() => Client(http, 1).SuggestAsync(new("Tema"), default));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(http).SuggestAsync(new("Tema"), cancellation.Token));
    }
}
