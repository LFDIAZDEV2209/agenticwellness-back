using CoppAddresd.Application.Features.ProgramProgress.PatientSummary;
using CoppAddresd.Application.Features.ProgramProgress.WeeklySuggestions;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services;
using CoppAddresd.Auth.Seeders;
using CoppAddresd.Domain.Exceptions;
using NSubstitute;
using System.Net;
using System.Net.Http.Json;
using CoppAddresd.Application.Common;
using CoppAddresd.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace CoppAddresd.UnitTests.ProgramProgress;

public sealed class WeeklySuggestionsTests
{
    internal static WeeklySuggestionsInput Input() => new(new(2026, 11, 16), new(2026, 11, 22), "es", [new("exercise.recent", "exercise", "Una tarea registrada.")],
        [new("routine.d1", new(2026, 11, 16), "routine", "Actual", [new("r1", "routine", "Alternativa"), new("rest", "rest", "Descanso")]),
         new("podcast.d1", new(2026, 11, 16), "podcast", null, [new("p1", "podcast", "Otro podcast")])]) { SubjectPatientId = Guid.NewGuid() };
    internal static WeeklySuggestionsResponse Response() => new("ok", "weekly_plan_suggestions", true,
        [new("routine.d1", "r1", "Valorar la alternativa [exercise.recent].", ["exercise.recent"])], null, new(2026, 11, 15, 23, 0, 0, TimeSpan.Zero));

    [Theory]
    [InlineData("unknown-slot")]
    [InlineData("cross-option")]
    [InlineData("duplicate-slot")]
    [InlineData("unknown-source")]
    [InlineData("inline-source")]
    [InlineData("html")]
    [InlineData("url")]
    [InlineData("not-estimate")]
    [InlineData("wrong-kind")]
    [InlineData("non-utc")]
    [InlineData("long-model")]
    public void Contrato_RechazaAlternativasYFuentesInventadas(string problem)
    {
        var response = Response(); var item = response.Suggestions[0];
        response = problem switch
        {
            "unknown-slot" => response with { Suggestions = [item with { SlotRef = "invented" }] },
            "cross-option" => response with { Suggestions = [item with { OptionRef = "p1" }] },
            "duplicate-slot" => response with { Suggestions = [item, item] },
            "unknown-source" => response with { Suggestions = [item with { SourceRefs = ["invented"] }] },
            "inline-source" => response with { Suggestions = [item with { Reason = "Texto [invented]" }] },
            "html" => response with { Suggestions = [item with { Reason = "<b>Texto</b>" }] },
            "url" => response with { Suggestions = [item with { Reason = "https://evil.invalid" }] },
            "not-estimate" => response with { IsEstimate = false },
            "wrong-kind" => response with { Kind = "patient_summary" },
            "non-utc" => response with { GeneratedAt = response.GeneratedAt.ToOffset(TimeSpan.FromHours(1)) },
            _ => response with { Model = new string('x', 121) },
        };
        Assert.False(WeeklySuggestionsContract.ValidResponse(response, Input()));
    }
    [Fact]
    public void Contrato_VacioValido_Insuficiente_NullNoEsCero_YCeldasAcotadas()
    {
        var input = Input(); Assert.True(WeeklySuggestionsContract.ValidInput(input));
        Assert.True(WeeklySuggestionsContract.ValidResponse(Response() with { Suggestions = [] }, input));
        Assert.True(WeeklySuggestionsContract.ValidResponse(Response() with { Status = "insufficient", Suggestions = [] }, input));
        Assert.False(WeeklySuggestionsContract.ValidInput(input with { Slots = [input.Slots[0], input.Slots[0]] }));
        Assert.False(WeeklySuggestionsContract.ValidInput(input with { Slots = [input.Slots[0] with { Date = new(2026, 11, 23) }] }));
        Assert.False(WeeklySuggestionsContract.ValidInput(input with { Slots = [input.Slots[1] with { Options = [new("rest", "rest", "Descanso")] }] }));
        var wire = System.Text.Encoding.UTF8.GetString(WeeklySuggestionsJson.Bytes(input));
        Assert.DoesNotContain(input.SubjectPatientId.ToString(), wire); Assert.Contains("\"current\":null", wire);
        Assert.NotEqual(WeeklySuggestionsJson.Hash(input), WeeklySuggestionsJson.Hash(input with { Language = "en" }));
    }

