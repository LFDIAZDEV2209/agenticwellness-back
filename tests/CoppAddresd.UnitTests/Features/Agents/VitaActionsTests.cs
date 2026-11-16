using CoppAddresd.Api.Security;
using CoppAddresd.Application.DTOs.ProgramProgress;
using CoppAddresd.Application.Features.Agents.Vita;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Domain.Exceptions;
using MediatR;
using System.Security.Cryptography;
using NSubstitute;

namespace CoppAddresd.UnitTests.Features.Agents;

public sealed class VitaActionsTests
{
    private readonly IVitaProposalTokens tokens = new VitaProposalTokens(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    private readonly IVitaTaskWriter writer = Substitute.For<IVitaTaskWriter>();
    private readonly IPatientRepository patients = Substitute.For<IPatientRepository>();
    private readonly ILegalConsentService consent = Substitute.For<ILegalConsentService>();
    private readonly ISender sender = Substitute.For<ISender>();
    private readonly Guid userId = Guid.NewGuid();
    private readonly TimeProvider clock = Substitute.For<TimeProvider>();
    private readonly DateTimeOffset now = new(2026, 11, 14, 14, 0, 0, TimeSpan.Zero);
    private VitaTaskProposal Proposal => new(Guid.NewGuid(), userId, Guid.NewGuid(), TaskCode.podcast,
        new(2026, 11, 14), new string('a', 64), now.AddMinutes(5));
    private ConfirmVitaTaskCommandHandler Handler => new(new(patients, consent, clock), sender, tokens, writer, clock);
    public VitaActionsTests()
    {
        clock.GetUtcNow().Returns(now);
        patients.GetByUserIdAsync(userId, Arg.Any<CancellationToken>()).Returns(
            new PatientProfile { Id = Guid.NewGuid(), UserId = userId, Status = "Activo" });
    }

    [Fact]
    public async Task SinConfirmacion_NoConsultaNiEscribe()
    {
        await Assert.ThrowsAsync<ForbiddenException>(() => Handler.Handle(new(userId,
            new(tokens.Protect(Proposal), false)), default));
        Assert.Empty(writer.ReceivedCalls()); Assert.Empty(patients.ReceivedCalls());
    }

    [Fact]
    public async Task PropuestaDeOtroUsuario_NoEscribe()
    {
        await Assert.ThrowsAsync<ForbiddenException>(() => Handler.Handle(new(Guid.NewGuid(),
            new(tokens.Protect(Proposal), true)), default));
        Assert.Empty(writer.ReceivedCalls());
    }

    [Fact]
    public async Task PropuestaExpirada_NoEscribe()
    {
        await Assert.ThrowsAsync<BusinessRuleViolationException>(() => Handler.Handle(new(userId,
            new(tokens.Protect(Proposal with { ExpiresAt = now }), true)), default));
        Assert.Empty(writer.ReceivedCalls());
    }

    [Fact]
    public void TokenManipulado_ODeOtroProtector_NoSeAcepta()
    {
        var token = tokens.Protect(Proposal);
        Assert.Throws<ForbiddenException>(() => tokens.Unprotect(token[..^8] + "tampered"));
        var other = new VitaProposalTokens(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        Assert.Throws<ForbiddenException>(() => other.Unprotect(token));
    }

    [Theory]
    [InlineData(TaskCode.vitals)]
    [InlineData(TaskCode.emocional)]
    [InlineData(TaskCode.nut)]
    [InlineData(TaskCode.nutraceutico)]
    public async Task NingunRegistroClinicoSeCompletaPorInferencia(TaskCode code)
    {
        await Assert.ThrowsAsync<ForbiddenException>(() => Handler.Handle(new(userId,
            new(tokens.Protect(Proposal with { TaskCode = code }), true)), default));
        Assert.Empty(writer.ReceivedCalls());
    }

    [Fact]
    public async Task DobleConfirmacion_ConservaLaIdentidadIdempotenteDeLaPropuesta()
    {
        var proposal = Proposal;
        writer.CompleteAsync(Arg.Any<VitaTaskProposal>(), Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(new CompleteTaskResult(CompleteTaskOutcome.Replay, Guid.NewGuid(), 0, 50, false, 0, 0, 0, 0, 100));
        var command = new ConfirmVitaTaskCommand(userId, new(tokens.Protect(proposal), true));
        await Handler.Handle(command, default); await Handler.Handle(command, default);
        await writer.Received(2).CompleteAsync(proposal, Arg.Any<Func<CancellationToken, Task>>(), default);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("stale")]
    [InlineData("ok")]
    public void FechaAjenaORevisionVieja_NoSeEjecuta(string status)
    {
        var context = new VitaContextDto(status, now, new DateOnly(2026, 11, 13), "v",
            VitaVersions.Json(new { rest = false }), [], []);
        Assert.Throws<BusinessRuleViolationException>(() => VitaCompletionValidation.Require(context,
            new(2026, 11, 14), TaskCode.podcast));
    }

    [Fact]
    public void Descanso_NoPuedeCompletarEjercicio()
    {
        var context = new VitaContextDto("ok", now, new DateOnly(2026, 11, 14), "v",
            VitaVersions.Json(new { rest = true }), [], []);
        Assert.Throws<BusinessRuleViolationException>(() => VitaCompletionValidation.Require(context,
            new(2026, 11, 14), TaskCode.ejercicio));
    }
}
