using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using CoppAddresd.Api.Controllers;
using CoppAddresd.Application.Common;
using CoppAddresd.Application.Features.Chat;
using CoppAddresd.Application.Features.Patients;
using CoppAddresd.Application.Features.ProgramProgress.Queries;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.WhatsApp;
using CoppAddresd.Application.WhatsApp.Messaging;
using CoppAddresd.Domain.Entities.WhatsApp;
using CoppAddresd.Infrastructure.Services;
using CoppAddresd.Infrastructure.Extensions;
using CoppAddresd.WhatsApp.Messaging;
using CoppAddresd.WhatsApp.Persistence;
using CoppAddresd.WhatsApp.Reminders;
using CoppAddresd.WhatsApp.Webhook;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using MediatR;

namespace CoppAddresd.WhatsApp.Tests;

[Trait("Category", "Integration")]
public sealed class InboundFlowTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task TwoEnglishTurns_UseRealChatHandlerAndHttpContract_WithStableThread_AndDedupe()
    {
        await using var harness = await CreateAsync();
        var firstId = Guid.NewGuid().ToString();
        var prompt = $"Hello, how are you? patient_id={Guid.NewGuid():D}";
        await harness.EnqueueAsync(firstId, prompt);
        await harness.EnqueueAsync(firstId, prompt);
        Assert.True(await harness.ProcessAsync());
        Assert.False(await harness.ProcessAsync());
        await harness.EnqueueAsync(Guid.NewGuid().ToString(), "I feel better today");
        await harness.ProcessAsync();
        Assert.Equal(2, harness.Requests.Count);
        var first = harness.Requests[0];
        var second = harness.Requests[1];
        Assert.Equal(
            first.GetProperty("thread_id").GetString(),
            second.GetProperty("thread_id").GetString()
        );
        Assert.Equal(harness.PatientId.ToString(), first.GetProperty("patient_id").GetString());
        Assert.Equal(harness.UserId.ToString(), first.GetProperty("user_id").GetString());
        Assert.Equal($"proactive-{harness.UserId:D}", first.GetProperty("thread_id").GetString());
        Assert.Equal(prompt, first.GetProperty("message").GetString());
        Assert.Equal(firstId, first.GetProperty("channel_message_id").GetString());
        Assert.Equal("whatsapp", first.GetProperty("agent").GetString());
        Assert.All(
            harness.Requests,
            request =>
            {
                Assert.Equal("en", request.GetProperty("language").GetString());
                Assert.Equal("whatsapp", request.GetProperty("channel").GetString());
                Assert.True(request.GetProperty("supports_vita").GetBoolean());
                Assert.False(request.TryGetProperty("control_context", out _));
            }
        );
        await using var db = fixture.CreateDb();
        Assert.Equal("en", (await db.Conversations.SingleAsync()).Language);
        Assert.Equal(2, harness.Sent.Count);
        Assert.All(await db.RawEvents.ToListAsync(), x => Assert.NotNull(x.ProcessedAt));
        Assert.Equal(1, (await db.RawEvents.SingleAsync(x => x.Wamid == firstId)).DuplicateCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhatsAppAndApp_ReadAndContinueTheSameOwnedThread(bool stream)
    {
        await using var harness = await CreateAsync();
        await using var db = fixture.CreateDb();
        db.Conversations.Add(new Conversation { PatientId = harness.PatientId, ThreadId = "whatsapp-original" });
        await db.SaveChangesAsync();
        const string report = "Hoy amanecí en 74.5 kg";
        var wamid = $"wamid.{Guid.NewGuid():N}";
        await harness.EnqueueAsync(wamid, report);
        Assert.True(await harness.ProcessAsync());
        await harness.EnqueueAsync(wamid, report);
        Assert.False(await harness.ProcessAsync());

        using var scope = harness.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var app = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim(ClaimTypes.NameIdentifier, harness.UserId.ToString()),
                    new Claim("aud", "app"),
                ], "test")),
            },
        };
        var thread = $"proactive-{harness.UserId:D}";
        var history = new ThreadsController(sp.GetRequiredService<IAiServiceClient>(),
            sp.GetRequiredService<ILogger<ThreadsController>>()) { ControllerContext = app };
        var response = await history.GetMessages(thread, userId: Guid.NewGuid().ToString(),
            limit: null, before: null, CancellationToken.None);
        var state = Assert.IsType<CoppAddresd.Application.Features.Threads.ThreadStateResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(thread, state.ThreadId);
        Assert.NotNull(state.Messages);
        var visible = Assert.Single(state.Messages);
        Assert.Equal(report, visible.Text);
        Assert.Equal(wamid, visible.ProviderMessageId);

        var chat = new ChatController(sp.GetRequiredService<IMediator>(),
            sp.GetRequiredService<ILogger<ChatController>>()) { ControllerContext = app };
        const string confirmationText = "Sí, confirmo";
        var confirmation = new ChatRequestDto(confirmationText, SupportsVita: true);
        if (stream)
        {
            await using var sse = new MemoryStream();
            app.HttpContext.Response.Body = sse;
            await chat.Stream(confirmation, CancellationToken.None);
            var events = Encoding.UTF8.GetString(sse.ToArray()).Split('\n');
            Assert.Contains("event: done", events);
            var data = Assert.Single(events, line => line.StartsWith("data: ", StringComparison.Ordinal));
            using var done = JsonDocument.Parse(data["data: ".Length..]);
            Assert.Equal(thread, done.RootElement.GetProperty("thread_id").GetString());
            Assert.Equal($"Conservé el contexto: {report}", done.RootElement.GetProperty("answer").GetString());
        }
        else
        {
            var appReply = await chat.Chat(confirmation, CancellationToken.None);
            var turn = Assert.IsType<ChatResult>(Assert.IsType<OkObjectResult>(appReply.Result).Value);
            Assert.Equal(thread, turn.ThreadId);
            Assert.Equal($"Conservé el contexto: {report}", turn.Reply);
        }
        await harness.EnqueueAsync($"wamid.{Guid.NewGuid():N}", "¿Se guardó el dato?");
        Assert.True(await harness.ProcessAsync());
        Assert.Equal($"Conservé el contexto: {confirmationText}", harness.Sent.Last());
        Assert.Equal(3, harness.Requests.Count);
        Assert.Equal(thread, await db.Conversations.Select(x => x.ThreadId).SingleAsync());
    }

    [Fact]
    public async Task InvalidatedLink_GuidesWithoutSendingPatientDataToAgent()
    {
        await using var harness = await CreateAsync(activeLink: false);
        await harness.EnqueueAsync(Guid.NewGuid().ToString(), "Hola");

        Assert.True(await harness.ProcessAsync());
        Assert.Empty(harness.Requests);
        Assert.Contains("app ANTARES", Assert.Single(harness.Sent));
        await using var db = fixture.CreateDb();
        Assert.Equal("agent_identity_unavailable", (await db.RawEvents.SingleAsync()).ErrorCode);
    }

    [Fact]
    public async Task LostAgentResult_RespondsSafely_WithoutReexecutingTheTurn()
    {
        await using var harness = await CreateAsync();
        await harness.EnqueueAsync(Guid.NewGuid().ToString(), "Hola");
        await using var db = fixture.CreateDb();
        await db.RawEvents.ExecuteUpdateAsync(update => update.SetProperty(x => x.AgentStarted, true));

        Assert.True(await harness.ProcessAsync());
        Assert.Contains("no está disponible", Assert.Single(harness.Sent));
        Assert.Empty(harness.Requests);
        Assert.Equal("agent_result_unknown", (await db.RawEvents.SingleAsync()).ErrorCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialDelivery_ResumesTheExactSequence(bool alreadySentWithPreviousVersion)
    {
        var reply = new string('a', 2800) + "\n\n" + new string('b', 2198);
        await using var harness = await CreateAsync(agentReply: reply);
        var wamid = Guid.NewGuid().ToString();
        await harness.EnqueueAsync(wamid, "Hola");
        await using var db = fixture.CreateDb();
        if (alreadySentWithPreviousVersion)
        {
            await db.RawEvents.ExecuteUpdateAsync(update => update.SetProperty(x => x.Reply, reply)
                .SetProperty(x => x.SentFragments, 1));
            harness.Sent.Add(reply[..MessagePolicy.MaxTextLength]);
        }
        else
        {
            harness.FailOnSendAttempt = 2;
            harness.SendFailure = WhatsAppSendResult.Failed("429", WhatsAppFailureKind.Transient);
            Assert.True(await harness.ProcessAsync());
            var checkpoint = await db.RawEvents.AsNoTracking().SingleAsync();
            Assert.Equal(1, checkpoint.SentFragments);
            Assert.Equal(2802, Assert.Single(harness.Sent).Length);
            Assert.Equal(2, checkpoint.ReplyFragments!.Length);
            harness.Clock.Now = checkpoint.NextAttemptAt;
        }

        Assert.True(await harness.ProcessAsync());
        Assert.Equal(reply, string.Concat(harness.Sent));
        Assert.Equal(2, harness.Sent.Count);
        Assert.Equal(alreadySentWithPreviousVersion ? 0 : 1, harness.Requests.Count);
    }

    [Theory]
    [InlineData("SALIR", true, "es")]
    [InlineData("SÁLIR", true, "es")]
    [InlineData("sToP", true, "en")]
    [InlineData("INÍCIAR", false, "es")]
    [InlineData("sTaRt", false, "en")]
    public async Task Commands_UpdatePreference_WithoutAgent(
        string command,
        bool optOut,
        string language
    )
    {
        await using var harness = await CreateAsync();
        if (!optOut)
        {
            await using var previous = fixture.CreateDb();
            await previous.PatientLinks.ExecuteUpdateAsync(update => update.SetProperty(x => x.OptOut, true));
        }
        await harness.EnqueueAsync(Guid.NewGuid().ToString(), command);
        await harness.ProcessAsync();
        Assert.Empty(harness.Requests);
        Assert.Single(harness.Sent);
        await using var db = fixture.CreateDb();
        var link = await db.PatientLinks.SingleAsync();
        Assert.Equal(optOut, link.OptOut);
        Assert.Equal(language, link.Language);
        if (optOut)
        {
            await harness.EnqueueAsync(Guid.NewGuid().ToString(), "Hola");
            Assert.True(await harness.ProcessAsync());
            Assert.Single(harness.Requests);
        }
    }

    [Fact]
    public async Task ConcurrentInboundWorkers_RespondOnlyOnce()
    {
        await using var harness = await CreateAsync();
        await harness.EnqueueAsync(Guid.NewGuid().ToString(), "Hola");
        await Task.WhenAll(harness.ProcessAsync(), harness.ProcessAsync());
        Assert.Single(harness.Requests);
        Assert.Single(harness.Sent);
    }

    [Theory]
    [InlineData(503, "Hola", "no está disponible", "agent_AiServiceException")]
    [InlineData(503, "Hello", "temporarily unavailable", "agent_AiServiceException")]
    [InlineData(0, "Hello", "temporarily unavailable", "agent_timeout")]
    public async Task AgentFailure_SendsSafeMessage_AndRetainsCause(
        int agentStatus, string message, string expected, string code)
    {
        await using var harness = await CreateAsync(agentStatus: agentStatus);
        var wamid = Guid.NewGuid().ToString();
        await harness.EnqueueAsync(wamid, message);
        Assert.True(await harness.ProcessAsync());
        Assert.Contains(expected, Assert.Single(harness.Sent));
        await harness.EnqueueAsync(wamid, message);
        Assert.False(await harness.ProcessAsync());
        Assert.Single(harness.Requests);
        await using var db = fixture.CreateDb();
        var input = await db.RawEvents.SingleAsync();
        Assert.Equal(code, input.ErrorCode);
        Assert.NotNull(input.ProcessedAt);
        Assert.Equal(1, input.Attempts);
        Assert.Equal(1, input.SentFragments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \t")]
    [InlineData("---\n***")]
    public async Task EmptyAgentReplyAfterFormatting_SendsSafeMessage(string? reply)
    {
        await using var harness = await CreateAsync(agentReply: reply);
        await harness.EnqueueAsync(Guid.NewGuid().ToString(), "Hola");

        Assert.True(await harness.ProcessAsync());
        Assert.Contains("no está disponible", Assert.Single(harness.Sent));
        await using var db = fixture.CreateDb();
        var input = await db.RawEvents.SingleAsync();
        Assert.Equal("agent_empty_reply", input.ErrorCode);
        Assert.Equal(1, input.SentFragments);
        Assert.NotNull(input.ProcessedAt);
    }

    [Fact]
    public async Task UnlinkedWithoutBinding_SendsAppGuide_WithoutCallingAgent()
    {
        await using var harness = await CreateAsync(linked: false);
        await harness.EnqueueAsync(Guid.NewGuid().ToString(), "hola");

        Assert.True(await harness.ProcessAsync());
        var reply = Assert.Single(harness.Sent);
        Assert.Contains("app ANTARES", reply);
        Assert.DoesNotContain("SMS", reply);
        Assert.Empty(harness.Requests);
        await using var db = fixture.CreateDb();
        Assert.Empty(await db.PatientLinks.ToListAsync());
        Assert.Empty(await db.Conversations.ToListAsync());
        var input = await db.RawEvents.SingleAsync();
        Assert.Equal("{}", input.Payload);
        Assert.NotNull(input.ProcessedAt);
    }

    [Fact]
    public async Task IdentityLookupThrows_SendsSafeReply_WithoutCallingAgent()
    {
        await using var harness = await CreateAsync(linked: false, failLookup: true);
        await harness.EnqueueAsync(Guid.NewGuid().ToString(), "Hello");

        Assert.True(await harness.ProcessAsync());
        Assert.Contains("temporarily unavailable", Assert.Single(harness.Sent));
        Assert.Empty(harness.Requests);
        await using var db = fixture.CreateDb();
        var input = await db.RawEvents.SingleAsync();
        Assert.Equal("preparation_failed", input.ErrorCode);
        Assert.Equal(1, input.SentFragments);
        Assert.NotNull(input.ProcessedAt);
    }

    [Fact]
    public async Task NonTextInbound_ReceivesInstructions_WithoutCallingAgent()
    {
        await using var harness = await CreateAsync();
        await harness.EnqueueAsync(Guid.NewGuid().ToString(), "", type: "image");

        Assert.True(await harness.ProcessAsync());
        Assert.Contains("Envía un mensaje de texto", Assert.Single(harness.Sent));
        Assert.Empty(harness.Requests);
    }

    [Fact]
    public async Task SavedBlankReply_ReceivesFallback_WithoutCallingAgentAgain()
    {
        await using var harness = await CreateAsync();
        await harness.EnqueueAsync(Guid.NewGuid().ToString(), "Hello");
        await using var db = fixture.CreateDb();
        await db.RawEvents.ExecuteUpdateAsync(update => update.SetProperty(x => x.Reply, "---"));

        Assert.True(await harness.ProcessAsync());
        Assert.Contains("temporarily unavailable", Assert.Single(harness.Sent));
        Assert.Empty(harness.Requests);
        Assert.Equal("reply_empty", (await db.RawEvents.SingleAsync()).ErrorCode);
    }

    [Fact]
    public async Task PatientReply_CancelsPendingReminderBeforeAck_AndContinuesAgent()
    {
        await using var harness = await CreateAsync();
        await using var db = fixture.CreateDb();
        db.Reminders.Add(
            new Reminder
            {
                PatientId = harness.PatientId,
                SourceKey = "interaction-test",
                Kind = ReminderKind.checkin,
                ScheduledAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(120),
            }
        );
        await db.SaveChangesAsync();
        await harness.EnqueueAsync(Guid.NewGuid().ToString(), "Hola, me siento bien");
        Assert.Equal(
            ReminderState.Cancelled,
            (await db.Reminders.AsNoTracking().SingleAsync()).State
        );
        await harness.ProcessAsync();
        Assert.Single(harness.Requests);
    }

    [Fact]
    public async Task TransientSendFailure_ReplaysSavedReply_WithoutAnotherAgentCall()
    {
        await using var harness = await CreateAsync(agentStatus: 503);
        harness.SendFailure = WhatsAppSendResult.Failed("429", WhatsAppFailureKind.Transient);
        await harness.EnqueueAsync(Guid.NewGuid().ToString(), "Hola");
        Assert.True(await harness.ProcessAsync());
        await using (var db = fixture.CreateDb())
        {
            var input = await db.RawEvents.SingleAsync();
            Assert.Null(input.ProcessedAt);
            Assert.Contains("no está disponible", input.Reply);
            Assert.Equal(harness.Clock.Now.AddSeconds(5), input.NextAttemptAt);
        }
        Assert.False(await harness.ProcessAsync());
        harness.Clock.Now = harness.Clock.Now.AddSeconds(5);
        harness.SendFailure = null;
        Assert.True(await harness.ProcessAsync());
        Assert.Contains("no está disponible", Assert.Single(harness.Sent));
        Assert.Single(harness.Requests);
        await using var final = fixture.CreateDb();
        Assert.NotNull((await final.RawEvents.SingleAsync()).ProcessedAt);
    }

    [Fact]
    public async Task PermanentSendFailure_ClosesEvent_AndUnblocksNextMessage()
    {
        await using var harness = await CreateAsync();
        harness.SendFailure = WhatsAppSendResult.Failed("190", WhatsAppFailureKind.Permanent);
        var firstId = Guid.NewGuid().ToString();
        await harness.EnqueueAsync(firstId, "Hola");
        harness.Clock.Now = harness.Clock.Now.AddSeconds(1);
        await harness.EnqueueAsync(Guid.NewGuid().ToString(), "Hola de nuevo");

        Assert.True(await harness.ProcessAsync());
        await using var db = fixture.CreateDb();
        var failed = await db.RawEvents.SingleAsync(x => x.Wamid == firstId);
        Assert.Equal("190", failed.ErrorCode);
        Assert.NotNull(failed.ProcessedAt);
        Assert.Null(failed.LeaseUntil);
        Assert.Equal(1, failed.Attempts);
        Assert.Equal(0, failed.SentFragments);
        harness.SendFailure = null;
        Assert.True(await harness.ProcessAsync());
        Assert.False(await harness.ProcessAsync());
        Assert.Single(harness.Sent);
    }

    [Fact]
    public async Task RepeatedTransientFailures_UseBackoff_AndStopAtFiveAttempts()
    {
        await using var harness = await CreateAsync();
        harness.SendFailure = WhatsAppSendResult.Failed("network", WhatsAppFailureKind.Transient);
        await harness.EnqueueAsync(Guid.NewGuid().ToString(), "Hola");

        foreach (var seconds in new[] { 5, 10, 20, 40 })
        {
            Assert.True(await harness.ProcessAsync());
            await using var db = fixture.CreateDb();
            var input = await db.RawEvents.SingleAsync();
            Assert.Equal(harness.Clock.Now.AddSeconds(seconds), input.NextAttemptAt);
            Assert.Null(input.ProcessedAt);
            Assert.False(await harness.ProcessAsync());
            harness.Clock.Now = input.NextAttemptAt;
        }

        Assert.True(await harness.ProcessAsync());
        harness.Clock.Now = harness.Clock.Now.AddHours(1);
        Assert.False(await harness.ProcessAsync());
        await using var final = fixture.CreateDb();
        var exhausted = await final.RawEvents.SingleAsync();
        Assert.Equal(5, exhausted.Attempts);
        Assert.NotNull(exhausted.ProcessedAt);
        Assert.Equal("network", exhausted.ErrorCode);
        Assert.Single(harness.Requests);
        Assert.Equal(5, harness.SendAttempts);
    }

    [Fact]
    public async Task ExpiredFifthLease_ClosesWithoutASixthAttempt_AndUnblocksNextMessage()
    {
        await using var harness = await CreateAsync();
        var exhaustedId = Guid.NewGuid().ToString();
        await harness.EnqueueAsync(exhaustedId, "Hola");
        var lease = harness.Clock.Now.AddMinutes(5);
        await using var db = fixture.CreateDb();
        await db.RawEvents.Where(x => x.Wamid == exhaustedId).ExecuteUpdateAsync(update => update
            .SetProperty(x => x.Attempts, 5)
            .SetProperty(x => x.LeaseUntil, lease));
        harness.Clock.Now = harness.Clock.Now.AddSeconds(1);
        await harness.EnqueueAsync(Guid.NewGuid().ToString(), "Hola de nuevo");
        Assert.False(await harness.ProcessAsync());

        harness.Clock.Now = lease;
        Assert.True(await harness.ProcessAsync());
        Assert.Empty(harness.Requests);
        Assert.Equal(0, harness.SendAttempts);
        var exhausted = await db.RawEvents.SingleAsync(x => x.Wamid == exhaustedId);
        Assert.Equal(5, exhausted.Attempts);
        Assert.Equal("attempts_exhausted", exhausted.ErrorCode);
        Assert.Equal(lease, exhausted.ProcessedAt);
        Assert.Null(exhausted.LeaseUntil);

        Assert.True(await harness.ProcessAsync());
        Assert.False(await harness.ProcessAsync());
        Assert.Single(harness.Sent);
    }

    private async Task<Harness> CreateAsync(
        int agentStatus = 200, string? agentReply = "Respuesta del agente",
        bool linked = true, bool failLookup = false, bool activeLink = true)
    {
        await using var db = fixture.CreateDb();
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM whatsapp.reminder_deliveries; DELETE FROM whatsapp.reminders; DELETE FROM whatsapp.raw_events; DELETE FROM whatsapp.conversations; DELETE FROM whatsapp.patient_links;",
            CancellationToken.None
        );
        var harness = new Harness();
        if (linked) db.PatientLinks.Add(
            new PatientLink
            {
                PatientId = harness.PatientId,
                UserId = harness.UserId,
                WaId = "+573053924819",
                FirstName = "Ana",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            }
        );
        await db.SaveChangesAsync();
        harness.AiHost = await new HostBuilder()
            .ConfigureWebHost(web =>
                web.UseTestServer()
                    .Configure(app =>
                        app.Run(async context =>
                        {
                            Assert.Equal(
                                "test-internal",
                                context.Request.Headers["X-Internal-Key"]
                            );
                            if (context.Request.Method == "GET")
                            {
                                var owner = context.Request.Query["user_id"].ToString();
                                Assert.Equal("true", context.Request.Query["supports_vita"].ToString());
                                var previous = Assert.Single(harness.Requests);
                                var previousThread = previous.GetProperty("thread_id").GetString();
                                Assert.Equal(previous.GetProperty("user_id").GetString(), owner);
                                Assert.Equal($"/api/v1/threads/{previousThread}/state", context.Request.Path.Value);
                                await context.Response.WriteAsJsonAsync(new
                                {
                                    thread_id = previousThread,
                                    message_count = 1,
                                    messages = new[] { new { role = "user", text = previous.GetProperty("message").GetString(),
                                        channel = "whatsapp", provider_message_id = previous.GetProperty("channel_message_id").GetString() } },
                                });
                                return;
                            }
                            var streaming = context.Request.Path == "/api/v1/chat/stream";
                            Assert.Equal(streaming ? "/api/v1/chat/stream" : "/api/v1/chat", context.Request.Path.Value);
                            using var payload = await JsonDocument.ParseAsync(context.Request.Body);
                            var turn = payload.RootElement.Clone();
                            var prior = harness.Requests.LastOrDefault();
                            harness.Requests.Add(turn);
                            if (agentStatus == 0)
                            {
                                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
                                return;
                            }
                            if (agentStatus != 200)
                            {
                                context.Response.StatusCode = agentStatus;
                                await context.Response.WriteAsync("{\"detail\":\"offline\"}");
                                return;
                            }
                            string? answer = agentReply;
                            if (prior.ValueKind == JsonValueKind.Object
                                && prior.TryGetProperty("channel", out _) != turn.TryGetProperty("channel", out _))
                            {
                                Assert.Equal(prior.GetProperty("thread_id").GetString(), turn.GetProperty("thread_id").GetString());
                                Assert.Equal(prior.GetProperty("user_id").GetString(), turn.GetProperty("user_id").GetString());
                                answer = $"Conservé el contexto: {prior.GetProperty("message").GetString()}";
                            }
                            var response = new
                            {
                                answer,
                                thread_id = turn.GetProperty("thread_id").GetString(),
                                execution_id = "test",
                            };
                            if (streaming)
                            {
                                context.Response.ContentType = "text/event-stream";
                                await context.Response.WriteAsync($"event: done\ndata: {JsonSerializer.Serialize(response)}\n\n");
                            }
                            else
                                await context.Response.WriteAsJsonAsync(response);
                        })
                    )
            )
            .StartAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<ChatCommand>());
        services.AddSingleton<TimeProvider>(harness.Clock);
        services.AddSingleton(WebhookTests.Options);
        services.AddSingleton<WhatsAppQueue>();
        services.AddSingleton(Substitute.For<IAgentCatalogRepository>());
        services.AddSingleton(Substitute.For<IAgentRuntimeSyncService>());
        services.AddSingleton(Substitute.For<IProgramControlRepository>());
        var patients = Substitute.For<IPatientChannelQuery>();
        patients.IsActiveLinkAsync(harness.PatientId, harness.UserId, Arg.Any<CancellationToken>())
            .Returns(activeLink);
        if (failLookup)
            patients.FindActiveByPhoneAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<PatientChannelIdentity?>(new InvalidOperationException("detalle interno")));
        services.AddSingleton(patients);
        services.AddSingleton<IOptions<ProgramControlSettings>>(
            Options.Create(new ProgramControlSettings())
        );
        services.AddSingleton<IOptions<AiServiceSettings>>(
            Options.Create(
                new AiServiceSettings
                {
                    BaseUrl = "http://localhost",
                    InternalApiKey = "test-internal",
                    TimeoutSeconds = agentStatus == 0 ? 1 : 30,
                }
            )
        );
        services.AddHttpClient<IAiServiceClient, AiServiceClient>()
            .ConfigurePrimaryHttpMessageHandler(() => harness.AiHost.GetTestServer().CreateHandler())
            .AddResiliencePolicy();
        var sender = Substitute.For<IWhatsAppMessageSender>();
        sender
            .SendTextAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(call =>
            {
                harness.SendAttempts++;
                if (harness.SendFailure is { } failure
                    && (harness.FailOnSendAttempt is null || harness.FailOnSendAttempt == harness.SendAttempts))
                    return failure;
                harness.Sent.Add(call.ArgAt<string>(1));
                return new WhatsAppSendResult("wamid.reply");
            });
        services.AddSingleton(sender);
        services.AddDbContext<WhatsAppDbContext>(options =>
            options.UseNpgsql(fixture.ConnectionString)
        );
        services.AddScoped<WhatsAppPatientResolver>();
        services.AddScoped<WhatsAppWebhook>();
        services.AddScoped<WhatsAppInboundProcessor>();
        services.AddSingleton(new ReminderSettings());
        services.AddSingleton(Substitute.For<IProgramAccompanimentQuery>());
        services.AddScoped<WhatsAppReminderScheduler>();
        services.AddScoped<IWhatsAppReminderHooks, WhatsAppReminderHooks>();
        harness.Services = services.BuildServiceProvider();
        return harness;
    }

    private sealed class Harness : IAsyncDisposable
    {
        public Guid PatientId { get; } = Guid.NewGuid();
        public Guid UserId { get; } = Guid.NewGuid();
        public ManualClock Clock { get; } = new()
        {
            Now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
        };
        public IHost AiHost { get; set; } = null!;
        public ServiceProvider Services { get; set; } = null!;
        public List<JsonElement> Requests { get; } = [];
        public List<string> Sent { get; } = [];
        public WhatsAppSendResult? SendFailure { get; set; }
        public int? FailOnSendAttempt { get; set; }
        public int SendAttempts { get; set; }

        public async Task EnqueueAsync(string wamid, string text, string type = "text")
        {
            using var scope = Services.CreateScope();
            var body = Encoding.UTF8.GetBytes(WebhookTests.Payload(wamid, text, type: type));
            var signature =
                "sha256="
                + Convert.ToHexStringLower(
                    HMACSHA256.HashData(
                        Encoding.UTF8.GetBytes(WebhookTests.Options.AppSecret),
                        body
                    )
                );
            Assert.Equal(
                200,
                await scope
                    .ServiceProvider.GetRequiredService<WhatsAppWebhook>()
                    .AcceptAsync(body, signature, CancellationToken.None)
            );
        }

        public async Task<bool> ProcessAsync()
        {
            using var scope = Services.CreateScope();
            return await scope
                .ServiceProvider.GetRequiredService<WhatsAppInboundProcessor>()
                .ProcessNextAsync(CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            AiHost.Dispose();
        }
    }
}

[Trait("Category", "Unit")]
public sealed class LanguageTests
{
    [Theory]
    [InlineData("Hello, how are you?", "es", "en")]
    [InlineData("I feel better", "en", "en")]
    [InlineData("Hola, me siento mejor", "en", "es")]
    [InlineData("ok", "en", "en")]
    [InlineData("ok", "es", "es")]
    public void Language_ChangesOnlyWithClearSignal(
        string text,
        string previous,
        string expected
    ) => Assert.Equal(expected, TurnLanguage.Detect(text, previous));
}
