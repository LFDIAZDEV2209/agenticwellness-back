using System.Net;
using CoppAddresd.Application.Common;
using CoppAddresd.Application.Features.Threads;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CoppAddresd.UnitTests.Chat;

/// <summary>Contrato de transporte AI, con fixtures sintéticos y sin acceso a pacientes.</summary>
public sealed class ThreadHistoryClientTests
{
    private sealed class Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; return send(request, ct); }
    }

    private static AiServiceClient Client(HttpClient http) => new(http,
        Options.Create(new AiServiceSettings { BaseUrl = "http://ai.test", InternalApiKey = "synthetic-internal" }),
        NullLogger<AiServiceClient>.Instance);

    [Theory]
    [InlineData("manual")]
    [InlineData("monthly")]
    public async Task Delete_UsaCanalInternoSinBodyYConservaConfirmacion(string reason)
    {
        var transport = new Transport((request, ct) =>
        {
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.Equal("/api/v1/threads/thread%2Fsynthetic/state", request.RequestUri!.AbsolutePath);
            Assert.Equal("?user_id=owner%2Bsynthetic%26ignored%3D1", request.RequestUri.Query);
            Assert.Equal("synthetic-internal", request.Headers.GetValues("X-Internal-Key").Single());
            Assert.Null(request.Content);
            Assert.True(ct.CanBeCanceled);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                $$"""{"thread_id":"thread/synthetic","history_revision":7,"cleared_at":"2026-11-01T00:00:00-05:00","reset_reason":"{{reason}}"}""") });
        });
        using var http = new HttpClient(transport);
        using var cts = new CancellationTokenSource();
        var result = await Client(http).ClearThreadHistoryAsync("thread/synthetic", "owner+synthetic&ignored=1", cts.Token);
        Assert.Equal(new ThreadHistoryClearResult("thread/synthetic", 7,
            DateTimeOffset.Parse("2026-11-01T00:00:00-05:00"), reason), result);
        Assert.Equal(1, transport.Calls);
    }

    public static TheoryData<string> InvalidConfirmations => new()
    {
        "", "null", "{}", "not-json",
        """{"thread_id":"other","history_revision":1,"cleared_at":"2026-11-01T05:00:00Z","reset_reason":"manual"}""",
        """{"thread_id":"thread","cleared_at":"2026-11-01T05:00:00Z","reset_reason":"manual"}""",
        """{"thread_id":"thread","history_revision":-1,"cleared_at":"2026-11-01T05:00:00Z","reset_reason":"manual"}""",
        """{"thread_id":"thread","history_revision":1.5,"cleared_at":"2026-11-01T05:00:00Z","reset_reason":"manual"}""",
        """{"thread_id":"thread","history_revision":1,"reset_reason":"manual"}""",
        """{"thread_id":"thread","history_revision":1,"cleared_at":"invalid","reset_reason":"manual"}""",
        """{"thread_id":"thread","history_revision":1,"cleared_at":"2026-11-01T05:00:00Z"}""",
        """{"thread_id":"thread","history_revision":1,"cleared_at":"2026-11-01T05:00:00Z","reset_reason":"unknown"}""",
    };

    [Theory]
    [MemberData(nameof(InvalidConfirmations))]
    public async Task Delete_RespuestaIncompletaOMalformada_NoInventaExito(string body)
    {
        using var http = new HttpClient(new Transport((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body) })));
        var error = await Assert.ThrowsAsync<AiServiceException>(() => Client(http).ClearThreadHistoryAsync("thread", "owner"));
        Assert.Equal(502, error.StatusCode);
    }

    [Theory]
    [InlineData(202)]
    [InlineData(204)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task Delete_StatusSinConfirmacion_LanzaError(int status)
    {
        using var http = new HttpClient(new Transport((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status))));
        var error = await Assert.ThrowsAsync<AiServiceException>(() => Client(http).ClearThreadHistoryAsync("thread", "owner"));
        Assert.Equal(status < 300 ? 502 : status, error.StatusCode);
    }

    [Fact]
    public async Task Delete_TokenYaCancelado_NoIniciaTransporte()
    {
        var transport = new Transport((_, _) => throw new InvalidOperationException("No debe llamar AI"));
        using var http = new HttpClient(transport);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(http).ClearThreadHistoryAsync("thread", "owner", cts.Token));
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task Delete_SinClaveInterna_NoIniciaTransporte()
    {
        var transport = new Transport((_, _) => throw new InvalidOperationException("No debe llamar AI"));
        using var http = new HttpClient(transport);
        var client = new AiServiceClient(http, Options.Create(new AiServiceSettings { BaseUrl = "http://ai.test" }),
            NullLogger<AiServiceClient>.Instance);
        await Assert.ThrowsAsync<AiServiceException>(() => client.ClearThreadHistoryAsync("thread", "owner"));
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task DeleteSinOpcionInterna_NoHeredaBypassDeHistorial()
    {
        var consents = Substitute.For<ILegalConsentService>();
        using var services = new ServiceCollection().AddSingleton(consents).BuildServiceProvider();
        var transport = new Transport((_, _) => throw new InvalidOperationException("No debe llamar AI"));
        using var http = new HttpClient(new AiConsentHttpHandler(services.GetRequiredService<IServiceScopeFactory>(), new HttpContextAccessor())
            { InnerHandler = transport });
        using var request = new HttpRequestMessage(HttpMethod.Delete, "http://ai.test/api/v1/threads/thread/state?user_id=synthetic");
        // Ni la URL ni una cabecera del cliente equivalen a la opción server-only.
        request.Headers.Add("CoppAddresd.ThreadHistoryDeletion", "true");
        await Assert.ThrowsAsync<AiConsentException>(() => http.SendAsync(request));
        Assert.Equal(0, transport.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Get_MapeaMetadatosInclusoVacioYConservaCamposPrevios(bool syncVoice)
    {
        var transport = new Transport((request, _) =>
        {
            Assert.Equal(syncVoice, request.RequestUri!.Query.Contains("sync_voice=true", StringComparison.Ordinal));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                """{"thread_id":"thread","message_count":0,"last_message":null,"messages":[],"has_more":false,"next_cursor":null,"voice_sync_status":"synchronized","history_revision":8,"cleared_at":"2026-11-01T05:00:00Z","reset_reason":"monthly"}""") });
        });
        using var http = new HttpClient(transport);
        var client = Client(http);
        var result = syncVoice ? await client.GetThreadStateWithVoiceSyncAsync("thread", "owner")
            : await client.GetThreadStateAsync("thread", "owner");
        Assert.Equal("thread", result.ThreadId); Assert.Equal(0, result.MessageCount); Assert.Null(result.LastMessage);
        Assert.Empty(result.Messages!); Assert.False(result.HasMore); Assert.Null(result.NextCursor);
        Assert.Equal("synchronized", result.VoiceSyncStatus);
        Assert.Equal(8, result.HistoryRevision); Assert.Equal("monthly", result.ResetReason);
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T05:00:00Z"), result.ClearedAt);
    }

    [Fact]
    public async Task Get_AiAnterior_ConservaResumenYPaginacionConRevisionInicial()
    {
        using var http = new HttpClient(new Transport((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("""{"thread_id":"thread","message_count":2,"last_message":"synthetic","messages":[{"role":"bot","text":"synthetic","message_id":"m1","execution_id":"e1"}],"has_more":true,"next_cursor":1}""") })));
        var result = await Client(http).GetThreadStateAsync("thread", "owner");
        Assert.Equal(2, result.MessageCount); Assert.Equal("synthetic", result.LastMessage);
        Assert.Equal("e1", Assert.Single(result.Messages!).ExecutionId);
        Assert.True(result.HasMore); Assert.Equal(1, result.NextCursor);
        Assert.Equal(0, result.HistoryRevision); Assert.Null(result.ClearedAt); Assert.Null(result.ResetReason);
    }
}
