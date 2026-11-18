using CoppAddresd.Application.Common;
using CoppAddresd.Application.Features.Chat;
using CoppAddresd.Application.WhatsApp.Messaging;
using CoppAddresd.Domain.Entities.WhatsApp;
using CoppAddresd.WhatsApp.Messaging;
using CoppAddresd.WhatsApp.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CoppAddresd.WhatsApp.Tests;

[Trait("Category", "Unit")]
public sealed class InboundProcessorTests
{
    [Theory]
    [InlineData("es", "Tu asistente no está disponible en este momento. Inténtalo de nuevo en unos minutos.", "agent_AiServiceException")]
    [InlineData("en", "Your assistant is temporarily unavailable. Please try again in a few minutes.", "agent_AiServiceException")]
    [InlineData("en", "Your assistant is temporarily unavailable. Please try again in a few minutes.", "agent_unexpected_error")]
    public async Task AgentException_PreparesOnlyTheLocalizedFallback(string language, string expected, string code)
    {
        var mediator = Substitute.For<ISender>();
        Exception failure = code == "agent_AiServiceException"
            ? new AiServiceException(503, "detalle interno")
            : new InvalidOperationException("detalle interno");
        mediator.Send(Arg.Any<ChatCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ChatResult>(failure));
        await using var db = new WhatsAppDbContext(new DbContextOptionsBuilder<WhatsAppDbContext>().Options);
        var processor = CreateProcessor(db, mediator);
        var input = new WhatsAppRawEvent { Text = "mensaje de prueba" };
        var conversation = new Conversation { ThreadId = "original-thread" };

        await processor.PrepareAgentReplyAsync(input, conversation, new PatientLink(), language, CancellationToken.None);

        Assert.Equal(expected, input.Reply);
        Assert.Equal(code, input.ErrorCode);
        Assert.Equal("original-thread", conversation.ThreadId);
    }

    [Fact]
    public async Task CancelledCaller_PropagatesCancellation_WithoutPreparingFallback()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var mediator = Substitute.For<ISender>();
        mediator.Send(Arg.Any<ChatCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromCanceled<ChatResult>(call.Arg<CancellationToken>()));
        await using var db = new WhatsAppDbContext(new DbContextOptionsBuilder<WhatsAppDbContext>().Options);
        var processor = CreateProcessor(db, mediator);
        var input = new WhatsAppRawEvent();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            processor.PrepareAgentReplyAsync(input, new Conversation(), new PatientLink(), "es", cts.Token));

        Assert.Null(input.Reply);
        Assert.Null(input.ErrorCode);
    }

    private static WhatsAppInboundProcessor CreateProcessor(WhatsAppDbContext db, ISender mediator)
    {
        var sender = Substitute.For<IWhatsAppMessageSender>();
        var clock = TimeProvider.System;
        return new WhatsAppInboundProcessor(
            db, new WhatsAppPatientResolver(db, mediator, sender, clock), mediator, sender, clock,
            NullLogger<WhatsAppInboundProcessor>.Instance);
    }
}
