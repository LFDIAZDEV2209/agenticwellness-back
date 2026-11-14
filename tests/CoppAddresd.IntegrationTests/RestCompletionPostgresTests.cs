using CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    [NutritionPostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Descanso_CompleteTaskMaximoRachaReplayYReconciliacion(bool completeAll)
    {
        await using var db = Context(); var seed = await EffectiveSeedAsync(db); var today = seed.Start.AddDays(1);
        var template = await db.ProgramTemplates.SingleAsync(t => t.Id == seed.Template); template.StreakMinTasks = 3;
        var week = await db.ProgramWeeks.SingleAsync(w => w.EnrollmentId == seed.Enrollment && w.WeekNumber == 1);
        week.TasksSnapshot = WeekSnapshotJson.Serialize(WeekSnapshotJson.Parse(week.TasksSnapshot).Select(t =>
            t.Weekday == 2 && t.TaskCode == "ejercicio" ? t with { Rest = true } : t).ToArray());
        var previous = new DailyCheckIn { Id = Guid.NewGuid(), EnrollmentId = seed.Enrollment, ProgramWeekId = week.Id,
            LocalDate = seed.Start, Weekday = 1, IsPerfectDay = true, TotalPoints = 30, BonusAwarded = 50 };
        db.DailyCheckIns.Add(previous);
        foreach (var code in new[] { TaskCode.podcast, TaskCode.nut, TaskCode.ejercicio })
            db.TaskCompletions.Add(new() { Id = Guid.NewGuid(), EnrollmentId = seed.Enrollment, ProgramWeekId = week.Id,
                DailyCheckinId = previous.Id, LocalDate = seed.Start, Weekday = 1, TaskCode = code, PointsAwarded = 10, SourceRefType = "test" });
        db.StreakStates.Add(new() { EnrollmentId = seed.Enrollment, CurrentStreak = 1, LongestStreak = 1, LastActiveDate = seed.Start });
        await db.SaveChangesAsync();
        var repo = EffectiveRepository(db);
        await Assert.ThrowsAsync<BusinessRuleViolationException>(() => repo.CompleteTaskAsync(new(seed.Enrollment, today, TaskCode.ejercicio, null, null, null, null, null), CancellationToken.None));
        Assert.False(await db.DailyCheckIns.AsNoTracking().AnyAsync(d => d.LocalDate == today));
        var podcast = new CoppAddresd.Application.DTOs.ProgramProgress.CompleteTaskInput(seed.Enrollment, today, TaskCode.podcast, "rest-podcast", null, null, null, null);
        var result = await repo.CompleteTaskAsync(podcast, CancellationToken.None);
        if (completeAll)
        {
            await repo.LogNutritionAsync(seed.Patient, MealCode.des, today, ct: CancellationToken.None);
            var nutrition = new CoppAddresd.Application.DTOs.ProgramProgress.CompleteTaskInput(seed.Enrollment, today, TaskCode.nut, "rest-nut", null, null, null, null);
            result = await repo.CompleteTaskAsync(nutrition, CancellationToken.None);
            var replay = await repo.CompleteTaskAsync(nutrition, CancellationToken.None);
            Assert.Equal(result.DayPointsMax, replay.DayPointsMax); Assert.Equal(result.DayPoints, replay.DayPoints);
            Assert.Equal(2, result.StreakCurrent);
        }
        else Assert.Equal(1, result.StreakCurrent);
        var snapshot = await repo.GetSnapshotAsync(seed.Enrollment, today, CancellationToken.None);
        Assert.DoesNotContain(snapshot!.TodayTasks, t => t.TaskCode == TaskCode.ejercicio);
        Assert.Equal(70, snapshot.TodayPointsMax); Assert.Equal(snapshot.TodayPointsMax, result.DayPointsMax);
        Assert.Equal(completeAll, result.IsPerfectDay);
        if (completeAll) Assert.Equal(snapshot.TodayPointsMax, snapshot.TodayPoints);
        else { Assert.Equal(10, snapshot.TodayPoints); Assert.True(snapshot.TodayBonusAvailable); }
        Assert.False(await db.TaskCompletions.AsNoTracking().AnyAsync(t => t.LocalDate == today && t.TaskCode == TaskCode.ejercicio));
        var bonusCount = await db.XpLedgerEntries.AsNoTracking().CountAsync(x => x.Reason == XpReason.DailyBonus);
        Assert.Equal(completeAll ? 1 : 0, bonusCount);
        await repo.ReconcileStreaksAsync(CancellationToken.None);
        var reconciled = await db.StreakStates.AsNoTracking().SingleAsync(s => s.EnrollmentId == seed.Enrollment);
        Assert.Equal(completeAll ? 2 : 1, reconciled.LongestStreak);
        Assert.Equal(completeAll ? today : seed.Start, reconciled.LastActiveDate);
    }
}
