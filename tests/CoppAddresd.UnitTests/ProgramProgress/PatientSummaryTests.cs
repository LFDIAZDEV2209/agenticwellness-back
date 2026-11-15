using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using CoppAddresd.Application.Common;
using CoppAddresd.Application.Features.ProgramProgress.PatientSummary;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services;
using CoppAddresd.Auth.Seeders;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Services;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CoppAddresd.UnitTests.ProgramProgress;

public sealed class PatientSummaryTests
{
    private static PatientSummaryResponse Ready() => new("ok", "patient_summary", true, "Programa registrado [program.current].",
        ["program.current"], [new("Revisar registros.", ["program.current"])], null, new(2026, 11, 15, 18, 0, 0, TimeSpan.Zero));
    private static PatientSummaryInput Input() => new(new(2026, 11, 9), new(2026, 11, 15), "es", [new("program.current", "program", "Programa activo.")])
        { SubjectPatientId = Guid.NewGuid() };

    [Theory]
    [InlineData("unknown-ref")]
    [InlineData("missing-citation")]
    [InlineData("extra-ref")]
    [InlineData("html")]
    [InlineData("url")]
    [InlineData("attention-unknown")]
    [InlineData("attention-inline")]
    [InlineData("not-estimate")]
    [InlineData("wrong-kind")]
    [InlineData("non-utc")]
    [InlineData("model-too-long")]
    public void Frontera_RechazaTextoOReferenciasNoConfiables(string problem)
    {
        var response = Ready();
        response = problem switch
        {
            "unknown-ref" => response with { Summary = "Texto [invented]", SourceRefs = ["invented"] },
            "missing-citation" => response with { Summary = "Texto sin fuente" },
            "extra-ref" => response with { SourceRefs = ["program.current", "extra"] },
            "html" => response with { Summary = "<b>Texto</b> [program.current]" },
            "url" => response with { Summary = "https://evil.invalid [program.current]" },
            "attention-unknown" => response with { Attention = [new("Texto", ["invented"])] },
            "attention-inline" => response with { Attention = [new("Texto [invented]", ["program.current"])] },
            "not-estimate" => response with { IsEstimate = false },
            "wrong-kind" => response with { Kind = "weekly_plan_suggestions" },
            "non-utc" => response with { GeneratedAt = response.GeneratedAt.ToOffset(TimeSpan.FromHours(1)) },
            _ => response with { Model = new string('m', 201) },
        };
        Assert.False(PatientSummaryContract.ValidResponse(response, ["program.current"]));
    }

