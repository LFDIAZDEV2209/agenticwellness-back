using System.Security.Cryptography;
using CoppAddresd.Api.Security;
using CoppAddresd.Application.Features.Agents.Vita;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Domain.Exceptions;
using FluentValidation;

namespace CoppAddresd.UnitTests.Features.Agents;

public sealed class VitaCaptureTokenTests
{
    [Fact]
    public void CaptureAndTaskTokens_AreBoundToDifferentPurposes()
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var captureTokens = VitaProposalTokens.ForCapture(secret);
        var taskTokens = new VitaProposalTokens(secret);
        var now = DateTimeOffset.UtcNow;
        var user = Guid.NewGuid();
        var capture = new VitaCaptureProposal(Guid.NewGuid(), user, "vita_voice", "turn",
            new("record_measurement", "weight", 74.5m, "kg", now, null, null, null, null),
            now.AddMinutes(5), Guid.NewGuid());
        var task = new VitaTaskProposal(Guid.NewGuid(), user, Guid.NewGuid(), TaskCode.podcast,
            DateOnly.FromDateTime(now.UtcDateTime), "test-version", now.AddMinutes(5));
        var captureToken = captureTokens.Protect(capture);
        var taskToken = taskTokens.Protect(task);
        Assert.Equal(capture, captureTokens.Unprotect(captureToken));
        Assert.Equal(task, taskTokens.Unprotect(taskToken));
        Assert.Throws<ValidationException>(() => captureTokens.Unprotect(taskToken));
        Assert.Throws<ForbiddenException>(() => taskTokens.Unprotect(captureToken));
        Assert.NotEqual(captureToken, captureTokens.Protect(capture));
    }

    [Fact]
    public void CaptureSecret_RejectsInvalidLengthAndEquivalentBase64()
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var other = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Assert.False(VitaProposalTokens.DedicatedSecret(secret, " " + secret));
        Assert.True(VitaProposalTokens.DedicatedSecret(secret, other));
        Assert.Throws<InvalidOperationException>(() => VitaProposalTokens.ForCapture("bad-key"));
    }
}
