using CoppAddresd.Application.Features.Nutrition.Queries;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Persistence;
using CoppAddresd.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    private async Task<NutritionPrescription> CycleAdherencePlanAsync(AppDbContext db, Guid patient, DateOnly start, DateTime published,
        Guid? cycle = null, int version = 1, int meals = 3, bool active = true)
    {
        var p = new NutritionPrescription { PatientId = patient, CycleId = cycle ?? Guid.NewGuid(), Version = version,
            StartDate = start, EndDate = start.AddDays(29), State = "aprobado", IsPublished = active, ApprovedAt = published, ApprovedBy = Guid.NewGuid() };
        p.Meals = Enumerable.Range(1, 30).SelectMany(day => new[] { ("des", new TimeOnly(7, 0)), ("alm", new TimeOnly(12, 0)),
            ("cen", new TimeOnly(19, 0)), ("mer", new TimeOnly(15, 30)) }.Take(meals)
            .Select(m => new NutritionPrescriptionMeal { DayNumber = day, MealType = m.Item1, ScheduledTime = m.Item2 })).ToList();
        db.NutritionPrescriptions.Add(p); await db.SaveChangesAsync(); return p;
    }

    private async Task AddCycleIntakesAsync(AppDbContext db, Guid patient, DateOnly day, params MealCode[] meals)
    {
        var templates = await db.HabitTemplates.ToDictionaryAsync(h => h.Code);
        foreach (var meal in meals)
        {
            var code = meal.ToString();
            if (!templates.TryGetValue(code, out var template))
            { template = new HabitTemplate { Id = Guid.NewGuid(), Code = code, Name = code }; db.HabitTemplates.Add(template); templates.Add(code, template); }
            var habit = new HabitCheck { Id = Guid.NewGuid(), PatientId = patient, LocalDate = day, HabitTemplateId = template.Id };
            db.HabitChecks.Add(habit);
            db.NutritionIntakeLogs.Add(new() { Id = Guid.NewGuid(), PatientId = patient, LocalDate = day, MealCode = meal, HabitCheckId = habit.Id });
        }
        await db.SaveChangesAsync();
    }

    private GetCycleAdherenceQueryHandler AdherenceHandler(AppDbContext db, IPatientScopeActor actor, DateTime utc)
        => new(new PatientScopeGuard(actor, new PatientScopeStore(db)), new NutritionCycleReadStore(db), new EffectiveClock(utc));

    [NutritionPostgresFact]
    public async Task CicloAdherencia_RangoSeisSieteOcho_PausaCuenta_FuturoNo_AlcanceAdminYPaginacion()
    {
        await using var db = Context(); var seed = await EffectiveSeedAsync(db); var foreign = await PatientAsync(db);
        var professional = NutritionProfessional(seed.Patient); db.Professionals.Add(professional);
        db.PatientProfessionalAssignments.Add(new() { PatientId = seed.Patient, ProfessionalId = professional.Id });
        await db.ProgramEnrollments.Where(e => e.Id == seed.Enrollment).ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, ProgramEnrollmentStatus.Paused));
        await db.SaveChangesAsync();
        var start = new DateOnly(2026, 11, 5); var p = await CycleAdherencePlanAsync(db, seed.Patient, start, new(2026, 11, 5, 15, 0, 0, DateTimeKind.Utc));
        var f = await CycleAdherencePlanAsync(db, foreign.Id, start, new(2026, 11, 5, 15, 0, 0, DateTimeKind.Utc));
        await AddCycleIntakesAsync(db, seed.Patient, new(2026, 11, 6), MealCode.des, MealCode.alm, MealCode.cen);
        await AddCycleIntakesAsync(db, seed.Patient, new(2026, 11, 7), MealCode.des, MealCode.alm);
        await AddCycleIntakesAsync(db, seed.Patient, new(2026, 11, 9), MealCode.des);
        await AddCycleIntakesAsync(db, foreign.Id, new(2026, 11, 6), MealCode.des);
        var actor = Substitute.For<IPatientScopeActor>(); actor.UserId.Returns(Guid.NewGuid()); actor.ProfessionalIdAsync(Arg.Any<CancellationToken>()).Returns(professional.Id);
        actor.HasPermissionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var handler = AdherenceHandler(db, actor, new(2026, 11, 8, 15, 0, 0, DateTimeKind.Utc));
        var result = await handler.Handle(new(p.CycleId, new(2026, 11, 6), new(2026, 11, 30), PageSize: 2), default);
        Assert.Equal(.83m, result.Adherence); Assert.Equal(2, result.DaysWithData); Assert.Equal(3, result.ElapsedDays); Assert.Equal(1, result.Interruptions);
        Assert.Equal(new DateOnly(2026, 11, 8), result.Through); Assert.Equal(2, result.Days.Count);
        var last = await handler.Handle(new(p.CycleId, new(2026, 11, 6), Page: 2, PageSize: 2), default);
        Assert.True(Assert.Single(last.Days).Interruption);
        Assert.Empty((await handler.Handle(new(p.CycleId, Page: 100, PageSize: 100), default)).Days);
        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(new(f.CycleId), default));
        actor.IsAdmin.Returns(true); Assert.Equal(1, (await handler.Handle(new(f.CycleId, new(2026, 11, 6), new(2026, 11, 6)), default)).DaysWithData);
        actor.HasPermissionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(new(p.CycleId), default));
        actor.HasPermissionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.Handle(new(p.CycleId), cancelled.Token));
    }

    [NutritionPostgresFact]
    public async Task CicloAdherencia_VersionVigentePorDia_ContinuidadNoSeDuplicaEnSucesorTardio()
    {
        await using var db = Context(); var patient = await PatientAsync(db); var start = new DateOnly(2026, 11, 5);
        var first = await CycleAdherencePlanAsync(db, patient.Id, start, new(2026, 11, 5, 15, 0, 0, DateTimeKind.Utc), active: false);
        await CycleAdherencePlanAsync(db, patient.Id, start, new(2026, 11, 8, 2, 0, 0, DateTimeKind.Utc), first.CycleId, 2, 4);
        var second = await CycleAdherencePlanAsync(db, patient.Id, new(2026, 12, 9), new(2026, 12, 9, 15, 0, 0, DateTimeKind.Utc));
        await AddCycleIntakesAsync(db, patient.Id, new(2026, 11, 6), MealCode.des);
        await AddCycleIntakesAsync(db, patient.Id, new(2026, 11, 7), MealCode.des);
        await AddCycleIntakesAsync(db, patient.Id, new(2026, 12, 6), MealCode.des);
        await AddCycleIntakesAsync(db, patient.Id, new(2026, 12, 9), MealCode.des);
        var actor = Substitute.For<IPatientScopeActor>(); actor.UserId.Returns(Guid.NewGuid()); actor.IsAdmin.Returns(true); actor.HasPermissionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var handler = AdherenceHandler(db, actor, new(2026, 12, 10, 15, 0, 0, DateTimeKind.Utc));
        var versions = await handler.Handle(new(first.CycleId, new(2026, 11, 6), new(2026, 11, 7)), default);
        Assert.Equal(3, versions.Days[0].PlannedMeals); Assert.Equal(4, versions.Days[1].PlannedMeals);
        Assert.Equal(first.Id, versions.Days[0].PrescriptionId);
        var continuity = await handler.Handle(new(first.CycleId, new(2026, 12, 5), new(2026, 12, 10)), default);
        var successor = await handler.Handle(new(second.CycleId), default);
        Assert.Equal(new DateOnly(2026, 12, 8), continuity.Through); Assert.Equal(4, continuity.ElapsedDays);
        Assert.All(continuity.Days, d => { Assert.True(d.Continuity); Assert.Equal(30, d.MenuDay); });
        Assert.Contains(continuity.Days, d => d.Date == new DateOnly(2026, 12, 6) && d.RecordedMeals == 1);
        Assert.DoesNotContain(successor.Days, d => d.Date < new DateOnly(2026, 12, 9));
        Assert.Empty(continuity.Days.SelectMany(d => d.IntakeIds).Intersect(successor.Days.SelectMany(d => d.IntakeIds)));
    }
}
