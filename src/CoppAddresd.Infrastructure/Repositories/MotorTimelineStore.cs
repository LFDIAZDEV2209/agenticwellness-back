using CoppAddresd.Application.Features.Motor365;
using CoppAddresd.Domain.Wellness365;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

public sealed class MotorTimelineStore(AppDbContext db) : IMotorTimelineStore
{
    private const int PageSize = 40;

    public async Task<MotorTimelinePage> ReadAsync(Guid patientId, MotorTimelineFilter filter, int page, CancellationToken ct)
    {
        var rows = await Timeline(patientId, filter).Skip((page - 1) * PageSize).Take(PageSize + 1).ToArrayAsync(ct);
        return new(rows.Take(PageSize).ToArray(), page, rows.Length > PageSize);
    }

    public async Task<IReadOnlyList<MotorTimelineEvent>> ReadForExportAsync(Guid patientId, MotorTimelineFilter filter, CancellationToken ct) =>
        await Timeline(patientId, filter).ToArrayAsync(ct);

    public async Task AuditExportAsync(Guid patientId, Guid actorId, DateTimeOffset occurredAt, CancellationToken ct)
    {
        db.Motor365AuditLog.Add(new Motor365AuditLog
        {
            PersonId = patientId, ActorUserId = actorId, Action = "timeline.export", OccurredAt = occurredAt,
        });
        await db.SaveChangesAsync(ct);
    }

    private IQueryable<MotorTimelineEvent> Timeline(Guid patientId, MotorTimelineFilter filter)
    {
        var events = db.Motor365EngineEvents.AsNoTracking().Where(row => row.PersonId == patientId);
        // Una revisión republica decisiones previas. Conservar una por familia y día;
        // "sin" y "ajuste" son dos resultados de la misma decisión semanal.
        var latest = events.Where(row => row.Id == events.Where(candidate => candidate.Day == row.Day &&
                (candidate.Kind == row.Kind || ((row.Kind == "sin" || row.Kind == "ajuste") &&
                    (candidate.Kind == "sin" || candidate.Kind == "ajuste"))))
            .OrderByDescending(candidate => candidate.PlanVersion).ThenByDescending(candidate => candidate.ParamsVersion)
            .ThenByDescending(candidate => candidate.OccurredAt).ThenByDescending(candidate => candidate.Id)
            .Select(candidate => candidate.Id).First());

        // Aplicar filtros tras resolver la revisión evita mostrar una decisión sustituida.
        if (filter.Kind is { } kind) latest = latest.Where(row => row.Kind == kind);
        if (filter.From is { } from)
        {
            var inclusive = UtcStartOf(from);
            latest = latest.Where(row => row.OccurredAt >= inclusive);
        }
        if (filter.To is { } to && to < DateOnly.MaxValue)
        {
            var exclusive = UtcStartOf(to.AddDays(1));
            latest = latest.Where(row => row.OccurredAt < exclusive);
        }

        return latest.OrderByDescending(row => row.OccurredAt).ThenByDescending(row => row.PlanVersion)
            .ThenByDescending(row => row.ParamsVersion).ThenByDescending(row => row.Id)
            .Select(row => new MotorTimelineEvent(row.Id, row.Day, row.Kind, row.OccurredAt, row.PlanVersion,
                row.ParamsVersion, row.EngineVer, row.CalorieDelta, row.RatePercent, row.MessageEs, row.MessageEn));
    }

    private static DateTimeOffset UtcStartOf(DateOnly date) => new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
}
