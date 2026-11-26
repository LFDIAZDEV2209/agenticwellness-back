using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Reflection;
using CoppAddresd.Api;
using CoppAddresd.Api.Internal;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace CoppAddresd.UnitTests.Features.Agents;

/// <summary>Pipeline HTTP real de BACK; transporte de AI simulado, sin llamadas ElevenLabs.</summary>
public sealed class VitaVoiceProxyHttpTests
{
    private const string Key = "synthetic-vita-voice-service-key-32-bytes";
    private const string Route = "/api/v1/internal/vita/voice/v1/chat/completions";
    private const string Event = "data: {\"choices\":[{\"delta\":{\"content\":\"Hola\"}}]}\n\ndata: [DONE]\n\n";
    private const string ToolEvent = "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_language\",\"type\":\"function\",\"function\":{\"name\":\"language_detection\",\"arguments\":\"{\\\"reason\\\":\\\"Synthetic language switch\\\",\\\"language\\\":\\\"en\\\"}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\n";
    private sealed class Upstream(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Interlocked.Increment(ref Calls); return send(request, ct); }
    }

    private static WebApplicationFactory<ApiEntryPoint> Host(HttpMessageHandler upstream, bool enabled = true,
        OutcomeLog? outcomeLog = null) =>
        new WebApplicationFactory<ApiEntryPoint>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("SkipDatabaseInitialization", "true");
            builder.UseSetting("Storage:SignatureKey", "synthetic-signature-only-for-vita-http-tests");
            builder.UseSetting("ConnectionStrings:DefaultConnection", "Host=127.0.0.1;Port=1;Database=unused;Username=unused");
            builder.UseSetting("Jwt:Secret", "synthetic-jwt-secret-only-for-vita-http-tests");
            builder.UseSetting("Jwt:Issuer", "synthetic-vita");
            builder.UseSetting("Vita:VoiceProxyEnabled", enabled.ToString());
            builder.UseSetting("Vita:VoiceServiceKey", Key);
            builder.UseSetting("AiService:BaseUrl", "http://ai.test");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddHttpClient("VitaVoice").ConfigurePrimaryHttpMessageHandler(() => upstream);
                if (outcomeLog is not null) services.AddSingleton<ILogger<VitaVoiceProxyController>>(outcomeLog);
            });
        });

    private static HttpRequestMessage Request(string? key = Key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Route)
        { Content = new StringContent("""{"stream":true,"messages":[{"role":"user","content":"Hola"}]}""", Encoding.UTF8, "application/json") };
        if (key is not null) request.Headers.Authorization = new("Bearer", key);
        request.Headers.Add("X-Vita-Session", "v1.synthetic-token.signature");
        request.Headers.Add("X-Vita-Conversation", "conv_synthetic");
        request.Headers.Add("X-Vita-Turn", "1");
        return request;
    }

    [Theory]
    [InlineData("data: [DONE]\n\n", "completed")]
    [InlineData("data: [DONE]\r\n\r\n", "completed")]
    [InlineData("data: [DONE]\n", "incomplete")]
    [InlineData("data: [DONE]extra\n\n", "incomplete")]
    [InlineData("data: first line\ndata: [DONE]\n\n", "incomplete")]
    [InlineData("data: {\"content\":\"data: [DONE]\"}\n\n", "incomplete")]
    [InlineData("", "incomplete")]
    public async Task ProxyOutcome_RequiresACompleteTerminalFrameAndDoesNotChangeTheResponse(string tail, string expected)
    {
        const string payload = "data: {\"content\":\"qa-only-text\"}\n\n";
        var output = payload + tail;
        var upstream = new Upstream((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new FragmentedStream(Encoding.UTF8.GetBytes(output)))
                { Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") } }
        }));
        var log = new OutcomeLog();
        await using var host = Host(upstream, outcomeLog: log); using var client = host.CreateClient();
        using var request = Request(); using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(output, await response.Content.ReadAsStringAsync());
        var entry = Assert.Single(log.Entries);
        Assert.Equal(expected, entry["Outcome"]);
        Assert.Equal(200, entry["StatusCode"]);
        Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<string>(entry["ProxyRequestId"])));
        Assert.Equal(new[] { "DurationMs", "Outcome", "ProxyRequestId", "StatusCode", "{OriginalFormat}" },
            entry.Keys.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(false, "client_aborted")]
    [InlineData(true, "transport_error")]
    public async Task ProxyOutcome_HeadersAndForwardedMarkerAreNotCompletionIfTheStreamBreaks(bool upstreamFails, string expected)
    {
        var atEnd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new WaitingStream(atEnd, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            Event, upstreamFails ? finish.Task : null);
        var upstream = new Upstream((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream) { Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") } }
        }));
        var log = new OutcomeLog();
        await using var host = Host(upstream, outcomeLog: log); using var client = host.CreateClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var request = Request();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        await atEnd.Task.WaitAsync(cts.Token);
        Assert.Empty(log.Entries);
        if (upstreamFails) finish.SetResult();
        else { cts.Cancel(); response.Dispose(); }

        await log.Written.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(expected, Assert.Single(log.Entries)["Outcome"]);
        Assert.Equal(1, upstream.Calls);
    }

    [Fact]
    public async Task ProxyOutcome_Upstream503DoesNotCountAsACompletedStream()
    {
        var upstream = new Upstream((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("synthetic upstream error details")
        }));
        var log = new OutcomeLog();
        await using var host = Host(upstream, outcomeLog: log); using var client = host.CreateClient();
        using var request = Request(); using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var entry = Assert.Single(log.Entries);
        Assert.Equal("upstream_rejected", entry["Outcome"]);
        Assert.Equal(503, entry["StatusCode"]);
    }

    [Fact]
    public async Task ProxyHTTP_PreservaTresCabecerasBodyYSseSinJWTClaveInternaNiCookies()
    {
        var upstream = new Upstream(async (request, ct) =>
        {
            Assert.Equal("/internal/voice/v1/chat/completions", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer " + Key, request.Headers.Authorization!.ToString());
            Assert.Equal("v1.synthetic-token.signature", request.Headers.GetValues("X-Vita-Session").Single());
            Assert.Equal("conv_synthetic", request.Headers.GetValues("X-Vita-Conversation").Single());
            Assert.Equal("1", request.Headers.GetValues("X-Vita-Turn").Single());
            Assert.False(request.Headers.Contains("X-Internal-Key")); Assert.False(request.Headers.Contains("Cookie"));
            Assert.False(request.Headers.Contains("X-Untrusted"));
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("Hola", body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
            return new(HttpStatusCode.OK) { Content = new StringContent(Event, Encoding.UTF8, "text/event-stream") };
        });
        await using var host = Host(upstream); using var client = host.CreateClient();
        using var request = Request(); request.Headers.Add("X-Internal-Key", "must-not-forward");
        request.Headers.Add("Cookie", "patient-jwt=must-not-forward"); request.Headers.Add("X-Untrusted", "ignored");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Event, await response.Content.ReadAsStringAsync()); Assert.Equal(1, upstream.Calls);
    }

    [Fact]
    public async Task ToolsLanguageDetection_IdaYResultado_ConservaJsonGrandeYSse()
    {
        var payload = JsonSerializer.Serialize(new
        {
            stream = true,
            messages = new object[] { new { role = "user", content = "Please speak English" } },
            tools = new[] { new { type = "function", function = new { name = "language_detection",
                description = new string('x', 96 * 1024), parameters = new { type = "object",
                    properties = new { reason = new { type = "string" }, language = new { type = "string" } },
                    required = new[] { "reason", "language" } } } } },
        });
        var returned = """{"stream":true,"messages":[{"role":"user","content":"Please speak English"},{"role":"assistant","content":null,"tool_calls":[{"id":"call_language","type":"function","function":{"name":"language_detection","arguments":"{\"reason\":\"Synthetic language switch\",\"language\":\"en\"}"}}]},{"role":"tool","tool_call_id":"call_language","content":"{\"language\":\"en\"}"}]}""";
        var size = Encoding.UTF8.GetByteCount(payload);
        Assert.True(size > 65536);
        // TestServer no aplica límites de Kestrel: comprobar también el límite efectivo declarado.
        var attribute = typeof(VitaVoiceProxyController).GetMethod(nameof(VitaVoiceProxyController.Stream))!
            .GetCustomAttribute<RequestSizeLimitAttribute>()!;
        var limit = ((Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata)attribute).MaxRequestBodySize!.Value;
        Assert.InRange(size, 65537L, limit); Assert.Equal(1024 * 1024, limit);
        var calls = 0;
        var upstream = new Upstream(async (request, ct) =>
        {
            Assert.Equal("/internal/voice/v1/chat/completions", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer " + Key, request.Headers.Authorization!.ToString());
            var received = await request.Content!.ReadAsStringAsync(ct);
            var expected = calls++ == 0 ? payload : returned;
            Assert.Equal(JsonSerializer.Serialize(JsonDocument.Parse(expected).RootElement),
                JsonSerializer.Serialize(JsonDocument.Parse(received).RootElement));
            return new(HttpStatusCode.OK) { Content = new StringContent(calls == 1 ? ToolEvent + "data: [DONE]\n\n" : Event,
                Encoding.UTF8, "text/event-stream") };
        });
        await using var host = Host(upstream); using var client = host.CreateClient();
        foreach (var body in new[] { payload, returned })
        {
            using var request = Request(); request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(body == payload ? ToolEvent + "data: [DONE]\n\n" : Event, await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(2, upstream.Calls);
    }

    [Fact]
    public async Task ToolCall_SeEntregaAntesDelFinDelStream_SinBufferizarRespuesta()
    {
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upstream = new Upstream((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new WaitingStream(blocked, cancelled, ToolEvent))
                { Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") } } }));
        await using var host = Host(upstream); using var client = host.CreateClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var request = Request();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal("no", response.Headers.GetValues("X-Accel-Buffering").Single());
        await using var body = await response.Content.ReadAsStreamAsync(cts.Token);
        var bytes = new byte[Encoding.UTF8.GetByteCount(ToolEvent)];
        await body.ReadExactlyAsync(bytes, cts.Token);
        Assert.Equal(ToolEvent, Encoding.UTF8.GetString(bytes));
        await blocked.Task.WaitAsync(cts.Token); Assert.False(cancelled.Task.IsCompleted);
        cts.Cancel(); response.Dispose(); await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, upstream.Calls);
    }

    [Fact]
    public void DeployDev_DeclaraProxySoloEnApi_YMantieneClaveComoSecret()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CoppAddresd.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var workflow = File.ReadAllText(Path.Combine(root.FullName, ".github", "workflows", "deploy-backend.yml"));
        Assert.Contains("branches: [dev]", workflow);
        var api = workflow.Split("            api)", StringSplitOptions.None)[1].Split("              ;;", StringSplitOptions.None)[0];
        var envJson = api.Split("echo \"extra_env=", StringSplitOptions.None)[1].Split("\" >>", StringSplitOptions.None)[0].Replace("\\\"", "\"");
        using var env = JsonDocument.Parse(envJson);
        Assert.Equal("true", Assert.Single(env.RootElement.EnumerateArray(),
            value => value.GetProperty("name").GetString() == "Vita__VoiceProxyEnabled").GetProperty("value").GetString());
        Assert.DoesNotContain("Vita__VoiceServiceKey", envJson);
        Assert.Contains("\\\"name\\\":\\\"Vita__VoiceServiceKey\\\",\\\"valueFrom\\\":", api);
        Assert.Equal(1, workflow.Split("Vita__VoiceProxyEnabled", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task ClienteVoice_MantieneTimeout90Segundos()
    {
        await using var host = Host(new Upstream((_, _) => throw new InvalidOperationException("No debe invocarse")));
        using var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("VitaVoice");
        Assert.Equal(TimeSpan.FromSeconds(90), client.Timeout);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("1000000")]
    public async Task ContadorDelProveedor_IncluyeCeroYSeReenviaSinCambiar(string value)
    {
        var upstream = new Upstream((request, _) =>
        {
            Assert.Equal(value, request.Headers.GetValues("X-Vita-Turn").Single());
            Assert.Equal("Bearer " + Key, request.Headers.Authorization!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(Event, Encoding.UTF8, "text/event-stream") });
        });
        await using var host = Host(upstream); using var client = host.CreateClient();
        using var request = Request(); request.Headers.Remove("X-Vita-Turn"); request.Headers.Add("X-Vita-Turn", value);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Event, await response.Content.ReadAsStringAsync()); Assert.Equal(1, upstream.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("patient-jwt")]
    [InlineData("internal-master-key")]
    public async Task ClaveDedicadaObligatoria_NoInvocaAI(string? key)
    {
        var upstream = new Upstream((_, _) => throw new InvalidOperationException("No debe invocarse"));
        var log = new OutcomeLog();
        await using var host = Host(upstream, outcomeLog: log); using var client = host.CreateClient();
        using var request = Request(key); request.Headers.Remove("X-Vita-Turn"); request.Headers.Add("X-Vita-Turn", "0");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); Assert.Equal(0, upstream.Calls);
        Assert.Empty(log.Entries);
    }

    [Theory]
    [InlineData("X-Vita-Session", "bad token")]
    [InlineData("X-Vita-Conversation", "../other")]
    [InlineData("X-Vita-Session", null)]
    [InlineData("X-Vita-Conversation", null)]
    [InlineData("X-Vita-Turn", null)]
    [InlineData("X-Vita-Turn", "-1")]
    [InlineData("X-Vita-Turn", "1000001")]
    [InlineData("X-Vita-Turn", "2147483648")]
    [InlineData("X-Vita-Turn", "invalid")]
    [InlineData("X-Vita-Turn", "1.5")]
    [InlineData("X-Vita-Turn", "")]
    [InlineData("X-Vita-Turn", "+1")]
    public async Task CabeceraInvalidaOAusente_RechazaAntesDeAI(string header, string? value)
    {
        var upstream = new Upstream((_, _) => throw new InvalidOperationException("No debe invocarse"));
        await using var host = Host(upstream); using var client = host.CreateClient();
        using var request = Request(); request.Headers.Remove(header);
        if (value is not null) request.Headers.Add(header, value);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode); Assert.Equal(0, upstream.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CabeceraDuplicadaODemasiadoLarga_Rechaza(bool duplicate)
    {
        var upstream = new Upstream((_, _) => throw new InvalidOperationException("No debe invocarse"));
        await using var host = Host(upstream); using var client = host.CreateClient();
        using var request = Request();
        if (duplicate) request.Headers.Add("X-Vita-Conversation", "conv_other");
        else { request.Headers.Remove("X-Vita-Session"); request.Headers.Add("X-Vita-Session", new string('a', 4097)); }
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode); Assert.Equal(0, upstream.Calls);
    }

    [Fact]
    public async Task ProxyApagado_404SinAI()
    {
        var upstream = new Upstream((_, _) => throw new InvalidOperationException("No debe invocarse"));
        await using var host = Host(upstream, false); using var client = host.CreateClient();
        using var response = await client.SendAsync(Request());
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); Assert.Equal(0, upstream.Calls);
    }

    [Fact]
    public async Task CancelacionHTTP_CancelaLecturaUpstreamSinReintentar()
    {
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new WaitingStream(blocked, cancelled);
        var upstream = new Upstream((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(stream) { Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") } } }));
        await using var host = Host(upstream); using var client = host.CreateClient();
        using var cts = new CancellationTokenSource(); using var request = Request();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(10)); cts.Cancel(); response.Dispose();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(1, upstream.Calls);
    }

    private sealed class OutcomeLog : ILogger<VitaVoiceProxyController>
    {
        public ConcurrentQueue<IReadOnlyDictionary<string, object?>> Entries { get; } = new();
        public TaskCompletionSource Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> properties) return;
            var fields = properties.ToDictionary();
            if (fields.GetValueOrDefault("{OriginalFormat}") is not string format
                || !format.StartsWith("VitaVoiceProxyOutcome ", StringComparison.Ordinal)) return;
            Entries.Enqueue(fields);
            Written.TrySetResult();
        }
    }

    private sealed class FragmentedStream(byte[] content) : MemoryStream(content)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], ct);
    }

    private sealed class WaitingStream(TaskCompletionSource blocked, TaskCompletionSource cancelled,
        string initial = "data: {}\n\n", Task? failWhen = null) : Stream
    {
        private bool sent;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!sent) { sent = true; var bytes = Encoding.UTF8.GetBytes(initial); bytes.CopyTo(buffer); return bytes.Length; }
            blocked.TrySetResult();
            if (failWhen is not null)
            {
                await failWhen.WaitAsync(ct);
                throw new HttpRequestException("synthetic upstream failure");
            }
            try { await Task.Delay(Timeout.Infinite, ct); return 0; }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
