using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using CoppAddresd.Api;
using CoppAddresd.Api.Controllers;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace CoppAddresd.UnitTests.Chat;

/// <summary>JWT firmado → controlador → cliente AI y guarda de consentimiento reales; solo el transporte AI es simulado.</summary>
public sealed class ThreadHistoryHttpTests
{
    private const string SigningKey = "synthetic-history-jwt-signing-material-only-for-tests";
    private const string Owner = "10000000-0000-0000-0000-000000000001";
    private const string Other = "10000000-0000-0000-0000-000000000002";
    private const string Confirmation = """{"thread_id":"thread","history_revision":3,"cleared_at":"2026-11-01T05:00:00Z","reset_reason":"manual"}""";

    private sealed class Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Interlocked.Increment(ref Calls); return send(request, ct); }
    }

    private static WebApplicationFactory<ApiEntryPoint> Host(Transport upstream, ILegalConsentService? consents = null) =>
        new WebApplicationFactory<ApiEntryPoint>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing"); builder.UseSetting("SkipDatabaseInitialization", "true");
            builder.UseSetting("Storage:SignatureKey", "synthetic-history-storage-only-for-tests");
            builder.UseSetting("ConnectionStrings:DefaultConnection", "Host=127.0.0.1;Port=1;Database=unused;Username=unused");
            builder.UseSetting("Jwt:Secret", SigningKey); builder.UseSetting("Jwt:Issuer", "history-tests");
            builder.UseSetting("Jwt:ValidAudiences:0", "app"); builder.UseSetting("Jwt:ValidAudiences:1", "erp");
            builder.UseSetting("AiService:BaseUrl", "http://ai.test");
            builder.UseSetting("AiService:InternalApiKey", "synthetic-internal");
            builder.UseSetting("AuthService:BaseUrl", "http://auth.test");
            builder.UseSetting("LegalConsents:EnforcementEnabled", "true");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddSingleton(consents ?? Substitute.For<ILegalConsentService>());
                // Conserva cliente/guarda reales, con transporte aislado y sin esperas de reintentos.
                services.AddHttpClient<IAiServiceClient, AiServiceClient>("HistoryTests")
                    .ConfigurePrimaryHttpMessageHandler(() => upstream).AddHttpMessageHandler<AiConsentHttpHandler>();
                // La comprobación de sesión ERP también permanece aislada de Auth real.
                services.AddHttpClient("ErpSessionValidation").ConfigurePrimaryHttpMessageHandler(() =>
                    new Transport((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent))));
            });
        });

    private static string Jwt(string? user = Owner, string audience = "app", string key = SigningKey, bool expired = false)
    {
        var token = new JwtSecurityToken("history-tests", audience,
            user is null ? [] : [new Claim(JwtRegisteredClaimNames.Sub, user)],
            notBefore: DateTime.UtcNow.AddHours(-2), expires: DateTime.UtcNow.AddMinutes(expired ? -1 : 5),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static HttpResponseMessage Response(int status = 200, string body = Confirmation) => new((HttpStatusCode)status)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Theory]
    [InlineData("absent", HttpStatusCode.Unauthorized)]
    [InlineData("expired", HttpStatusCode.Unauthorized)]
    [InlineData("wrong-signature", HttpStatusCode.Unauthorized)]
    [InlineData("no-owner", HttpStatusCode.Unauthorized)]
    [InlineData("invalid-owner", HttpStatusCode.Unauthorized)]
    [InlineData("erp", HttpStatusCode.Forbidden)]
    [InlineData("wrong-audience", HttpStatusCode.Unauthorized)]
    public async Task Delete_AutenticacionYAudienciaObligatorias_NoInvocaAI(string scenario, HttpStatusCode expected)
    {
        var upstream = new Transport((_, _) => throw new InvalidOperationException("No debe llamar AI"));
        await using var host = Host(upstream); using var client = host.CreateClient();
        if (scenario != "absent") client.DefaultRequestHeaders.Authorization = new("Bearer", scenario switch
        {
            "expired" => Jwt(expired: true), "wrong-signature" => Jwt(key: SigningKey + "invalid"),
            "no-owner" => Jwt(user: null), "invalid-owner" => Jwt(user: "invalid"),
            "erp" => Jwt(audience: "erp"), "wrong-audience" => Jwt(audience: "other"), _ => Jwt(),
        });
        using var response = await client.DeleteAsync("/api/v1/threads/thread/messages");
        Assert.Equal(expected, response.StatusCode); Assert.Equal(0, upstream.Calls);
    }

    [Fact]
    public async Task Delete_IgnoraOwnerDeQueryBodyYCabeceras_NoExigeConsentimientosYDevuelveCamelCase()
    {
        var consents = Substitute.For<ILegalConsentService>();
        // Ninguna consulta de consentimiento puede completarse; borrar debe continuar permitido.
        consents.EnsureUserAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Consentimiento retirado")));
        var upstream = new Transport((request, _) =>
        {
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.Equal("/api/v1/threads/thread/state", request.RequestUri!.AbsolutePath);
            Assert.Equal("?user_id=" + Owner, request.RequestUri.Query);
            Assert.Equal("synthetic-internal", request.Headers.GetValues("X-Internal-Key").Single());
            Assert.False(request.Headers.Contains("X-User-Id")); Assert.Null(request.Content);
            return Task.FromResult(Response());
        });
        await using var host = Host(upstream, consents); using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete,
            $"/api/v1/threads/thread/messages?userId={Other}&user_id={Other}")
            { Content = JsonContent.Create(new { userId = Other, historyRevision = 999, resetReason = "monthly" }) };
        request.Headers.Authorization = new("Bearer", Jwt()); request.Headers.Add("X-User-Id", Other);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var body = json.RootElement;
        Assert.Equal(4, body.EnumerateObject().Count());
        Assert.Equal("thread", body.GetProperty("threadId").GetString()); Assert.Equal(3, body.GetProperty("historyRevision").GetInt64());
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T05:00:00Z"), body.GetProperty("clearedAt").GetDateTimeOffset());
        Assert.Equal("manual", body.GetProperty("resetReason").GetString());
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Empty(consents.ReceivedCalls()); Assert.Equal(1, upstream.Calls);
    }

    [Fact]
    public async Task Delete_MismoThreadEnOtraCuenta_UsaOtroNamespaceYPropaga404()
    {
        var upstream = new Transport((request, _) =>
        {
            Assert.Equal("?user_id=" + Other, request.RequestUri!.Query);
            return Task.FromResult(Response(404, "synthetic-private-detail"));
        });
        await using var host = Host(upstream); using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Jwt(user: Other));
        using var response = await client.DeleteAsync($"/api/v1/threads/thread/messages?userId={Owner}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("synthetic-private-detail", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(403, 403)]
    [InlineData(404, 404)]
    [InlineData(409, 502)]
    [InlineData(500, 502)]
    [InlineData(503, 502)]
    [InlineData(202, 502)]
    [InlineData(204, 502)]
    [InlineData(200, 502)]
    public async Task Delete_ErrorOConfirmacionInvalida_NoExponeDetalleNiDevuelveExito(int upstreamStatus, int expected)
    {
        var upstream = new Transport((_, _) => Task.FromResult(Response(upstreamStatus, "synthetic-private-detail")));
        await using var host = Host(upstream); using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Jwt());
        using var response = await client.DeleteAsync("/api/v1/threads/thread/messages");
        Assert.Equal((HttpStatusCode)expected, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("synthetic-private-detail", body); Assert.DoesNotContain("historyRevision", body);
    }

    [Theory]
    [InlineData(false, 503)]
    [InlineData(true, 504)]
    public async Task Delete_RedOTimeout_ErrorReintentableSinExito(bool timeout, int expected)
    {
        var upstream = new Transport((_, _) => timeout ? throw new TaskCanceledException("synthetic-private-detail")
            : throw new HttpRequestException("synthetic-private-detail"));
        await using var host = Host(upstream); using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Jwt());
        using var response = await client.DeleteAsync("/api/v1/threads/thread/messages");
        Assert.Equal((HttpStatusCode)expected, response.StatusCode);
        Assert.DoesNotContain("synthetic-private-detail", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_VacioTrasLimpieza_ExponeRevisionSinLastMessage()
    {
        var upstream = new Transport((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Contains("user_id=" + Owner, request.RequestUri!.Query);
            return Task.FromResult(Response(200,
                """{"thread_id":"thread","message_count":0,"last_message":null,"messages":[],"history_revision":4,"cleared_at":"2026-11-01T05:00:00Z","reset_reason":"monthly"}"""));
        });
        await using var host = Host(upstream); using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Jwt());
        using var response = await client.GetAsync("/api/v1/threads/thread/messages");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var body = json.RootElement;
        Assert.Equal("thread", body.GetProperty("threadId").GetString());
        Assert.Equal(0, body.GetProperty("messageCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("lastMessage").ValueKind);
        Assert.Equal(0, body.GetProperty("messages").GetArrayLength());
        Assert.Equal(4, body.GetProperty("historyRevision").GetInt64());
        Assert.Equal("monthly", body.GetProperty("resetReason").GetString());
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T05:00:00Z"), body.GetProperty("clearedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Delete_Repetido_ConservaMetadatosIdempotentesConfirmadosPorAI()
    {
        var upstream = new Transport((_, _) => Task.FromResult(Response()));
        await using var host = Host(upstream); using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Jwt());
        using var first = await client.DeleteAsync("/api/v1/threads/thread/messages");
        using var repeated = await client.DeleteAsync("/api/v1/threads/thread/messages");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode); Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await repeated.Content.ReadAsStringAsync());
        Assert.Equal(2, upstream.Calls);
    }

    [Fact]
    public async Task Delete_CancelacionHttp_LlegaHastaAIYNoReintenta()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upstream = new Transport(async (_, ct) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); return Response(); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
        });
        await using var host = Host(upstream); using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Jwt());
        using var cts = new CancellationTokenSource();
        var pending = client.DeleteAsync("/api/v1/threads/thread/messages", cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10)); cts.Cancel();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, upstream.Calls);
    }

    [Fact]
    public async Task Delete_Controlador_PropagaTokenExactoAlCliente()
    {
        var ai = Substitute.For<IAiServiceClient>();
        using var cts = new CancellationTokenSource();
        ai.ClearThreadHistoryAsync("thread", Owner, cts.Token).Returns(new CoppAddresd.Application.Features.Threads.ThreadHistoryClearResult(
            "thread", 3, DateTimeOffset.Parse("2026-11-01T05:00:00Z"), "manual"));
        var controller = new ThreadsController(ai, NullLogger<ThreadsController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(
            new ClaimsIdentity([new(ClaimTypes.NameIdentifier, Owner), new("aud", "app")], "test")) } } };
        await controller.ClearMessages("thread", cts.Token);
        await ai.Received(1).ClearThreadHistoryAsync("thread", Owner, cts.Token);
    }
}
