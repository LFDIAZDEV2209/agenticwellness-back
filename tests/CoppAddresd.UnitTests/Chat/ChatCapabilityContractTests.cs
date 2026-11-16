using System.Net;
using System.Security.Claims;
using System.Text.Json;
using CoppAddresd.Api.Controllers;
using CoppAddresd.Application.Common;
using CoppAddresd.Application.DTOs.Ai;
using CoppAddresd.Application.Features.Chat;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Infrastructure.Services;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CoppAddresd.UnitTests.Chat;

public sealed class ChatCapabilityContractTests
{
    private static async IAsyncEnumerable<StreamChatChunk> Chunks()
    { yield return new("event: done"); await Task.CompletedTask; }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Controller_RequestAntiguoONuevo_PropagaCapacidadYUsaSoloIdentidadJwt(bool supports, bool streaming)
    {
        var json = supports
            ? """{"message":"Hola","supportsVita":true,"userId":"spoofed","customerId":"spoofed"}"""
            : """{"message":"Hola","userId":"spoofed","customerId":"spoofed"}""";
        var input = JsonSerializer.Deserialize<ChatRequestDto>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ChatCommand>(), Arg.Any<CancellationToken>()).Returns(new ChatResult("Hola", "thread"));
        mediator.Send(Arg.Any<StreamChatCommand>(), Arg.Any<CancellationToken>()).Returns(Chunks());
        var owner = Guid.NewGuid().ToString(); using var cts = new CancellationTokenSource();
        var controller = new ChatController(mediator, NullLogger<ChatController>.Instance)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
            [new(ClaimTypes.NameIdentifier, owner), new("aud", "app")], "test")) } } };
        if (streaming)
        {
            await controller.Stream(input, cts.Token);
            await mediator.Received(1).Send(Arg.Is<StreamChatCommand>(c => c.SupportsVita == supports && c.UserId == owner), cts.Token);
        }
        else
        {
            await controller.Chat(input, cts.Token);
            await mediator.Received(1).Send(Arg.Is<ChatCommand>(c => c.SupportsVita == supports && c.UserId == owner), cts.Token);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Handler_ConservaCapacidadEnDtoSyncYStream(bool supports, bool streaming)
    {
        var ai = Substitute.For<IAiServiceClient>();
        ai.ChatAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>()).Returns(new ChatResponse("Hola", "thread"));
        ai.StreamRawAsync(Arg.Any<ChatRequest>(), Arg.Any<Action<string>?>(), Arg.Any<CancellationToken>()).Returns(Chunks());
        var catalog = Substitute.For<IAgentCatalogRepository>(); var runtime = Substitute.For<IAgentRuntimeSyncService>();
        var controls = Substitute.For<IProgramControlRepository>(); var settings = Options.Create(new ProgramControlSettings { Enabled = false });
        using var cts = new CancellationTokenSource();
        if (streaming)
        {
            var handler = new StreamChatCommandHandler(ai, catalog, runtime, controls, settings, NullLogger<StreamChatCommandHandler>.Instance);
            var chunks = await handler.Handle(new("Hola", UserId: "owner", SupportsVita: supports), cts.Token);
            await foreach (var _ in chunks) { }
            ai.Received(1).StreamRawAsync(Arg.Is<ChatRequest>(r => r.SupportsVita == supports && r.UserId == "owner"),
                Arg.Any<Action<string>?>(), cts.Token);
        }
        else
        {
            var handler = new ChatCommandHandler(ai, catalog, runtime, controls, settings, NullLogger<ChatCommandHandler>.Instance);
            await handler.Handle(new("Hola", UserId: "owner", IncludeControlContext: false, SupportsVita: supports), cts.Token);
            await ai.Received(1).ChatAsync(Arg.Is<ChatRequest>(r => r.SupportsVita == supports && r.UserId == "owner"), cts.Token);
        }
    }

    private sealed class Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ClienteHttp_ContratoSupportsVitaSnakeCaseDefaultFalseEnAmbosCanales(bool supports, bool streaming)
    {
        var calls = 0;
        var client = new AiServiceClient(new HttpClient(new Transport(async (request, ct) =>
        {
            calls++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(supports, body.RootElement.GetProperty("supports_vita").GetBoolean());
            Assert.Equal("owner", body.RootElement.GetProperty("user_id").GetString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(streaming
                ? "event: done\ndata: {\"thread_id\":\"thread\"}\n\n" : """{"answer":"Hola","thread_id":"thread"}""") };
        })), Options.Create(new AiServiceSettings { BaseUrl = "http://ai.test" }), NullLogger<AiServiceClient>.Instance);
        var input = supports ? new ChatRequest("Hola", UserId: "owner", SupportsVita: true) : new ChatRequest("Hola", UserId: "owner");
        if (streaming) { await foreach (var _ in client.StreamRawAsync(input)) { } }
        else await client.ChatAsync(input);
        Assert.Equal(1, calls);
    }
}
