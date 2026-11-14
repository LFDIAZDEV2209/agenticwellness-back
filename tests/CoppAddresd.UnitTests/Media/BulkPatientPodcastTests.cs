using CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Domain.Exceptions;

namespace CoppAddresd.UnitTests.Media;

public sealed class BulkPatientPodcastTests
{
    [Theory]
    [InlineData(0, 1, 1)] [InlineData(101, 1, 1)] [InlineData(1, 0, 1)] [InlineData(1, 2, 1)] [InlineData(1, 1, 84)]
    public void ValidaLimites(int patients, int from, int to) => Assert.False(new BulkAssignPatientPodcastsValidator().Validate(
        new BulkAssignPatientPodcastsCommand(Enumerable.Range(0, patients).Select(_ => Guid.NewGuid()).ToArray(), Guid.NewGuid(), from, to)).IsValid);

    [Fact]
    public void UltimaSemana_SeisDiasIndividualSinDia84_YNoExigePlantillaActiva()
    {
        var (enrollment, media, now) = Seed(); var actor = Guid.NewGuid();
        var plan = BulkPatientPodcastPlanner.Build(new([enrollment.PatientId], media.Id, 12, 12), media, [enrollment.PatientId], [enrollment], [], actor, now, CancellationToken.None);
        Assert.Equal(1, plan.Result.Aplicadas);
        var rows = WeekSnapshotJson.Parse(Assert.Single(plan.Updates).Snapshot);
        Assert.Equal(6, rows.Count); Assert.DoesNotContain(rows, r => r.Weekday == 7);
        Assert.All(rows, r => { Assert.Equal("individual", r.Origin); Assert.Equal(actor, r.EditedBy); Assert.Equal(media.Id, r.MediaId); });
    }

    [Fact]
    public void RangoMixto_UnPacientePuedeAplicarYConservarSemanaIniciada()
    {
        var (enrollment, media, now) = Seed();
        var plan = BulkPatientPodcastPlanner.Build(new([enrollment.PatientId], media.Id, 1, 2), media, [enrollment.PatientId], [enrollment], [], Guid.NewGuid(), now, CancellationToken.None);
        Assert.Equal(1, plan.Result.Aplicadas); Assert.Equal(1, plan.Result.OmitidasSemanaIniciada); Assert.Single(plan.Updates);
    }

    [Theory]
    [InlineData(MediaStatus.Draft, MediaType.Podcast, "media/real.mp3")]
    [InlineData(MediaStatus.Published, MediaType.Audio, "media/real.mp3")]
    [InlineData(MediaStatus.Published, MediaType.Podcast, "media/podcasts/demo-copp.mp3")]
    public void RechazaMediosNoValidos(MediaStatus status, MediaType type, string key)
    {
        var (enrollment, media, now) = Seed(); media.Status = status; media.MediaType = type; media.StorageKey = key;
        Assert.Throws<UnprocessableEntityException>(() => BulkPatientPodcastPlanner.Build(new([enrollment.PatientId], media.Id, 2, 2),
            media, [enrollment.PatientId], [enrollment], [], Guid.NewGuid(), now, CancellationToken.None));
    }

    [Fact]
    public void PacientePropioSinInscripcion_RechazaSinPlanParcial()
    {
        var (enrollment, media, now) = Seed();
        Assert.Throws<UnprocessableEntityException>(() => BulkPatientPodcastPlanner.Build(new([enrollment.PatientId], media.Id, 2, 2),
            media, [enrollment.PatientId], [], [], Guid.NewGuid(), now, CancellationToken.None));
    }

    private static (ProgramEnrollment, MediaItem, DateTimeOffset) Seed()
    {
        var template = new ProgramTemplate { Id = Guid.NewGuid(), TotalWeeks = 12, Status = TemplateStatus.Archived,
            DayTemplates = Enumerable.Range(1, 7).Select(d => new WeeklyDayTemplate { Weekday = (short)d, TaskCode = TaskCode.podcast, Points = 10, SortOrder = 1 }).ToList() };
        return (new() { Id = Guid.NewGuid(), PatientId = Guid.NewGuid(), Template = template, StartLocalDate = new(2026, 11, 2), Timezone = "America/Bogota" },
            new() { Id = Guid.NewGuid(), MediaType = MediaType.Podcast, Status = MediaStatus.Published, StorageKey = "media/real.mp3" }, new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));
    }
}
