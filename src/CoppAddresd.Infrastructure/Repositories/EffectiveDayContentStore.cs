using CoppAddresd.Application.Features.Media;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

/// <summary>Precarga por conjuntos: ningún SELECT se ejecuta dentro del calendario de pacientes/días.</summary>
public sealed class EffectiveDayContentStore(AppDbContext db) : IEffectiveDayContentStore
{
    public async Task<EffectiveContentData> LoadPatientAsync(Guid patientId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var ids = await db.ProgramEnrollments.AsNoTracking().Where(e => e.PatientId == patientId
            && e.Status != ProgramEnrollmentStatus.PendingAssignment && e.StartLocalDate <= to
            && e.StartLocalDate.AddDays(e.Template!.TotalWeeks == 12 ? 82 : e.Template.TotalWeeks * 7 - 1) >= from)
            .OrderByDescending(e => e.StartLocalDate).ThenByDescending(e => e.CreatedAt).Select(e => e.Id).ToArrayAsync(ct);
        return await LoadAsync(ids, from, to, ct);
    }

    public async Task<EffectiveContentData> LoadAsync(IReadOnlyCollection<Guid> enrollmentIds, DateOnly from, DateOnly to, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (to < from) throw new ArgumentException("Rango de fechas inválido.");
        var ids = enrollmentIds.Distinct().ToArray();
        if (ids.Length == 0) return new();
        var enrollments = await db.ProgramEnrollments.AsNoTracking().Where(e => ids.Contains(e.Id))
            .Include(e => e.Template).ThenInclude(t => t!.DayTemplates).AsSplitQuery().ToListAsync(ct);
        var patients = enrollments.Select(e => e.PatientId).Distinct().ToArray();
        var weeks = await db.ProgramWeeks.AsNoTracking().Where(w => ids.Contains(w.EnrollmentId)
            && w.WeekStartDateLocal <= to && w.WeekEndDateLocal >= from).ToListAsync(ct);
        var podcasts = await db.MediaItems.AsNoTracking().Where(m => m.MediaType == MediaType.Podcast && m.Status == MediaStatus.Published
            && m.StorageKey != null && m.StorageKey.Trim() != "" && m.StorageKey.Trim().ToLower() != MediaPublishRules.DemoStorageKey).Select(m => m.Id).ToListAsync(ct);
        var routines = await db.ExerciseRoutines.AsNoTracking().Where(r => r.Status == NutritionPlanStatus.Active).Select(r => r.Id).ToListAsync(ct);
        var headers = await db.NutritionPrescriptions.AsNoTracking().Where(p => patients.Contains(p.PatientId)
            && p.IsPublished && p.State == "aprobado" && p.StartDate != null && p.EndDate != null && p.StartDate <= to)
            .Select(p => new { p.Id, p.PatientId, p.StartDate, p.EndDate, p.ApprovedAt }).ToListAsync(ct);
        var prescriptionIds = headers.Select(p => p.Id).ToArray();
        var prescriptionDays = await db.NutritionPrescriptionMeals.AsNoTracking().Where(m => prescriptionIds.Contains(m.PrescriptionId))
            .Select(m => new { m.PrescriptionId, m.DayNumber }).Distinct().ToListAsync(ct);
        var daysByPrescription = prescriptionDays.ToLookup(m => m.PrescriptionId, m => (int)m.DayNumber);
        var plans = await db.NutritionPlans.AsNoTracking().Where(p => p.Status == NutritionPlanStatus.Active
            && (p.IsTemplate && p.PatientId == null || p.PatientId.HasValue && patients.Contains(p.PatientId.Value)))
            .Select(p => new NutritionPlan { Id = p.Id, IsTemplate = p.IsTemplate, PatientId = p.PatientId, Status = p.Status,
                DurationDays = p.DurationDays, ReviewStatus = p.ReviewStatus, ApprovedAt = p.ApprovedAt, ApprovedBy = p.ApprovedBy }).ToListAsync(ct);
        var planIds = plans.Select(p => p.Id).ToArray();
        var planDays = await db.NutritionPlanDays.AsNoTracking().Where(d => planIds.Contains(d.PlanId))
            .Select(d => new { d.PlanId, d.DayNumber }).Distinct().ToListAsync(ct);
        var daysByPlan = planDays.ToLookup(d => d.PlanId, d => d.DayNumber);
        foreach (var plan in plans)
            plan.Days = daysByPlan[plan.Id].Select(day => new NutritionPlanDay { DayNumber = day }).ToList();
        var legacyRoutines = await db.RoutineAssignments.AsNoTracking().Where(a => patients.Contains(a.PatientId) && a.Status == AssignmentStatus.Active)
            .Select(a => new { a.PatientId, a.RoutineId, a.StartDate, a.EndDate }).ToListAsync(ct);
        var legacyPlans = await db.NutritionPlanAssignments.AsNoTracking().Where(a => patients.Contains(a.PatientId) && a.Status == AssignmentStatus.Active)
            .Select(a => new { a.PatientId, a.PlanId, a.StartDate, a.EndDate }).ToListAsync(ct);
        var decisions = await db.ProgramAssignmentRecommendations.AsNoTracking().Where(r => ids.Contains(r.EnrollmentId)
            && (r.State == "applied" || r.State == "edited" || r.State == "fallback")
            && db.ProgramEnrollments.Any(e => e.Id == r.EnrollmentId && e.PatientId == r.PatientId))
            .Select(r => new { r.EnrollmentId, r.Proposal, r.CreatedAt, r.Id }).ToListAsync(ct);
        var enginePlans = decisions.GroupBy(r => r.EnrollmentId)
            .Select(g => g.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).First())
            .Select(r => new { r.EnrollmentId, PlanId = EffectiveDayContentResolver.EnginePlanId(r.Proposal) })
            .Where(r => r.PlanId.HasValue).ToDictionary(r => r.EnrollmentId, r => r.PlanId!.Value);
        return new()
        {
            Enrollments = enrollments, Weeks = weeks, PublishedPodcasts = podcasts.ToHashSet(), ActiveRoutines = routines.ToHashSet(),
            Prescriptions = headers.Where(p => p.EndDate >= p.StartDate && p.EndDate!.Value <= p.StartDate!.Value.AddDays(29))
                .Select(p => new EffectivePrescription(p.Id, p.PatientId, p.StartDate!.Value, p.EndDate!.Value, p.ApprovedAt, daysByPrescription[p.Id].ToArray())).ToArray(),
            Plans = plans.Select(p => new EffectivePlan(p)).ToArray(),
            Legacy = legacyRoutines.Select(a => new EffectiveLegacyAssignment(a.PatientId, a.RoutineId, DateOnly.FromDateTime(a.StartDate),
                    a.EndDate.HasValue ? DateOnly.FromDateTime(a.EndDate.Value) : null, false))
                .Concat(legacyPlans.Select(a => new EffectiveLegacyAssignment(a.PatientId, a.PlanId, DateOnly.FromDateTime(a.StartDate),
                    a.EndDate.HasValue ? DateOnly.FromDateTime(a.EndDate.Value) : null, true))).ToArray(),
            EngineNutrition = enginePlans,
        };
    }
}
