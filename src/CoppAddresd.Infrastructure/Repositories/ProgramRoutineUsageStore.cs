using CoppAddresd.Application.Features.ProgramProgress.Queries.GetRoutineUsage;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

/// <summary>Cinco consultas set-based como máximo; filtra pacientes antes de materializar semanas.</summary>
public sealed class ProgramRoutineUsageStore(AppDbContext db, IPatientScopeStore patientScope) : IProgramRoutineUsageStore
{
    public async Task<EffectiveContentData?> LoadAsync(Guid routineId, PatientScopeFilter scope, CancellationToken ct)
        => db.Database.CurrentTransaction is not null ? await ReadAsync(routineId, scope, ct)
        : await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            // Una sola fotografía de semanas, catálogo y asignaciones; el permiso ya se resolvió fuera de la transacción.
            await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
            await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
            var result = await ReadAsync(routineId, scope, ct);
            await tx.CommitAsync(ct);
            return result;
        });

    private async Task<EffectiveContentData?> ReadAsync(Guid routineId, PatientScopeFilter scope, CancellationToken ct)
    {
        if (!await db.ExerciseRoutines.AsNoTracking().AnyAsync(r => r.Id == routineId, ct)) return null;
        var visible = patientScope.VisiblePatients(scope.ProfessionalId, scope.IsAdmin);
        var query = db.ProgramWeeks.AsNoTracking().Where(w => w.Status == ProgramWeekStatus.Active
            && w.Enrollment!.Status == ProgramEnrollmentStatus.Active && w.Enrollment.Patient!.Status == "Activo"
            && visible.Contains(w.Enrollment.PatientId));
        var weeks = await query.Select(w => new ProgramWeek
        {
            Id = w.Id, EnrollmentId = w.EnrollmentId, WeekNumber = w.WeekNumber, Status = w.Status,
            WeekStartDateLocal = w.WeekStartDateLocal, WeekEndDateLocal = w.WeekEndDateLocal, TasksSnapshot = w.TasksSnapshot,
            Enrollment = new ProgramEnrollment
            {
                Id = w.Enrollment!.Id, PatientId = w.Enrollment.PatientId, TemplateId = w.Enrollment.TemplateId,
                StartLocalDate = w.Enrollment.StartLocalDate,
                Template = new ProgramTemplate { Id = w.Enrollment.TemplateId, TotalWeeks = w.Enrollment.Template!.TotalWeeks },
            },
        }).ToArrayAsync(ct);
        if (weeks.Length == 0) return new();
        var enrollments = weeks.Select(w => w.Enrollment!).DistinctBy(e => e.Id).ToArray();
        var templateIds = enrollments.Select(e => e.TemplateId).Distinct().ToArray();
        var days = await db.WeeklyDayTemplates.AsNoTracking().Where(d => templateIds.Contains(d.TemplateId) && d.TaskCode == TaskCode.ejercicio)
            .OrderByDescending(d => d.WeekNumber.HasValue).ThenBy(d => d.SortOrder).ToArrayAsync(ct);
        var byTemplate = days.ToLookup(d => d.TemplateId);
        foreach (var enrollment in enrollments) enrollment.Template!.DayTemplates = byTemplate[enrollment.TemplateId].ToList();
        var routines = await db.ExerciseRoutines.AsNoTracking().Where(r => r.Status == NutritionPlanStatus.Active).Select(r => r.Id).ToArrayAsync(ct);
        var assignments = await db.RoutineAssignments.AsNoTracking().Where(a => a.Status == AssignmentStatus.Active
            && query.Any(w => w.Enrollment!.PatientId == a.PatientId))
            .Select(a => new { a.PatientId, a.RoutineId, a.StartDate, a.EndDate }).ToArrayAsync(ct);
        return new()
        {
            Enrollments = enrollments, Weeks = weeks, ActiveRoutines = routines.ToHashSet(),
            Legacy = assignments.Select(a => new EffectiveLegacyAssignment(a.PatientId, a.RoutineId, DateOnly.FromDateTime(a.StartDate),
                a.EndDate.HasValue ? DateOnly.FromDateTime(a.EndDate.Value) : null, false)).ToArray(),
        };
    }
}
