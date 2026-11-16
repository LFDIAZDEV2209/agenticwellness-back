using System.Net;
using System.Text.Json;
using CoppAddresd.Application.Common;
using CoppAddresd.Application.DTOs.Ai;
using CoppAddresd.Application.Features.Chat;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CoppAddresd.UnitTests.Features.Agents;

public sealed class VitaUpstreamContractsTests
{
    private sealed class Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
    private static AiServiceClient Client(Transport transport) => new(new HttpClient(transport),
        Options.Create(new AiServiceSettings { BaseUrl = "http://ai.test", InternalApiKey = "synthetic-internal" }),
        NullLogger<AiServiceClient>.Instance);
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };

    [Fact]
    public async Task SesionVoz_CamposNuevosCruzanSnakeCaseACamelCaseSinJwt()
    {
        var client = Client(new(async (request, ct) =>
        {
            Assert.Equal("/internal/voice/session", request.RequestUri!.AbsolutePath);
            Assert.Null(request.Headers.Authorization);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("user", body.RootElement.GetProperty("user_id").GetString());
            Assert.Equal("thread", body.RootElement.GetProperty("thread_id").GetString());
            return Json("""{"signed_url":"wss://synthetic","agent_id":"agent","conversation_id":"conv","thread_id":"thread","session_id":"session","session_token":"synthetic-reduced-token","expires_at":"2026-11-14T14:10:00Z","runtime_mode":"vita"}""");
        }));
        var result = await client.CreateVoiceSessionAsync(new("user", "patient", "thread", "es", SupportsVitaVoice: true));
        Assert.Equal("thread", result.ThreadId); Assert.Equal("vita", result.RuntimeMode);
        Assert.Equal("session", result.SessionId); Assert.Equal("synthetic-reduced-token", result.SessionToken);
        var wire = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("session", wire.GetProperty("sessionId").GetString());
        Assert.Equal("thread", wire.GetProperty("threadId").GetString());
    }

    [Fact]
    public async Task SesionLegacy_CompatibleSinCamposNuevos()
    {
        var client = Client(new((_, _) => Task.FromResult(Json("""{"signed_url":"wss://synthetic","agent_id":"agent"}"""))));
        var result = await client.CreateVoiceSessionAsync(new("user", null, null));
        Assert.Equal("legacy", result.RuntimeMode); Assert.Null(result.SessionToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SesionVoz_PropagaSupportsVitaVoiceYConservaVinculoLegacy(bool supports)
    {
        var client = Client(new(async (request, ct) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(supports, body.RootElement.GetProperty("supports_vita_voice").GetBoolean());
            return Json("""{"signed_url":"wss://synthetic","agent_id":"agent","thread_id":"thread","session_id":"durable-legacy-session","runtime_mode":"legacy"}""");
        }));
        var input = supports ? new VoiceSessionInternalRequest("user", null, "thread", SupportsVitaVoice: true)
            : new VoiceSessionInternalRequest("user", null, "thread");
        var result = await client.CreateVoiceSessionAsync(input);
        Assert.Equal("legacy", result.RuntimeMode); Assert.Equal("thread", result.ThreadId);
        Assert.Equal("durable-legacy-session", result.SessionId);
    }

    [Fact]
    public async Task Revocacion_PropagaPropietarioYClaveInterna()
    {
        var client = Client(new((request, _) =>
        {
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.Equal("/internal/voice/session/session_one", request.RequestUri!.AbsolutePath);
            Assert.Equal("?user_id=owner", request.RequestUri.Query);
            Assert.Equal("synthetic-internal", request.Headers.GetValues("X-Internal-Key").Single());
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }));
        await client.RevokeVoiceSessionAsync("session_one", "owner");
    }

    [Fact]
    public async Task Historial_PreservaIdentidadCanalEstadoRevisionYPaginacion()
    {
        var client = Client(new((_, _) => Task.FromResult(Json("""{"thread_id":"t","message_count":2,"messages":[{"role":"user","text":"igual","message_id":"u1","channel":"voice","status":"final","revision":2},{"role":"user","text":"igual","message_id":"u2","channel":"voice","status":"interrupted","revision":3}],"has_more":true,"next_cursor":2}"""))));
        var state = await client.GetThreadStateAsync("t", "owner");
        Assert.Equal(2, state.Messages!.Count); Assert.NotEqual(state.Messages[0].MessageId, state.Messages[1].MessageId);
        var wire = JsonSerializer.SerializeToElement(state, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("u1", wire.GetProperty("messages")[0].GetProperty("messageId").GetString());
        Assert.Equal("voice", state.Messages[0].Channel); Assert.Equal(2, state.Messages[0].Revision);
        Assert.Equal("interrupted", state.Messages[1].Status); Assert.True(state.HasMore); Assert.Equal(2, state.NextCursor);
    }

    [Theory]
    [InlineData("synchronized")]
    [InlineData("processing")]
    [InlineData("unavailable")]
    [InlineData("busy")]
    public async Task Historial_SyncOptInPreservaEstadoYProcedenciaCamelCase(string status)
    {
        var client = Client(new((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Contains("sync_voice=true", request.RequestUri!.Query);
            Assert.True(request.Options.TryGetValue(AiConsentSubject.Key, out var subject)); Assert.True(subject!.RequireVoiceConsent);
            return Task.FromResult(Json($$"""{"thread_id":"t","message_count":1,"voice_sync_status":"{{status}}","messages":[{"role":"bot","text":"Verificado","message_id":"canonical-id","channel":"voice","status":"provider_final","revision":2,"source":"elevenlabs_transcript","voice_session_id":"session","provider_message_id":"provider-id","source_event_id":3}]}"""));
        }));
        var result = await client.GetThreadStateWithVoiceSyncAsync("t", Guid.NewGuid().ToString());
        var wire = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(status, wire.GetProperty("voiceSyncStatus").GetString());
        var message = wire.GetProperty("messages")[0];
        Assert.Equal("elevenlabs_transcript", message.GetProperty("source").GetString());
        Assert.Equal("session", message.GetProperty("voiceSessionId").GetString());
        Assert.Equal("provider-id", message.GetProperty("providerMessageId").GetString());
        Assert.Equal(3, message.GetProperty("sourceEventId").GetInt64());
        Assert.Equal("canonical-id", message.GetProperty("messageId").GetString());
    }

    [Fact]
    public async Task HistorialAntiguo_NoSolicitaSyncYCambiosOpcionalesQuedanNull()
    {
        var client = Client(new((request, _) =>
        {
            Assert.DoesNotContain("sync_voice", request.RequestUri!.Query);
            return Task.FromResult(Json("""{"thread_id":"t","message_count":1,"messages":[{"role":"bot","text":"Texto"}]}"""));
        }));
        var result = await client.GetThreadStateAsync("t", "owner");
        Assert.Null(result.VoiceSyncStatus); Assert.Null(Assert.Single(result.Messages!).SourceEventId);
    }

    [Fact]
    public async Task Historial_ExecutionIdRoundtripDirecto_TextoYNullEnLegadoOVoz()
    {
        var execution = Guid.NewGuid().ToString(); var calls = 0;
        var client = Client(new((_, _) =>
        {
            calls++;
            return Task.FromResult(Json($$"""{"thread_id":"t","message_count":3,"messages":[{"role":"bot","text":"Igual","message_id":"text-id","execution_id":"{{execution}}","channel":"text"},{"role":"bot","text":"Igual","message_id":"voice-id","channel":"voice","execution_id":null},{"role":"bot","text":"Igual","message_id":"legacy-id"}]}"""));
        }));
        var result = await client.GetThreadStateAsync("t", "owner");
        var wire = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var messages = wire.GetProperty("messages");
        Assert.Equal(execution, messages[0].GetProperty("executionId").GetString());
        Assert.Equal("text-id", messages[0].GetProperty("messageId").GetString());
        Assert.Equal(JsonValueKind.Null, messages[1].GetProperty("executionId").ValueKind);
        Assert.Equal(JsonValueKind.Null, messages[2].GetProperty("executionId").ValueKind);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Sync_ActionsSourcesVersionCruzanClienteYHandlerSinPerderCamposCamelCase()
    {
        var id = Guid.NewGuid(); var version = Guid.NewGuid().ToString();
        var payload = $$"""{"answer":"Hechos","thread_id":"t","agent":"nutrition","version_id":"{{version}}","actions":[{"type":"navigate","destination":"podcast","resourceId":"{{id}}","localDate":"2026-11-14","resourceVersion":"v"}],"sources":[{"capability":"podcast","caseUse":"GetSnapshotQuery"}]}""";
        var client = Client(new((_, _) => Task.FromResult(Json(payload))));
        var upstream = await client.ChatAsync(new("Pregunta"));
        Assert.Equal(id, Assert.Single(upstream.Actions!).ResourceId);
        Assert.Equal(new DateOnly(2026, 11, 14), upstream.Actions![0].LocalDate);
        var handler = new ChatCommandHandler(client, Substitute.For<IAgentCatalogRepository>(),
            Substitute.For<IAgentRuntimeSyncService>(), Substitute.For<IProgramControlRepository>(),
            Options.Create(new ProgramControlSettings { Enabled = false }), NullLogger<ChatCommandHandler>.Instance);
        var result = await handler.Handle(new("Pregunta", IncludeControlContext: false), default);
        var wire = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(version, wire.GetProperty("versionId").GetString());
        Assert.Equal(id, wire.GetProperty("actions")[0].GetProperty("resourceId").GetGuid());
        Assert.Equal("GetSnapshotQuery", wire.GetProperty("sources")[0].GetProperty("caseUse").GetString());
    }

    [Fact]
    public async Task DoneSse_ConservaMetadatosYAccionesDelRuntime()
    {
        const string done = "event: done\ndata: {\"thread_id\":\"t\",\"agent\":\"specialist\",\"version_id\":\"v2\",\"actions\":[{\"type\":\"navigate\",\"destination\":\"nutrition\"}],\"sources\":[{\"capability\":\"nutrition\"}]}\n\n";
        var client = Client(new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(done) })));
        var lines = new List<string>(); await foreach (var item in client.StreamRawAsync(new("Pregunta"))) lines.Add(item.RawData);
        Assert.Equal(done, string.Join("\n", lines) + "\n");
    }
}
