using CoppAddresd.Application.DTOs.ProgramProgress;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Domain.Enums.ProgramProgress;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

public sealed partial class ProgramRepository
{
    /// <summary>Una sentencia atómica por lote; omite filas ocupadas por una completación concurrente.</summary>
    public Task<int> CompleteExpiredProgramsAsync(CancellationToken ct)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        // El cierre por calendario no exige una semana perfecta. Respeta la zona
        // del paciente y las pausas, y no toca las plantillas legadas de 83 semanas.
        return dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            WITH due AS MATERIALIZED (
                SELECT e.id, e.start_local_date
                FROM app.program_enrollments e
                JOIN app.program_templates t ON t.id=e.template_id
                JOIN app.patient_profiles p ON p.id=e.patient_id
                WHERE e.status='Active' AND t.total_weeks=12 AND p.deleted_at IS NULL
                  AND ({now} AT TIME ZONE e.timezone)::date > e.start_local_date+82
                ORDER BY e.start_local_date,e.id LIMIT 500 FOR UPDATE OF e SKIP LOCKED
            ), closed_weeks AS (
                UPDATE app.program_weeks w SET status='Completed',
                    completed_at=COALESCE(w.completed_at,{now}),updated_at={now}
                FROM due d WHERE w.enrollment_id=d.id AND w.status<>'Completed'
                RETURNING w.enrollment_id
            )
            UPDATE app.program_enrollments e SET status='Completed',current_week_number=12,
                completed_at={now},updated_at={now}
            FROM due d WHERE e.id=d.id AND e.status='Active'
            """, ct);
    }

    public async Task<ProgramCompletionDto?> GetCompletionAsync(Guid patientId, CancellationToken ct)
    {
        var row = await dbContext.ProgramEnrollments.AsNoTracking()
            .Where(e => e.PatientId == patientId && e.Status == ProgramEnrollmentStatus.Completed && e.CompletedAt != null)
            .OrderByDescending(e => e.CompletedAt).ThenByDescending(e => e.Id)
            .Select(e => new { e.Id, e.Timezone, e.StartLocalDate, e.CompletedAt,
                e.Template!.Name, e.Template.TotalWeeks,
                CompletedWeeks = e.Weeks.Count(w => w.Status == ProgramWeekStatus.Completed),
                LongestStreak = (int?)e.StreakState!.LongestStreak ?? 0,
                XpEarned = e.XpLedgerEntries.Where(x => x.ValidatedBy != null || x.Rule == null || !x.Rule.RequiresValidation)
                    .Sum(x => (int?)x.Amount) ?? 0,
                CanStart = !dbContext.ProgramEnrollments.Any(other => other.PatientId == patientId
                    && (other.Status == ProgramEnrollmentStatus.Active || other.Status == ProgramEnrollmentStatus.PendingAssignment
                        || other.Status == ProgramEnrollmentStatus.Paused)) })
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;
        var duration = EffectiveDayContentResolver.DurationDays(new() { TotalWeeks = row.TotalWeeks });
        var end = row.StartLocalDate.AddDays(duration - 1);
        var counts = await dbContext.TaskCompletions.AsNoTracking()
            .Where(t => t.EnrollmentId == row.Id && t.LocalDate >= row.StartLocalDate && t.LocalDate <= end)
            .GroupBy(t => t.EnrollmentId)
            .Select(g => new { Tasks = g.Count(), Days = g.Select(t => t.LocalDate).Distinct().Count() })
            .SingleOrDefaultAsync(ct);
        var perfect = await dbContext.DailyCheckIns.AsNoTracking()
            .CountAsync(d => d.EnrollmentId == row.Id && d.LocalDate >= row.StartLocalDate && d.LocalDate <= end && d.IsPerfectDay, ct);
        return new(row.Id, row.Name, row.Timezone, row.StartLocalDate, end, row.CompletedAt!.Value, duration,
            row.CompletedWeeks, counts?.Days ?? 0, perfect, counts?.Tasks ?? 0, row.XpEarned, row.LongestStreak, row.CanStart);
    }
}
