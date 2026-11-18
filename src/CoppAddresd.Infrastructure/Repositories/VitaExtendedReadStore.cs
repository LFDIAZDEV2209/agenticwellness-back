using CoppAddresd.Application.Features.Agents.Vita;
using CoppAddresd.Application.Features.HealthTests;
using CoppAddresd.Domain.Enums.HealthTests;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

public sealed class VitaExtendedReadStore(AppDbContext db) : IVitaExtendedReadStore
{
    public async Task<IReadOnlyList<VitaNutritionHistoryDto>> NutritionHistoryAsync(Guid patientId, DateOnly from,
        DateOnly to, CancellationToken ct)
        => await db.NutritionIntakeLogs.AsNoTracking().Where(x => x.PatientId == patientId && x.LocalDate >= from && x.LocalDate <= to)
            .OrderByDescending(x => x.LocalDate).ThenBy(x => x.MealCode).ThenBy(x => x.Id)
            .Select(x => new VitaNutritionHistoryDto(x.LocalDate, x.MealCode.ToString(), x.Calories, x.ProteinG,
                x.CarbsG, x.FatG, x.FiberG, x.WaterMl, x.Source)).ToArrayAsync(ct);

    public async Task<VitaAssessmentsDto> AssessmentsAsync(Guid patientId, CancellationToken ct)
    {
        var batteries = await db.HealthTestBatteryAssignments.AsNoTracking()
            .Where(b => b.PatientId == patientId && (b.Status == HealthTestAssignmentStatus.completed
                || b.Status == HealthTestAssignmentStatus.pending || b.Status == HealthTestAssignmentStatus.in_progress))
            .OrderByDescending(b => b.AssignedAt).ThenByDescending(b => b.Id).Take(20)
            .Select(b => new { b.Id, b.Battery!.Name, b.Status, b.DueDate, b.CompletedAt }).ToArrayAsync(ct);
        var completed = batteries.Where(b => b.Status == HealthTestAssignmentStatus.completed).Select(b => b.Id).ToArray();
        var tests = await db.HealthTestEvaluations.AsNoTracking()
            .Where(e => e.PatientId == patientId && e.Status == HealthTestEvaluationStatus.completed && e.CompletedAt != null
                && e.Assignment!.PatientId == patientId && e.Assignment.Status == HealthTestAssignmentStatus.completed
                && e.Assignment.BatteryAssignmentId != null && completed.Contains(e.Assignment.BatteryAssignmentId.Value))
            .OrderByDescending(e => e.CompletedAt).ThenByDescending(e => e.Id)
            .Select(e => new
            {
                BatteryId = e.Assignment!.BatteryAssignmentId!.Value,
                TestName = e.Version!.Instrument!.Name,
                TestCode = e.Version.Instrument.Code,
                CompletedAt = e.CompletedAt!.Value,
                e.Score,
                e.ScorePercentage,
                Results = e.Results.OrderBy(r => r.Code).ThenBy(r => r.Id).ToArray(),
            }).AsSplitQuery().ToArrayAsync(ct);
        var byBattery = tests.ToLookup(e => e.BatteryId, e => new VitaAssessmentTestDto(e.TestName, e.TestCode,
            e.CompletedAt, e.Score, e.ScorePercentage, e.Results.Select(result =>
            {
                var visible = HealthTestResultDto.FromEntity(result);
                return new VitaAssessmentResultDto(visible.ResultType.ToString(), visible.Code, visible.Label, visible.Value,
                    visible.Qualifier, visible.Severity?.ToString());
            }).ToArray()));
        return new(batteries.Select(b => new VitaAssessmentBatteryDto(b.Id, b.Name, b.Status.ToString(),
            b.DueDate, b.CompletedAt, byBattery[b.Id].ToArray())).ToArray());
    }
}
