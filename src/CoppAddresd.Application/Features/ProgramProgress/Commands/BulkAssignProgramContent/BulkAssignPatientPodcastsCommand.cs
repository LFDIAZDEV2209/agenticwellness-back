using CoppAddresd.Application.Features.Media;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Domain.Exceptions;
using FluentValidation;
using MediatR;

namespace CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent;

public sealed record BulkAssignPatientPodcastsCommand(IReadOnlyList<Guid> PatientIds, Guid MediaId,
    int SemanaDesde, int SemanaHasta, bool Simulate = false) : IRequest<BulkAssignPatientPodcastsResult>;
public sealed record BulkAssignPatientPodcastsResult(int Aplicadas, int OmitidasSemanaIniciada, IReadOnlyList<Guid> OmitidasSinAlcance);
public interface IBulkPatientPodcastStore
{
    Task<BulkAssignPatientPodcastsResult> AssignAsync(BulkAssignPatientPodcastsCommand request, CancellationToken ct);
}
public sealed class BulkAssignPatientPodcastsValidator : AbstractValidator<BulkAssignPatientPodcastsCommand>
{
    public BulkAssignPatientPodcastsValidator()
    {
        RuleFor(x => x.PatientIds).NotNull().Must(x => x is { Count: >= 1 and <= 100 })
            .WithMessage("Se requieren de 1 a 100 pacientes.");
        RuleForEach(x => x.PatientIds).NotEmpty();
        RuleFor(x => x.MediaId).NotEmpty();
        RuleFor(x => x.SemanaDesde).InclusiveBetween(1, 83);
        RuleFor(x => x.SemanaHasta).InclusiveBetween(1, 83).GreaterThanOrEqualTo(x => x.SemanaDesde);
    }
}
public sealed class BulkAssignPatientPodcastsHandler(IBulkPatientPodcastStore store)
    : IRequestHandler<BulkAssignPatientPodcastsCommand, BulkAssignPatientPodcastsResult>
{
    public Task<BulkAssignPatientPodcastsResult> Handle(BulkAssignPatientPodcastsCommand request, CancellationToken ct) => store.AssignAsync(request, ct);
}

public sealed record PatientPodcastWeekUpdate(ProgramWeek Week, System.Text.Json.JsonElement Snapshot, bool IsNew);
public sealed record PatientPodcastPlan(BulkAssignPatientPodcastsResult Result, IReadOnlyList<PatientPodcastWeekUpdate> Updates);

/// <summary>Plan puro: omite iniciadas, personaliza solo podcasts y conserva las demás tareas y sus metadatos.</summary>
public static class BulkPatientPodcastPlanner
{
    public static PatientPodcastPlan Build(BulkAssignPatientPodcastsCommand request, MediaItem media,
        IReadOnlyCollection<Guid> visiblePatients, IReadOnlyList<ProgramEnrollment> enrollments, IReadOnlyList<ProgramWeek> weeks,
        Guid actorId, DateTimeOffset now, CancellationToken ct)
    {
        if (media.Status != MediaStatus.Published || media.MediaType != MediaType.Podcast || string.IsNullOrWhiteSpace(media.StorageKey)
            || MediaPublishRules.IsPlaceholderStorageKey(media.StorageKey))
            throw new UnprocessableEntityException("PODCAST_NOT_PUBLISHED: se requiere un podcast publicado con archivo válido.");
        return BulkPatientContentPlanner.Build(request.PatientIds, media.Id, request.SemanaDesde, request.SemanaHasta,
            "podcast", visiblePatients, enrollments, weeks, actorId, now, ct);
    }
}
