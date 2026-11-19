using CoppAddresd.Application.DTOs.Ai;
using CoppAddresd.Application.Features.Chat;
using CoppAddresd.Application.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using CoppAddresd.Application.Features.Agents.Vita;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Api.Controllers;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Text.Json;

namespace CoppAddresd.UnitTests.Chat;

/// <summary>
/// Handler <c>VoiceSessionCommandHandler</c> (sesiones de voz ElevenLabs):
/// invoca al cliente IA con el DTO correcto, propaga el
/// <c>CancellationToken</c>, valida el comando y jamás registra el signed URL
/// (credencial temporal del paciente).
/// </summary>
public sealed class VoiceSessionCommandHandlerTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid PatientId = Guid.NewGuid();
    private static VoiceSessionCommandHandler BuildHandler(IAiServiceClient client)
    {
        var patients = Substitute.For<IPatientRepository>();
        patients.GetByUserIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(new PatientProfile
            { Id = PatientId, UserId = UserId, Status = "Activo" });
        return new(client, NullLogger<VoiceSessionCommandHandler>.Instance,
            new VitaPatientAccess(patients, Substitute.For<ILegalConsentService>(), TimeProvider.System));
    }

    [Theory]
    [InlineData("proactive-paciente-123", false)]
    [InlineData("proactive-paciente-123", true)]
    [InlineData(null, false)]
    [InlineData(null, true)]
    public async Task Handle_InvocaAlClienteConDtoYUserIdYPropagaCt(string? threadId, bool supportsVita)
    {
        // Arrange.
        var client = Substitute.For<IAiServiceClient>();
        VoiceSessionInternalRequest? capturedDto = null;
        CancellationToken capturedCt = default;
        var expected = new VoiceSessionResponseDto(
            "wss://signed.example/one-time",
            "agent_4501m3qqzq0ne7qtpcf3p2wkec1a",
            "conv_123"
        );
        client
            .CreateVoiceSessionAsync(
                Arg.Any<VoiceSessionInternalRequest>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(callInfo =>
            {
                capturedDto = callInfo.Arg<VoiceSessionInternalRequest>();
                capturedCt = callInfo.Arg<CancellationToken>();
                return expected;
            });
        var handler = BuildHandler(client);
        using var cts = new CancellationTokenSource();

        // Act.
        var result = await handler.Handle(
            new VoiceSessionCommand(UserId.ToString(), "p-1", threadId, SupportsVitaVoice: supportsVita),
            cts.Token
        );

        // Assert.
        Assert.Equal(expected, result);
        Assert.NotNull(capturedDto);
        Assert.Equal(UserId.ToString(), capturedDto!.UserId);
        Assert.Equal(PatientId.ToString(), capturedDto.PatientId);
        Assert.Equal(threadId ?? (supportsVita ? $"proactive-{UserId:D}" : null), capturedDto.ThreadId);
        Assert.Equal(cts.Token, capturedCt);
        await client
            .Received(1)
            .CreateVoiceSessionAsync(Arg.Any<VoiceSessionInternalRequest>(), cts.Token);
    }

    [Fact]
    public void Validator_UserIdVacio_Falla()
    {
        var validator = new VoiceSessionCommandValidator();
        var result = validator.Validate(new VoiceSessionCommand("", null, null));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("Usuario no identificado"));
    }

    [Fact]
    public void Validator_ThreadOpcional_Pasa()
    {
        var validator = new VoiceSessionCommandValidator();
        var result = validator.Validate(new VoiceSessionCommand("u1", "p1", "proactive-u-1"));
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validator_ThreadMuyLargo_Falla()
    {
        var validator = new VoiceSessionCommandValidator();
        var result = validator.Validate(new VoiceSessionCommand("u1", null, new string('x', 201)));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("ThreadId"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Controller_ClienteAntiguoONuevo_PreservaCapacidadEnComando(bool supports)
    {
        var body = supports ? """{"threadId":"thread","supportsVitaVoice":true}""" : """{"threadId":"thread"}""";
        var input = JsonSerializer.Deserialize<VoiceSessionRequestDto>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<VoiceSessionCommand>(), Arg.Any<CancellationToken>())
            .Returns(new VoiceSessionResponseDto("wss://synthetic", "agent", null, ThreadId: "thread", SessionId: "session"));
        var controller = new ChatController(mediator, NullLogger<ChatController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext {
            User = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, UserId.ToString())], "test")) } } };
        var result = await controller.CreateVoiceSession(input, default);
        Assert.IsType<OkObjectResult>(result.Result);
        await mediator.Received(1).Send(Arg.Is<VoiceSessionCommand>(c => c.SupportsVitaVoice == supports), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handler_PropagaCapacidadSinForzarMotorVita(bool supports)
    {
        var client = Substitute.For<IAiServiceClient>();
        client.CreateVoiceSessionAsync(Arg.Any<VoiceSessionInternalRequest>(), Arg.Any<CancellationToken>())
            .Returns(new VoiceSessionResponseDto("wss://synthetic", "agent", null, ThreadId: "thread", SessionId: "session"));
        var result = await BuildHandler(client).Handle(new(UserId.ToString(), null, "thread", SupportsVitaVoice: supports), default);
        Assert.Equal("legacy", result.RuntimeMode);
        await client.Received(1).CreateVoiceSessionAsync(Arg.Is<VoiceSessionInternalRequest>(r =>
            r.SupportsVitaVoice == supports && r.PatientId == PatientId.ToString()), Arg.Any<CancellationToken>());
    }
}
