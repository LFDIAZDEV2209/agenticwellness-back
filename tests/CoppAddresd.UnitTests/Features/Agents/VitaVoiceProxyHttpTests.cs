using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CoppAddresd.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CoppAddresd.UnitTests.Features.Agents;

/// <summary>Pipeline HTTP real de BACK; transporte de AI simulado, sin llamadas ElevenLabs.</summary>
public sealed class VitaVoiceProxyHttpTests
{
    private const string Key = "synthetic-vita-voice-service-key-32-bytes";
    private const string Route = "/api/v1/internal/vita/voice/v1/chat/completions";
    private const string Event = "data: {\"choices\":[{\"delta\":{\"content\":\"Hola\"}}]}\n\ndata: [DONE]\n\n";
    private sealed class Upstream(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Interlocked.Increment(ref Calls); return send(request, ct); }
    }

    private static WebApplicationFactory<ApiEntryPoint> Host(HttpMessageHandler upstream, bool enabled = true) =>
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

    [Theory]
    [InlineData(null)]
    [InlineData("patient-jwt")]
    [InlineData("internal-master-key")]
    public async Task ClaveDedicadaObligatoria_NoInvocaAI(string? key)
    {
        var upstream = new Upstream((_, _) => throw new InvalidOperationException("No debe invocarse"));
        await using var host = Host(upstream); using var client = host.CreateClient();
        using var response = await client.SendAsync(Request(key));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); Assert.Equal(0, upstream.Calls);
    }

    [Theory]
    [InlineData("X-Vita-Session", "bad token")]
    [InlineData("X-Vita-Conversation", "../other")]
    [InlineData("X-Vita-Turn", "0")]
    [InlineData("X-Vita-Turn", "1000001")]
    public async Task CabeceraInvalidaOAusente_RechazaAntesDeAI(string header, string value)
    {
        var upstream = new Upstream((_, _) => throw new InvalidOperationException("No debe invocarse"));
        await using var host = Host(upstream); using var client = host.CreateClient();
        using var request = Request(); request.Headers.Remove(header); request.Headers.Add(header, value);
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

    private sealed class WaitingStream(TaskCompletionSource blocked, TaskCompletionSource cancelled) : Stream
    {
        private bool sent;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!sent) { sent = true; var bytes = Encoding.UTF8.GetBytes("data: {}\n\n"); bytes.CopyTo(buffer); return bytes.Length; }
            blocked.TrySetResult();
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
