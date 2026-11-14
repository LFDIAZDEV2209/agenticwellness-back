using System.Data;
using CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoppAddresd.Infrastructure.Repositories;

/// <summary>Carga por conjuntos y escritura atómica del lote, con alcance antes de materializar y sin lecturas por paciente.</summary>
public sealed class BulkPatientPodcastStore(AppDbContext db, IPatientScopeGuard guard, IPatientScopeActor actor,
    TimeProvider clock) : IBulkPatientPodcastStore
{
    public async Task<BulkAssignPatientPodcastsResult> AssignAsync(BulkAssignPatientPodcastsCommand request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requested = request.PatientIds.Distinct().ToArray();
        // Resolver autorización antes de abrir la transacción evita llamadas de Auth dentro de ella.
        var visibleQuery = await guard.FilterAsync(db.PatientProfiles.AsNoTracking().Where(p => requested.Contains(p.Id) && p.DeletedAt == null),
            p => p.Id, "Program.Edit", ct);
        var actorId = actor.UserId ?? throw new ForbiddenException("Usuario no identificado.");
        try
        {
            return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                var visible = await visibleQuery.Select(p => p.Id).ToArrayAsync(ct);
                var media = await db.MediaItems.AsNoTracking().SingleOrDefaultAsync(m => m.Id == request.MediaId, ct)
                    ?? throw new NotFoundException("Medio no encontrado.");
                var enrollments = await db.ProgramEnrollments.AsNoTracking().Where(e => visible.Contains(e.PatientId) && e.Status == ProgramEnrollmentStatus.Active)
                    .Include(e => e.Template).ThenInclude(t => t!.DayTemplates).AsSplitQuery().ToListAsync(ct);
                var ids = enrollments.Select(e => e.Id).ToArray();
                var weeks = await db.ProgramWeeks.AsNoTracking().Where(w => ids.Contains(w.EnrollmentId)
                    && w.WeekNumber >= request.SemanaDesde && w.WeekNumber <= request.SemanaHasta).ToListAsync(ct);
                var plan = BulkPatientPodcastPlanner.Build(request, media, visible, enrollments, weeks, actorId, clock.GetUtcNow(), ct);
                foreach (var update in plan.Updates)
                {
                    if (update.IsNew) db.ProgramWeeks.Add(update.Week);
                    else
                    {
                        db.Attach(update.Week);
                        db.Entry(update.Week).Property(w => w.TasksSnapshot).IsModified = true;
                        db.Entry(update.Week).Property(w => w.UpdatedAt).IsModified = true;
                    }
                    update.Week.TasksSnapshot = update.Snapshot; update.Week.UpdatedAt = clock.GetUtcNow().UtcDateTime;
                }
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                // Un reintento de la estrategia vuelve a cargar el estado confirmado, sin entidades de intentos anteriores.
                db.ChangeTracker.Clear();
                return plan.Result;
            });
        }
        catch (PostgresException ex) when (ex.SqlState == "40001")
        { throw new BusinessRuleViolationException("PROGRAM_CONTENT_CONCURRENT_CHANGE: cambió el programa durante la asignación; reintenta."); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "40001" or "23505" })
        { throw new BusinessRuleViolationException("PROGRAM_CONTENT_CONCURRENT_CHANGE: cambió una semana durante la asignación; reintenta."); }
    }
}
