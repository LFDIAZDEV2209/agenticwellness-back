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
    int SemanaDesde, int SemanaHasta) : IRequest<BulkAssignPatientPodcastsResult>;
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
        if (visiblePatients.Any(p => !enrollments.Any(e => e.PatientId == p)))
            throw new UnprocessableEntityException("ACTIVE_ENROLLMENT_REQUIRED: todos los pacientes propios deben tener inscripción activa.");
        var applied = new HashSet<Guid>(); var started = new HashSet<Guid>(); var updates = new List<PatientPodcastWeekUpdate>();
        var byEnrollment = weeks.ToLookup(w => w.EnrollmentId);
        foreach (var enrollment in enrollments)
        {
            ct.ThrowIfCancellationRequested();
            var template = enrollment.Template!;
            if (request.SemanaHasta > template.TotalWeeks)
                throw new UnprocessableEntityException("PROGRAM_WEEK_OUT_OF_RANGE: rango fuera del programa de un paciente propio.");
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now.UtcDateTime, TimeZoneInfo.FindSystemTimeZoneById(enrollment.Timezone)));
            var last = enrollment.StartLocalDate.AddDays(EffectiveDayContentResolver.DurationDays(template) - 1);
            var existing = byEnrollment[enrollment.Id].ToDictionary(w => w.WeekNumber);
            for (var n = request.SemanaDesde; n <= request.SemanaHasta; n++)
            {
                ct.ThrowIfCancellationRequested();
                var start = enrollment.StartLocalDate.AddDays((n - 1) * 7);
                existing.TryGetValue(n, out var old);
                if (start <= today || old is not null && TemplateImpactCalculator.Started(old, today))
                { started.Add(enrollment.PatientId); continue; }
                applied.Add(enrollment.PatientId);
                var rows = old is null ? [] : WeekSnapshotJson.Parse(old.TasksSnapshot).ToList();
                var weekdays = Enumerable.Range(0, Math.Min(7, last.DayNumber - start.DayNumber + 1))
                    .Select(d => (short)(start.AddDays(d).DayOfWeek == DayOfWeek.Sunday ? 7 : (int)start.AddDays(d).DayOfWeek)).ToArray();
                foreach (var weekday in weekdays)
                {
                    if (rows.Any(r => r.Weekday == weekday && r.TaskCode == "podcast")) continue;
                    var source = template.DayTemplates.Where(d => d.Weekday == weekday && d.TaskCode == CoppAddresd.Domain.Enums.ProgramProgress.TaskCode.podcast
                        && (d.WeekNumber is null || d.WeekNumber == n)).OrderByDescending(d => d.WeekNumber.HasValue).ThenBy(d => d.SortOrder).FirstOrDefault()
                        ?? throw new UnprocessableEntityException("TEMPLATE_PODCAST_DAY_MISSING: falta la tarea podcast de un día en la plantilla.");
                    rows.Add(new(weekday, "podcast", source.Points, source.SortOrder, MediaId: source.MediaId, Origin: "program"));
                }
                var assigned = PodcastSnapshotAssignment.ReplaceMedia(rows, weekdays, media.Id).Select(r =>
                    r.TaskCode == "podcast" && weekdays.Contains(r.Weekday)
                        && (r.Origin != "individual" || r.MediaId != rows.First(x => x.Weekday == r.Weekday && x.TaskCode == "podcast").MediaId)
                        ? r with { Origin = "individual", EditedBy = actorId, EditedAt = now } : r).ToArray();
                if (old is not null && assigned.SequenceEqual(WeekSnapshotJson.Parse(old.TasksSnapshot))) continue;
                var week = old ?? new ProgramWeek { Id = Guid.NewGuid(), EnrollmentId = enrollment.Id, WeekNumber = n,
                    WeekStartDateLocal = start, WeekEndDateLocal = start.AddDays(6), CreatedAt = now.UtcDateTime };
                updates.Add(new(week, WeekSnapshotJson.Serialize(assigned), old is null));
            }
        }
        return new(new(applied.Count, started.Count, request.PatientIds.Distinct().Except(visiblePatients).ToArray()), updates);
    }
}
