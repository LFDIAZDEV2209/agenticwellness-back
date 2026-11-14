using CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Application.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CoppAddresd.Application.Features.Media;

public enum MediaRetirementOperation { Unpublish, Archive, Delete }
public sealed record MediaRetirementImpact(int Pacientes, int Plantillas);
public sealed record RetireMediaCommand(Guid Id, MediaRetirementOperation Operation, Guid? ReplacementMediaId = null)
    : IRequest<MediaRetirementResult>;
public sealed record GetMediaRetirementImpactQuery(Guid Id) : IRequest<MediaRetirementImpact>;
public sealed record MediaRetirementResult(MediaItemDto Media, MediaRetirementImpact Impact, bool Blocked, bool Deleted = false);
public sealed record MediaReplacementRequest(Guid? ReplacementMediaId);

public sealed record MediaRetirementData(MediaItem Media, MediaItem? Replacement,
    IReadOnlyList<WeeklyDayTemplate> Templates, EffectiveContentData Content, bool HasFutureReferences = false);
public sealed record MediaRetirementPlan(MediaRetirementImpact Impact, bool Blocked,
    IReadOnlyDictionary<Guid, System.Text.Json.JsonElement> Snapshots);

public interface IMediaRetirementStore
{
    Task<MediaRetirementImpact> PreviewAsync(Guid id, CancellationToken ct);
    Task<MediaRetirementResult> ExecuteAsync(RetireMediaCommand request, CancellationToken ct);
}

/// <summary>Regla común: sustituye programa futuro, conserva toda personalización y semana iniciada.</summary>
public static class MediaRetirementPolicy
{
    public static MediaRetirementPlan Plan(MediaRetirementData data, IEffectiveDayContentResolver resolver,
        DateTime now, bool replacing, CancellationToken ct)
    {
        var patients = new HashSet<Guid>(); var snapshots = new Dictionary<Guid, System.Text.Json.JsonElement>();
        var inUse = data.Templates.Count > 0 || data.HasFutureReferences;
        foreach (var enrollment in data.Content.Enrollments)
        {
            ct.ThrowIfCancellationRequested();
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, TimeZoneInfo.FindSystemTimeZoneById(enrollment.Timezone)));
            var end = enrollment.StartLocalDate.AddDays(EffectiveDayContentResolver.DurationDays(enrollment.Template!) - 1);
            var futureWeeks = data.Content.Weeks.Where(w => w.EnrollmentId == enrollment.Id && !TemplateImpactCalculator.Started(w, today)).ToArray();
            var futureDays = resolver.ResolveRange(data.Content, enrollment, today.AddDays(1), end, ct)
                .Where(d => enrollment.StartLocalDate.AddDays((d.WeekNumber - 1) * 7) > today
                    && !data.Content.Weeks.Any(w => w.EnrollmentId == enrollment.Id && w.WeekNumber == d.WeekNumber && TemplateImpactCalculator.Started(w, today)));
            if (futureDays.Any(d => d.Podcast.Id == data.Media.Id)) { patients.Add(enrollment.PatientId); inUse = true; }
            foreach (var week in futureWeeks)
            {
                var rows = WeekSnapshotJson.Parse(week.TasksSnapshot);
                if (!rows.Any(r => r.MediaId == data.Media.Id)) continue;
                inUse = true; patients.Add(enrollment.PatientId);
                if (replacing && rows.Any(r => r.MediaId == data.Media.Id && r.Origin == "program"))
                    snapshots.Add(week.Id, WeekSnapshotJson.Serialize(rows.Select(r => r.MediaId == data.Media.Id && r.Origin == "program"
                        ? r with { MediaId = data.Replacement!.Id } : r).ToArray()));
            }
        }
        return new(new(patients.Count, data.Templates.Select(t => t.TemplateId).Distinct().Count()), inUse && !replacing, snapshots);
    }

    public static void ValidateReplacement(MediaRetirementData data)
    {
        var replacement = data.Replacement;
        if (replacement is null || replacement.Id == data.Media.Id || replacement.Status != MediaStatus.Published
            || replacement.MediaType != data.Media.MediaType || string.IsNullOrWhiteSpace(replacement.StorageKey)
            || MediaPublishRules.IsPlaceholderStorageKey(replacement.StorageKey))
            throw new UnprocessableEntityException("MEDIA_REPLACEMENT_INVALID: el sustituto debe ser otro medio publicado y del mismo tipo.");
    }
}

public sealed class GetMediaRetirementImpactQueryHandler(IMediaRetirementStore store)
    : IRequestHandler<GetMediaRetirementImpactQuery, MediaRetirementImpact>
{
    public Task<MediaRetirementImpact> Handle(GetMediaRetirementImpactQuery request, CancellationToken ct) => store.PreviewAsync(request.Id, ct);
}

public sealed class RetireMediaCommandHandler(IMediaRetirementStore store, IObjectStorageService storage,
    ILogger<RetireMediaCommandHandler> logger) : IRequestHandler<RetireMediaCommand, MediaRetirementResult>
{
    public async Task<MediaRetirementResult> Handle(RetireMediaCommand request, CancellationToken ct)
    {
        var result = await store.ExecuteAsync(request, ct);
        if (!result.Deleted) return result;
        // Solo después del commit y sin referencias se permite borrar el archivo.
        var keys = new List<string>();
        if (result.Media.SourceKind == "file") keys.Add(result.Media.StorageKey);
        if (!string.IsNullOrWhiteSpace(result.Media.ThumbnailKey) && !Uri.TryCreate(result.Media.ThumbnailKey, UriKind.Absolute, out _)) keys.Add(result.Media.ThumbnailKey);
        try { if (keys.Count > 0) await storage.DeleteObjectsAsync(keys, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger.LogWarning(ex, "Archivo huérfano tras eliminar medio {MediaId}", request.Id); }
        return result;
    }
}
