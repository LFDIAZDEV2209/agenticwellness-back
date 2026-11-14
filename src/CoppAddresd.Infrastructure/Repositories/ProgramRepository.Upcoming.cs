using CoppAddresd.Application.DTOs.ProgramProgress;
using CoppAddresd.Application.Services.ProgramProgress;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

public sealed partial class ProgramRepository
{
    private async Task<IReadOnlyList<ProgramUpcomingDayDto>?> BuildUpcomingAsync(Guid enrollmentId, DateOnly today, CancellationToken ct)
    {
        if (effectiveStore is null || effectiveResolver is null) return null;
        // El id procede del snapshot ya autorizado; ninguna consulta amplía su alcance a otros pacientes.
        var data = await effectiveStore.LoadAsync([enrollmentId], today.AddDays(1), today.AddDays(6), ct);
        var enrollment = data.Enrollments.SingleOrDefault(e => e.Id == enrollmentId);
        if (enrollment is null) return [];
        var days = ProgramUpcomingPlanner.Build(data, enrollment, effectiveResolver, today, ct);
        if (days.Count == 0) return [];
        var routineIds = days.Where(d => d.Content.Routine.Id.HasValue).Select(d => d.Content.Routine.Id!.Value).Distinct().ToArray();
        var mediaIds = days.Where(d => d.Content.Podcast.Id.HasValue).Select(d => d.Content.Podcast.Id!.Value).Distinct().ToArray();
        var planIds = days.Where(d => !d.Content.Nutrition.Prescription && d.Content.Nutrition.Id.HasValue)
            .Select(d => d.Content.Nutrition.Id!.Value).Distinct().ToArray();
        var routines = await dbContext.ExerciseRoutines.AsNoTracking().Where(r => routineIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name, ct);
        var media = await dbContext.MediaItems.AsNoTracking().Where(m => mediaIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.Title, ct);
        var plans = await dbContext.NutritionPlans.AsNoTracking().Where(p => planIds.Contains(p.Id)
            && (p.IsTemplate && p.PatientId == null || p.PatientId == enrollment.PatientId)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        return days.Select(day => new ProgramUpcomingDayDto(day.Content.Date, day.Day, day.Content.WeekNumber,
            day.Missions.Select(row =>
            {
                var code = ParseTaskCode(row.TaskCode); var title = ProgramTaskCatalog.For(code).Title;
                return code switch
                {
                    CoppAddresd.Domain.Enums.ProgramProgress.TaskCode.ejercicio => new ProgramUpcomingMissionDto("ejercicio",
                        day.Content.Routine.Id is { } id ? routines.GetValueOrDefault(id, title) : title,
                        RoutineId: day.Content.Routine.Id, ContentUnavailable: day.Content.Routine.Missing),
                    CoppAddresd.Domain.Enums.ProgramProgress.TaskCode.podcast => new ProgramUpcomingMissionDto("podcast",
                        day.Content.Podcast.Id is { } id ? media.GetValueOrDefault(id, title) : title,
                        MediaId: day.Content.Podcast.Id, ContentUnavailable: day.Content.Podcast.Missing),
                    CoppAddresd.Domain.Enums.ProgramProgress.TaskCode.nut => new ProgramUpcomingMissionDto("nut",
                        !day.Content.Nutrition.Prescription && day.Content.Nutrition.Id is { } id ? plans.GetValueOrDefault(id, title) : title,
                        NutritionPlanId: day.Content.Nutrition.Prescription ? null : day.Content.Nutrition.Id,
                        SourcePrescriptionId: day.Content.Nutrition.Prescription ? day.Content.Nutrition.Id : null,
                        ContentUnavailable: day.Content.Nutrition.Missing),
                    _ => new ProgramUpcomingMissionDto(code.ToString(), title),
                };
            }).ToArray(), day.Content.Routine.Rest ? true : null)).ToArray();
    }
}
