using CoppAddresd.Application.Features.Media;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Domain.Exceptions;
using NSubstitute;

namespace CoppAddresd.UnitTests.Media;

public sealed class MediaRetirementTests
{
    [Theory]
    [InlineData(MediaStatus.Draft, MediaType.Podcast)]
    [InlineData(MediaStatus.Published, MediaType.Video)]
    public void Sustituto_RechazaEstadoOTipoIncompatible(MediaStatus status, MediaType type)
    {
        var original = new MediaItem { Id = Guid.NewGuid(), MediaType = MediaType.Podcast };
        var substitute = new MediaItem { Id = Guid.NewGuid(), Status = status, MediaType = type, StorageKey = "media/real.mp3" };
        Assert.Throws<UnprocessableEntityException>(() => MediaRetirementPolicy.ValidateReplacement(new(original, substitute, [], new())));
    }
    [Fact]
    public void Plantillas_ConteoDistintoYSinSustitutoBloquea()
    {
        var id = Guid.NewGuid(); var media = new MediaItem { Id = Guid.NewGuid() };
        var plan = MediaRetirementPolicy.Plan(new(media, null, [new() { TemplateId = id }, new() { TemplateId = id }], new()),
            Substitute.For<IEffectiveDayContentResolver>(), DateTime.UtcNow, false, CancellationToken.None);
        Assert.True(plan.Blocked); Assert.Equal(new MediaRetirementImpact(0, 1), plan.Impact);
    }
}
