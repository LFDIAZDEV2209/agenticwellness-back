using CoppAddresd.Application.Features.HealthTests.Execution;
using CoppAddresd.Application.Common;
using CoppAddresd.Application.Features.ProgramProgress.Assignment;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Entities.HealthTests;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Domain.Enums.HealthTests;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Persistence;
using CoppAddresd.Infrastructure.Repositories;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CoppAddresd.IntegrationTests;

public sealed partial class ProgramAssignmentIntegrationTests
{
    private async Task SeedCompleteDefaultsAsync()
    {
        await using var db = NewDb();
        var template = await db.ProgramTemplates.SingleAsync();
        var plan = await db.NutritionPlans.SingleAsync();
        var routine = await db.ExerciseRoutines.SingleAsync();
        var podcast = Podcast("Audio válido por defecto"); db.MediaItems.Add(podcast);
        for (short day = 1; day <= 7; day++)
        {
            db.NutritionPlanDays.Add(new NutritionPlanDay { Id = Guid.NewGuid(), PlanId = plan.Id, DayNumber = day, Description = "Menú QA" });
            db.WeeklyDayTemplates.Add(new WeeklyDayTemplate { Id = Guid.NewGuid(), TemplateId = template.Id, Weekday = day, TaskCode = TaskCode.nut, NutritionPlanId = plan.Id });
            db.WeeklyDayTemplates.Add(new WeeklyDayTemplate { Id = Guid.NewGuid(), TemplateId = template.Id, Weekday = day, TaskCode = TaskCode.ejercicio, RoutineId = routine.Id });
            db.WeeklyDayTemplates.Add(new WeeklyDayTemplate { Id = Guid.NewGuid(), TemplateId = template.Id, Weekday = day, TaskCode = TaskCode.podcast, MediaId = podcast.Id });
        }
        await db.SaveChangesAsync();
    }

    private static async Task AssertDefaultsAsync(AppDbContext db, ProgramEnrollment enrollment)
    {
        var store = new EffectiveDayContentStore(db);
        var data = await store.LoadAsync([enrollment.Id], enrollment.StartLocalDate, enrollment.StartLocalDate.AddDays(82), default);
        var days = new EffectiveDayContentResolver(store).ResolveRange(data, data.Enrollments.Single(), enrollment.StartLocalDate, enrollment.StartLocalDate.AddDays(82), default);
        Assert.Equal(83, days.Count);
        Assert.All(days, day => Assert.False(day.HasGaps));
        Assert.All(days, day => Assert.NotNull(day.Nutrition.Id));
        Assert.All(days, day => Assert.NotNull(day.Routine.Id));
        Assert.All(days, day => Assert.NotNull(day.Podcast.Id));
    }

    private async Task<(Guid PatientId, Guid[] Last)> SeedSevenCompletedAsync()
    {
        await using var db = NewDb();
        var patient = new PatientProfile { Id = Guid.NewGuid(), FirstName = "Nuevo", LastName = "A2F2", DocumentNumber = Guid.NewGuid().ToString("N") };
        db.PatientProfiles.Add(patient);
        var battery = await db.HealthTestBatteries.SingleAsync(b => b.Code == "bateria-inicial"); battery.AutoAssignOnPatientCreate = true;
        var parent = new HealthTestBatteryAssignment { Id = Guid.NewGuid(), PatientId = patient.Id, BatteryId = battery.Id };
        db.HealthTestBatteryAssignments.Add(parent);
        var instruments = await db.HealthTestBatteryItems.Where(i => i.BatteryId == battery.Id && i.IsRequired).Select(i => i.InstrumentId).ToArrayAsync();
        var versions = await db.HealthTestVersions.Where(v => instruments.Contains(v.InstrumentId) && v.IsCurrent).OrderBy(v => v.Id).ToArrayAsync();
        var last = new List<Guid>();
        for (var i = 0; i < versions.Length; i++)
        {
            var a = new HealthTestAssignment { Id = Guid.NewGuid(), PatientId = patient.Id, BatteryAssignmentId = parent.Id, VersionId = versions[i].Id,
                Status = i < 7 ? HealthTestAssignmentStatus.completed : HealthTestAssignmentStatus.pending };
            db.HealthTestAssignments.Add(a);
            if (i < 7) db.HealthTestEvaluations.Add(new HealthTestEvaluation { Id = Guid.NewGuid(), AssignmentId = a.Id, PatientId = patient.Id,
                VersionId = a.VersionId, Status = HealthTestEvaluationStatus.completed, CompletedAt = _lifecycleClock.Current.UtcDateTime });
            else last.Add(a.Id);
        }
        await db.SaveChangesAsync(); return (patient.Id, last.ToArray());
    }