    [Theory]
    [InlineData("Admin", true, true)]
    [InlineData("ClinicalDirector", true, true)]
    [InlineData("NutritionReviewer", true, false)]
    [InlineData("CareCoordinator", true, false)]
    [InlineData("Admin", false, true)]
    [InlineData("ClinicalDirector", false, true)]
    [InlineData("NutritionReviewer", false, true)]
    [InlineData("CareCoordinator", false, false)]
    public async Task Matriz10_1_ListarYAplicarExigenCapacidadesDistintas(string role, bool apply, bool allowed)
    {
        var patient = Guid.NewGuid(); var enrollment = Guid.NewGuid(); var actor = Substitute.For<IPatientScopeActor>();
        actor.UserId.Returns(Guid.NewGuid()); actor.IsAdmin.Returns(role == "Admin"); actor.ProfessionalIdAsync(Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
        actor.HasPermissionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => WellbeingRolePermissions.ForRole(role).Contains(call.Arg<string>()));
        var access = Substitute.For<IPatientScopeStore>(); access.OwnerAsync(PatientResource.Enrollment, enrollment, Arg.Any<CancellationToken>()).Returns(patient);
        access.VisiblePatients(Arg.Any<Guid?>(), Arg.Any<bool>()).Returns(new[] { patient }.AsQueryable());
        access.ContainsAsync(Arg.Any<IQueryable<Guid>>(), patient, Arg.Any<CancellationToken>()).Returns(true);
        var scope = new PatientScopeGuard(actor, access); var consent = Substitute.For<ILegalConsentService>(); var store = Substitute.For<IWeeklySuggestionsStore>();
        var id = Guid.NewGuid(); store.ApplyAsync(patient, enrollment, 2, id, 0, actor.UserId!.Value, Arg.Any<CancellationToken>())
            .Returns(new ApplyWeeklySuggestionResult("applied", id, 0, default!));
        async Task Run()
        {
            if (apply) Assert.Equal("applied", (await new ApplyWeeklySuggestionHandler(scope, actor, consent, store).Handle(new(enrollment, 2, id, 0, true), default)).Status);
            else Assert.Equal("empty", (await new GetWeeklySuggestionsHandler(scope, consent, store).Handle(new(enrollment, 2), default)).Status);
        }
        if (allowed) await Run(); else { await Assert.ThrowsAsync<ForbiddenException>(Run); await store.DidNotReceiveWithAnyArgs().ApplyAsync(default, default, default, default, default, default, default); }
    }
    [Fact]
    public async Task SinConfirmacionHumana_NoLlamaAlEditor()
    {
        var scope = Substitute.For<IPatientScopeGuard>(); scope.EnsureResourceAsync(Arg.Any<PatientResource>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
        var store = Substitute.For<IWeeklySuggestionsStore>(); var actor = Substitute.For<IPatientScopeActor>(); actor.UserId.Returns(Guid.NewGuid());
        await Assert.ThrowsAsync<UnprocessableEntityException>(() => new ApplyWeeklySuggestionHandler(scope, actor, Substitute.For<ILegalConsentService>(), store)
            .Handle(new(Guid.NewGuid(), 2, Guid.NewGuid(), 0, false), default));
        await store.DidNotReceiveWithAnyArgs().ApplyAsync(default, default, default, default, default, default, default);
    }

    private sealed class Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => reply(request, ct);
    }
    private sealed class Capture : DelegatingHandler
    {
        public List<byte[]> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsByteArrayAsync(ct));
            Assert.True(request.Options.TryGetValue(AiConsentSubject.Key, out var subject));
            Assert.True(subject.RequireRecipientConsent); Assert.NotNull(subject.PatientId);
            return await base.SendAsync(request, ct);
        }
    }
    [Fact]
    public async Task WeeklyCliente_BytesConsentimiento_OkInsuficiente503_SimulacionOpcional()
    {
        var simulation = Environment.GetEnvironmentVariable("COP_WEEKLY_SUGGESTIONS_SIM_URL");
        var capture = new Capture { InnerHandler = string.IsNullOrEmpty(simulation) ? new Transport(async (request, ct) =>
        {
            if (request.Headers.Contains("X-Mock-Response-Code")) return new(HttpStatusCode.ServiceUnavailable);
            var input = System.Text.Json.JsonSerializer.Deserialize<WeeklySuggestionsInput>(await request.Content!.ReadAsStringAsync(ct), PatientSummaryJson.Options)!;
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(input.Facts.Count == 0
                ? Response() with { Status = "insufficient", Suggestions = [] } : Response()) };
        }) : new HttpClientHandler() };
        using var http = new HttpClient(capture); http.DefaultRequestHeaders.Add("X-Mock-Slug", "weekly");
        var client = new WeeklySuggestionsClient(http, Options.Create(new AiServiceSettings
            { BaseUrl = simulation ?? "http://ai.invalid", InternalApiKey = "simulation-only-key", TimeoutSeconds = 2 }));
        var input = Input(); Assert.Equal("ok", (await client.GenerateAsync(input, default)).Status);
        Assert.Equal(WeeklySuggestionsJson.Bytes(input), capture.Bodies[0]);
        Assert.Equal("insufficient", (await client.GenerateAsync(input with { Facts = [] }, default)).Status);
        http.DefaultRequestHeaders.Add("X-Mock-Response-Code", "503");
        await Assert.ThrowsAsync<WeeklySuggestionsUnavailableException>(() => client.GenerateAsync(input, default)); Assert.Equal(3, capture.Bodies.Count);
    }
    [Theory]
    [InlineData("extra")]
    [InlineData("large")]
    [InlineData("503")]
    public async Task WeeklyCliente_ErrorControladoSinContenidoDelProveedor(string kind)
    {
        using var transport = new Transport((_, _) => Task.FromResult(kind == "503" ? new(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = kind == "large" ? new StringContent(new string('x', 65537))
                : new StringContent("{\"status\":\"ok\",\"providerSecret\":\"synthetic-private\"}", System.Text.Encoding.UTF8, "application/json") }));
        using var http = new HttpClient(transport);
        var client = new WeeklySuggestionsClient(http, Options.Create(new AiServiceSettings { BaseUrl = "http://ai.invalid", InternalApiKey = "synthetic" }));
        var error = await Assert.ThrowsAsync<WeeklySuggestionsUnavailableException>(() => client.GenerateAsync(Input(), default));
        Assert.Null(error.InnerException); Assert.DoesNotContain("synthetic-private", error.Message);
    }
}