    [Fact]
    public void Contrato_ModeloAusenteOLargo_InsuficienciaYHashExacto()
    {
        Assert.True(PatientSummaryContract.ValidResponse(Ready(), ["program.current"]));
        Assert.True(PatientSummaryContract.ValidResponse(Ready() with { Model = new string('m', 200) }, ["program.current"]));
        Assert.True(PatientSummaryContract.ValidResponse(Ready() with { Status = "insufficient", Summary = "", SourceRefs = [], Attention = [] }, []));
        var input = Input(); var bytes = PatientSummaryJson.Bytes(input);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), PatientSummaryJson.Hash(input));
        Assert.DoesNotContain(input.SubjectPatientId.ToString(), System.Text.Encoding.UTF8.GetString(bytes));
        Assert.False(PatientSummaryContract.ValidInput(input with { Language = "fr" }));
        Assert.False(PatientSummaryContract.ValidInput(input with { EndDate = input.EndDate.AddDays(1) }));
        Assert.False(PatientSummaryContract.ValidInput(input with { Facts = [input.Facts[0], input.Facts[0]] }));
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
    public async Task Matriz10_1_PermisoIA_LecturaYGeneracion(string role, bool generate, bool allowed)
    {
        var patient = Guid.NewGuid(); var actor = Substitute.For<IPatientScopeActor>();
        actor.UserId.Returns(Guid.NewGuid()); actor.IsAdmin.Returns(role == "Admin");
        actor.ProfessionalIdAsync(Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
        actor.HasPermissionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => WellbeingRolePermissions.ForRole(role).Contains(call.Arg<string>()));
        var access = Substitute.For<IPatientScopeStore>(); access.VisiblePatients(Arg.Any<Guid?>(), Arg.Any<bool>()).Returns(new[] { patient }.AsQueryable());
        access.ContainsAsync(Arg.Any<IQueryable<Guid>>(), patient, Arg.Any<CancellationToken>()).Returns(true);
        var scope = new PatientScopeGuard(actor, access); var consent = Substitute.For<ILegalConsentService>();
        var store = Substitute.For<IPatientSummaryStore>(); var client = Substitute.For<IPatientSummaryClient>();
        store.TimezoneAsync(patient, Arg.Any<CancellationToken>()).Returns("America/Bogota");
        store.ReadAsync(patient, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new PatientSummaryData("Active", 1, new(2026, 11, 1), 0, 0, 0, 0, 0, 0));
        client.GenerateAsync(Arg.Any<PatientSummaryInput>(), Arg.Any<CancellationToken>()).Returns(Ready());
        Task<PatientSummaryResult> Run() => generate
            ? new GeneratePatientSummaryHandler(scope, consent, store, client, TimeProvider.System).Handle(new(patient), default)
            : new GetPatientSummaryHandler(scope, consent, store).Handle(new(patient), default);
        if (allowed) Assert.Equal(generate ? "ok" : "empty", (await Run()).Status);
        else
        {
            await Assert.ThrowsAsync<ForbiddenException>(Run);
            await client.DidNotReceiveWithAnyArgs().GenerateAsync(default!, default);
            await store.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
        }
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
    public async Task Cliente_ContratoCompleto_BytesConsentimiento_InsuficienciaY503_ConSimulacionOpcional()
    {
        var simulation = Environment.GetEnvironmentVariable("COP_PATIENT_SUMMARY_SIM_URL");
        var capture = new Capture { InnerHandler = string.IsNullOrEmpty(simulation)
            ? new Transport(async (request, ct) =>
            {
                if (request.Headers.Contains("X-Mock-Response-Code")) return new(HttpStatusCode.ServiceUnavailable);
                var input = System.Text.Json.JsonSerializer.Deserialize<PatientSummaryInput>(await request.Content!.ReadAsStringAsync(ct), PatientSummaryJson.Options)!;
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(input.Facts.Count == 0
                    ? Ready() with { Status = "insufficient", Summary = "", SourceRefs = [], Attention = [] } : Ready()) };
            }) : new HttpClientHandler() };
        using var http = new HttpClient(capture); http.DefaultRequestHeaders.Add("X-Mock-Slug", "summary");
        var client = new PatientSummaryClient(http, Options.Create(new AiServiceSettings
            { BaseUrl = simulation ?? "http://ai.invalid", InternalApiKey = "simulation-only-key", TimeoutSeconds = 2 }));
        var input = Input();
        Assert.Equal("ok", (await client.GenerateAsync(input, default)).Status);
        Assert.Equal(PatientSummaryJson.Bytes(input), capture.Bodies[0]);
        Assert.Equal("insufficient", (await client.GenerateAsync(input with { Facts = [] }, default)).Status);
        http.DefaultRequestHeaders.Add("X-Mock-Response-Code", "503");
        await Assert.ThrowsAsync<PatientSummaryUnavailableException>(() => client.GenerateAsync(input, default));
        Assert.Equal(3, capture.Bodies.Count);
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("large")]
    [InlineData("503")]
    public async Task Cliente_RespuestaInvalidaFallaSinDetallesDelProveedor(string kind)
    {
        using var transport = new Transport((_, _) => Task.FromResult(kind == "503" ? new(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = kind == "large" ? new StringContent(new string('x', 65537))
                : new StringContent("{\"status\":\"ok\",\"providerSecret\":\"synthetic-private\"}", System.Text.Encoding.UTF8, "application/json") }));
        using var http = new HttpClient(transport);
        var client = new PatientSummaryClient(http, Options.Create(new AiServiceSettings { BaseUrl = "http://ai.invalid", InternalApiKey = "synthetic", TimeoutSeconds = 1 }));
        var error = await Assert.ThrowsAsync<PatientSummaryUnavailableException>(() => client.GenerateAsync(Input(), default));
        Assert.Null(error.InnerException); Assert.DoesNotContain("synthetic-private", error.Message);
    }
}
