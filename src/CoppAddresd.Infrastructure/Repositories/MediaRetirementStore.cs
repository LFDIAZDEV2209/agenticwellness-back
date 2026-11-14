using System.Data;
using CoppAddresd.Application.Features.Media;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CoppAddresd.Infrastructure.Repositories;

/// <summary>Unidad atómica del retiro; serializable evita validar y sustituir sobre estados incompatibles.</summary>
public sealed class MediaRetirementStore(AppDbContext db, IEffectiveDayContentStore content,
    IEffectiveDayContentResolver resolver, TimeProvider clock) : IMediaRetirementStore
{
    private async Task<MediaRetirementData> LoadAsync(Guid id, Guid? replacement, string permission, CancellationToken ct)
    {
        var scoped = await content.LoadScopedActiveAsync(null, permission, ct);
        var media = await db.MediaItems.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, ct)
            ?? throw new NotFoundException("Medio no encontrado.");
        var substitute = replacement.HasValue ? await db.MediaItems.AsNoTracking().SingleOrDefaultAsync(m => m.Id == replacement, ct) : null;
        var templates = await db.WeeklyDayTemplates.AsNoTracking().Where(d => d.MediaId == id && d.Template!.Status == TemplateStatus.Active).ToListAsync(ct);
        return new(media, substitute, templates, scoped, await HasFutureReferencesAsync(id, ct));
    }

    public async Task<MediaRetirementImpact> PreviewAsync(Guid id, CancellationToken ct)
    {
        var data = await LoadAsync(id, null, "Media.View", ct);
        return MediaRetirementPolicy.Plan(data, resolver, clock.GetUtcNow().UtcDateTime, false, ct).Impact;
    }

    public Task<MediaRetirementResult> ExecuteAsync(RetireMediaCommand request, CancellationToken ct) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var permission = request.Operation switch { MediaRetirementOperation.Unpublish => "Media.Publish", MediaRetirementOperation.Archive => "Media.Archive", _ => "Media.Delete" };
            var data = await LoadAsync(request.Id, request.ReplacementMediaId, permission, ct);
            if (request.Operation == MediaRetirementOperation.Unpublish && data.Media.Status == MediaStatus.Archived)
                throw new UnprocessableEntityException("No se puede despublicar un medio archivado.");
            if (request.ReplacementMediaId.HasValue) MediaRetirementPolicy.ValidateReplacement(data);
            var plan = MediaRetirementPolicy.Plan(data, resolver, clock.GetUtcNow().UtcDateTime, request.ReplacementMediaId.HasValue, ct);
            var dto = MediaItemDto.FromEntity(data.Media);
            if (plan.Blocked) return new MediaRetirementResult(dto, plan.Impact, true);
            if (request.ReplacementMediaId is { } substitute)
            {
                var templateIds = data.Templates.Select(t => t.Id).ToArray();
                await db.WeeklyDayTemplates.Where(d => templateIds.Contains(d.Id)).ExecuteUpdateAsync(s => s.SetProperty(d => d.MediaId, (Guid?)substitute), ct);
                foreach (var (id, snapshot) in plan.Snapshots)
                {
                    var week = db.ProgramWeeks.Local.FirstOrDefault(w => w.Id == id)
                        ?? new CoppAddresd.Domain.Entities.ProgramProgress.ProgramWeek { Id = id };
                    if (db.Entry(week).State == EntityState.Detached) db.Attach(week);
                    week.TasksSnapshot = snapshot; db.Entry(week).Property(w => w.TasksSnapshot).IsModified = true;
                }
                await db.SaveChangesAsync(ct);
            }
            if (request.Operation == MediaRetirementOperation.Delete && await HasAnyReferencesAsync(request.Id, ct))
            {
                db.ChangeTracker.Clear();
                return new MediaRetirementResult(dto, plan.Impact, true);
            }
            if (request.Operation == MediaRetirementOperation.Delete)
                await db.MediaItems.Where(m => m.Id == request.Id).ExecuteDeleteAsync(ct);
            else
            {
                var status = request.Operation == MediaRetirementOperation.Archive ? MediaStatus.Archived : MediaStatus.Draft;
                await db.MediaItems.Where(m => m.Id == request.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, status)
                    .SetProperty(m => m.UpdatedAt, clock.GetUtcNow()), ct);
                dto = dto with { Status = status, UpdatedAt = clock.GetUtcNow() };
            }
            await tx.CommitAsync(ct);
            return new MediaRetirementResult(dto, plan.Impact, false, request.Operation == MediaRetirementOperation.Delete);
        });

    private async Task<bool> HasAnyReferencesAsync(Guid id, CancellationToken ct)
    {
        // Solo existencia global para integridad del DELETE; nunca carga ni revela datos de pacientes ajenos.
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM app.weekly_day_templates WHERE media_id=@id)
              OR EXISTS(SELECT 1 FROM app.program_weeks w CROSS JOIN LATERAL
                  jsonb_array_elements(CASE WHEN jsonb_typeof(w.tasks_snapshot)='array' THEN w.tasks_snapshot ELSE '[]'::jsonb END) t
                  WHERE t->>'media_id'=@id::text)
              OR EXISTS(SELECT 1 FROM app.routine_exercises WHERE media_id=@id)
              OR EXISTS(SELECT 1 FROM app.exercise_routines WHERE media_id=@id)
            """;
        var parameter = command.CreateParameter(); parameter.ParameterName = "id"; parameter.DbType = DbType.Guid; parameter.Value = id;
        command.Parameters.Add(parameter);
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }

    private async Task<bool> HasFutureReferencesAsync(Guid id, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection(); var ownsOpen = connection.State != ConnectionState.Open;
        if (ownsOpen) await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = """
                SELECT EXISTS(SELECT 1 FROM app.program_weeks w
                  JOIN app.program_enrollments e ON e.id=w.enrollment_id
                  CROSS JOIN LATERAL jsonb_array_elements(CASE WHEN jsonb_typeof(w.tasks_snapshot)='array' THEN w.tasks_snapshot ELSE '[]'::jsonb END) t
                  WHERE t->>'media_id'=@id::text AND e.status='Active' AND w.status='Locked'
                    AND w.activated_at IS NULL AND w.completed_at IS NULL
                    AND w.week_start_date_local > (@now AT TIME ZONE e.timezone)::date)
                """;
            var idParameter = command.CreateParameter(); idParameter.ParameterName = "id"; idParameter.DbType = DbType.Guid; idParameter.Value = id;
            var nowParameter = command.CreateParameter(); nowParameter.ParameterName = "now"; nowParameter.Value = clock.GetUtcNow().UtcDateTime;
            command.Parameters.Add(idParameter); command.Parameters.Add(nowParameter);
            return (bool)(await command.ExecuteScalarAsync(ct))!;
        }
        finally { if (ownsOpen) await db.Database.CloseConnectionAsync(); }
    }
}