    [AssignmentPostgresFact]
    public async Task GateDeshabilitado_UltimosTestsConcurrentes_Default83DiasSinIA_YReplayUnico()
    {
        await SeedCompleteDefaultsAsync();
        _services.GetRequiredService<IOptionsMonitor<ProgramAssignmentSettings>>().CurrentValue.Enabled = false;
        _lifecycleClock.Current = new DateTimeOffset(2026, 11, 15, 15, 0, 0, TimeSpan.Zero); // Domingo.
        var seed = await SeedSevenCompletedAsync();
        async Task Submit(Guid id)
        {
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new SubmitEvaluationCommand(id, new([], "a2-" + id)));
        }
        await Task.WhenAll(seed.Last.Select(Submit));
        await Task.WhenAll(seed.Last.Select(Submit));
        await using var db = NewDb();
        Assert.Equal(9, await db.HealthTestEvaluations.CountAsync(e => e.PatientId == seed.PatientId));
        var enrollment = await db.ProgramEnrollments.SingleAsync(e => e.PatientId == seed.PatientId);
        Assert.Equal(ProgramEnrollmentStatus.Active, enrollment.Status);
        Assert.Equal(DayOfWeek.Sunday, enrollment.StartLocalDate.DayOfWeek);
        Assert.Equal(12, await db.ProgramWeeks.CountAsync(w => w.EnrollmentId == enrollment.Id));
        Assert.False(await db.ProgramAssignmentRecommendations.AnyAsync(r => r.PatientId == seed.PatientId));
        await AssertDefaultsAsync(db, enrollment);
    }

    [AssignmentPostgresFact]
    public async Task DefaultConDemo_RevierteNovenoTest_NoConfirmaBateriaVacia_ReintentoRecupera()
    {
        await SeedCompleteDefaultsAsync();
        var seed = await SeedSevenCompletedAsync();
        using (var scope = _services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new SubmitEvaluationCommand(seed.Last[0], new([], "octavo")));
        await using var db = NewDb();
        await db.MediaItems.ExecuteUpdateAsync(s => s.SetProperty(m => m.StorageKey, "media/podcasts/demo-copp.mp3"));
        using (var scope = _services.CreateScope())
            await Assert.ThrowsAsync<BusinessRuleViolationException>(() => scope.ServiceProvider.GetRequiredService<IMediator>()
                .Send(new SubmitEvaluationCommand(seed.Last[1], new([], "noveno"))));
        Assert.Equal(8, await db.HealthTestEvaluations.CountAsync(e => e.PatientId == seed.PatientId && e.Status == HealthTestEvaluationStatus.completed));
        Assert.False(await db.ProgramEnrollments.AnyAsync(e => e.PatientId == seed.PatientId));
        await db.MediaItems.ExecuteUpdateAsync(s => s.SetProperty(m => m.StorageKey, "media/podcasts/qa-real.mp3"));
        using (var scope = _services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new SubmitEvaluationCommand(seed.Last[1], new([], "noveno")));
        await AssertDefaultsAsync(db, await db.ProgramEnrollments.SingleAsync(e => e.PatientId == seed.PatientId));
    }
}
