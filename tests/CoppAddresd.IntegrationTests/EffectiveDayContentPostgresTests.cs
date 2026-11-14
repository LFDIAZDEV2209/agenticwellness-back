using System.Data.Common;
using System.Text.Json;
using CoppAddresd.Application.DTOs.ProgramProgress;
using CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Infrastructure.Persistence;
using CoppAddresd.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    private sealed record EffectiveSeed(Guid Patient, Guid Enrollment, Guid Template, Guid Podcast, Guid Routine, Guid Plan, DateOnly Start);
    private async Task<EffectiveSeed> EffectiveSeedAsync(AppDbContext db)
    {
        var patient = await PatientAsync(db); var start = new DateOnly(2026, 11, 2);
        var podcast = new MediaItem { Id = Guid.NewGuid(), Title = "General", Author = "Equipo", MediaType = MediaType.Podcast,
            Category = MediaCategory.Biologia, Status = MediaStatus.Published, StorageKey = "media/podcasts/general.mp3", DurationSecs = 45 };
        var routine = new ExerciseRoutine { Id = Guid.NewGuid(), Name = "General", Status = NutritionPlanStatus.Active };
        var plan = new NutritionPlan { Id = Guid.NewGuid(), Name = "Respaldo", DurationDays = 7, Status = NutritionPlanStatus.Active,
            Days = Enumerable.Range(1, 7).Select(d => new NutritionPlanDay { Id = Guid.NewGuid(), DayNumber = d, MealType = MealType.Desayuno, Foods = "Menú general" }).ToList() };
        db.MediaItems.Add(podcast); db.ExerciseRoutines.Add(routine); db.NutritionPlans.Add(plan);
        var template = new ProgramTemplate { Id = Guid.NewGuid(), Code = Guid.NewGuid().ToString("N"), Name = "83 días", TotalWeeks = 12, Status = TemplateStatus.Active };
        template.DayTemplates = Enumerable.Range(1, 7).SelectMany(d => new[] {
            new WeeklyDayTemplate { Id = Guid.NewGuid(), Weekday = (short)d, TaskCode = TaskCode.podcast, Points = 10, MediaId = podcast.Id },
            new WeeklyDayTemplate { Id = Guid.NewGuid(), Weekday = (short)d, TaskCode = TaskCode.ejercicio, Points = 10, RoutineId = routine.Id },
            new WeeklyDayTemplate { Id = Guid.NewGuid(), Weekday = (short)d, TaskCode = TaskCode.nut, Points = 10, NutritionPlanId = plan.Id },
        }).ToList();
        db.ProgramTemplates.Add(template);
        var enrollment = new ProgramEnrollment { Id = Guid.NewGuid(), PatientId = patient.Id, TemplateId = template.Id, StartLocalDate = start };
        db.ProgramEnrollments.Add(enrollment);
        for (var number = 1; number <= 3; number++)
            db.ProgramWeeks.Add(new() { Id = Guid.NewGuid(), EnrollmentId = enrollment.Id, WeekNumber = number,
                WeekStartDateLocal = start.AddDays((number - 1) * 7), WeekEndDateLocal = start.AddDays(number * 7 - 1),
                Status = number == 1 ? ProgramWeekStatus.Active : ProgramWeekStatus.Locked,
                TasksSnapshot = WeekSnapshotJson.Serialize(number == 1 ? template.DayTemplates.Select(d =>
                    new WeekSnapshotTaskRow(d.Weekday, d.TaskCode.ToString(), d.Points, 1, d.RoutineId, d.NutritionPlanId, d.MediaId, "program")).ToList() : []) });
        db.HabitTemplates.Add(new() { Id = Guid.NewGuid(), Code = "des", Name = "Desayuno" });
        await db.SaveChangesAsync();
        return new(patient.Id, enrollment.Id, template.Id, podcast.Id, routine.Id, plan.Id, start);
    }

    private static ProgramRepository EffectiveRepository(AppDbContext db)
    {
        var store = new EffectiveDayContentStore(db); var resolver = new EffectiveDayContentResolver(store);
        var bridge = new ProgramContentResolver(new WellnessRepository(db), NullLogger<ProgramContentResolver>.Instance,
            new NutritionPrescriptionRepository(db), store, resolver);
        return new(db, new ConfigurationBuilder().Build(), programContentResolver: bridge, effectiveStore: store, effectiveResolver: resolver);
    }

    private sealed class EffectiveSqlCounter : DbCommandInterceptor
    {
        public int Reads;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Reads++; return ValueTask.FromResult(result); }
    }

    [NutritionPostgresFact]
    public async Task Effective_PrecargaConstanteRangoVacioArchivadoYCancelacion()
    {
        await using var seedDb = Context(); var seed = await EffectiveSeedAsync(seedDb);
        var template = await seedDb.ProgramTemplates.SingleAsync(t => t.Id == seed.Template);
        template.Code = "default-83w"; template.TotalWeeks = 83; template.Status = TemplateStatus.Archived; await seedDb.SaveChangesAsync();
        var counter = new EffectiveSqlCounter();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).AddInterceptors(counter).Options);
        var store = new EffectiveDayContentStore(db); var resolver = new EffectiveDayContentResolver(store);
        var data = await store.LoadAsync([seed.Enrollment], seed.Start, seed.Start, CancellationToken.None); var firstReads = counter.Reads;
        counter.Reads = 0;
        data = await store.LoadAsync([seed.Enrollment], seed.Start, seed.Start.AddDays(580), CancellationToken.None);
        Assert.Equal(firstReads, counter.Reads); Assert.InRange(counter.Reads, 1, 15);
        var before = counter.Reads;
        var days = resolver.ResolveRange(data, Assert.Single(data.Enrollments), seed.Start, seed.Start.AddDays(580), CancellationToken.None);
        Assert.Equal(581, days.Count); Assert.All(days, d => Assert.False(d.HasGaps)); Assert.Equal(before, counter.Reads);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveAsync(seed.Enrollment, seed.Start, cancellation.Token));
        Assert.Equal(before, counter.Reads);
        var none = await store.LoadAsync([Guid.NewGuid()], seed.Start, seed.Start, CancellationToken.None);
        Assert.Empty(none.Enrollments);
    }

    [NutritionPostgresFact]
    public async Task Effective_ConsumidorUsaFallbackPublicadoYNoReferenciasPrivadas()
    {
        await using var db = Context(); var seed = await EffectiveSeedAsync(db); var foreign = await PatientAsync(db);
        var privatePlan = new NutritionPlan { Id = Guid.NewGuid(), Name = "Ajeno", IsTemplate = false, PatientId = foreign.Id,
            DurationDays = 30, Status = NutritionPlanStatus.Active, ReviewStatus = "approved", ApprovedAt = DateTime.UtcNow, ApprovedBy = foreign.UserId };
        var draft = new MediaItem { Id = Guid.NewGuid(), Title = "Borrador", Author = "Equipo", StorageKey = "media/draft.mp3", MediaType = MediaType.Podcast, Status = MediaStatus.Draft };
        db.NutritionPlans.Add(privatePlan); db.MediaItems.Add(draft);
        db.WeeklyDayTemplates.Add(new() { TemplateId = seed.Template, WeekNumber = 1, Weekday = 1, TaskCode = TaskCode.podcast, MediaId = draft.Id });
        var week = await db.ProgramWeeks.SingleAsync(w => w.EnrollmentId == seed.Enrollment && w.WeekNumber == 1);
        var rows = WeekSnapshotJson.Parse(week.TasksSnapshot).Select(t => t.TaskCode == "podcast" ? t with { Origin = "individual", MediaId = draft.Id } : t).ToList();
        week.TasksSnapshot = WeekSnapshotJson.Serialize(rows); await db.SaveChangesAsync();
        var store = new EffectiveDayContentStore(db); var loaded = await store.LoadAsync([seed.Enrollment], seed.Start, seed.Start, CancellationToken.None);
        Assert.DoesNotContain(loaded.Plans, p => p.Id == privatePlan.Id);
        var snapshot = await EffectiveRepository(db).GetSnapshotAsync(seed.Enrollment, seed.Start, CancellationToken.None);
        var podcast = Assert.Single(snapshot!.TodayTasks, t => t.TaskCode == TaskCode.podcast).Content;
        Assert.Equal(seed.Podcast, podcast!.MediaId); Assert.False(podcast.ContentUnavailable);
        var nutrition = Assert.Single(snapshot.TodayTasks, t => t.TaskCode == TaskCode.nut).Content;
        Assert.Equal(seed.Plan, nutrition!.NutritionPlanId); Assert.NotEmpty(nutrition.NutritionMeals!);
        rows = rows.Select(t => t.TaskCode == "ejercicio" ? t with { Origin = "individual", Rest = true } : t).ToList();
        week.TasksSnapshot = WeekSnapshotJson.Serialize(rows); await db.SaveChangesAsync();
        snapshot = await EffectiveRepository(db).GetSnapshotAsync(seed.Enrollment, seed.Start, CancellationToken.None);
        Assert.DoesNotContain(snapshot!.TodayTasks, t => t.TaskCode == TaskCode.ejercicio);
    }

    [NutritionPostgresFact]
    public async Task Effective_CambioPlantillaSoloLlegaAFutura_ConservaPodcastProgramIniciado()
    {
        await using var db = Context(); var seed = await EffectiveSeedAsync(db);
        var changed = new MediaItem { Id = Guid.NewGuid(), Title = "Nuevo general", Author = "Equipo", MediaType = MediaType.Podcast,
            Status = MediaStatus.Published, StorageKey = "media/podcasts/nuevo.mp3" };
        db.MediaItems.Add(changed);
        var monday = await db.WeeklyDayTemplates.SingleAsync(d => d.TemplateId == seed.Template && d.Weekday == 1 && d.TaskCode == TaskCode.podcast);
        monday.MediaId = changed.Id;
        // Un programa terminado solapado no cambia la identidad de la inscripción solicitada.
        db.ProgramEnrollments.Add(new() { Id = Guid.NewGuid(), PatientId = seed.Patient, TemplateId = seed.Template,
            StartLocalDate = seed.Start, Status = ProgramEnrollmentStatus.Completed, CreatedAt = DateTime.UtcNow.AddMinutes(1) });
        await db.SaveChangesAsync();
        var today = await EffectiveRepository(db).GetSnapshotAsync(seed.Enrollment, seed.Start, CancellationToken.None);
        Assert.Equal(seed.Podcast, Assert.Single(today!.TodayTasks, t => t.TaskCode == TaskCode.podcast).Content!.MediaId);
        var resolver = new EffectiveDayContentResolver(new EffectiveDayContentStore(db));
        Assert.Equal("snapshot", (await resolver.ResolveAsync(seed.Enrollment, seed.Start, CancellationToken.None)).Podcast.Source);
        Assert.Equal(changed.Id, (await resolver.ResolveAsync(seed.Enrollment, seed.Start.AddDays(7), CancellationToken.None)).Podcast.Id);
        var frozen = await db.ProgramWeeks.AsNoTracking().SingleAsync(w => w.EnrollmentId == seed.Enrollment && w.WeekNumber == 1);
        Assert.Equal(seed.Podcast, WeekSnapshotJson.Parse(frozen.TasksSnapshot).Single(t => t.Weekday == 1 && t.TaskCode == "podcast").MediaId);
    }

    [NutritionPostgresFact]
    public Task Effective_ActivacionPorFechaConservaIndividualYCompletaPrograma() => EffectiveActivationAsync(false);
    [NutritionPostgresFact]
    public Task Effective_ActivacionPorSemanaPerfectaConservaIndividualYCompletaPrograma() => EffectiveActivationAsync(true);

    private async Task EffectiveActivationAsync(bool perfect)
    {
        await using var db = Context(); var seed = await EffectiveSeedAsync(db);
        var first = await db.ProgramWeeks.SingleAsync(w => w.EnrollmentId == seed.Enrollment && w.WeekNumber == 1);
        first.TasksSnapshot = WeekSnapshotJson.Serialize(WeekSnapshotJson.Parse(first.TasksSnapshot).Where(t => t.TaskCode == "podcast").ToList());
        var personal = new MediaItem { Id = Guid.NewGuid(), Title = "Individual", Author = "Equipo", MediaType = MediaType.Podcast,
            StorageKey = "media/podcasts/individual.mp3", Status = MediaStatus.Published };
        db.MediaItems.Add(personal);
        var future = await db.ProgramWeeks.SingleAsync(w => w.EnrollmentId == seed.Enrollment && w.WeekNumber == 2);
        future.TasksSnapshot = WeekSnapshotJson.Serialize([new(4, "podcast", 77, 1, MediaId: personal.Id, Origin: "individual")]);
        await db.SaveChangesAsync();
        if (perfect)
            for (var d = 0; d < 7; d++)
                await EffectiveRepository(db).CompleteTaskAsync(new(seed.Enrollment, seed.Start.AddDays(d), TaskCode.podcast, null, null, null, null, null), CancellationToken.None);
        else
            await EffectiveRepository(db).CompleteTaskAsync(new(seed.Enrollment, seed.Start.AddDays(7), TaskCode.podcast, null, null, null, null, null), CancellationToken.None);
        await db.Entry(future).ReloadAsync(); Assert.Equal(ProgramWeekStatus.Active, future.Status);
        Assert.Equal(personal.Id, WeekSnapshotJson.Parse(future.TasksSnapshot).Single(t => t.Weekday == 4 && t.TaskCode == "podcast").MediaId);
        var day = await new EffectiveDayContentResolver(new EffectiveDayContentStore(db)).ResolveAsync(seed.Enrollment, seed.Start.AddDays(7), CancellationToken.None);
        Assert.False(day.HasGaps); Assert.Equal(seed.Plan, day.Nutrition.Id);
    }

    [NutritionPostgresFact]
    public async Task Effective_LegadoDia8CompletaConReferenciaExactaYMigracionReversible()
    {
        await using var db = Context(); var seed = await EffectiveSeedAsync(db);
        var patient = await db.PatientProfiles.SingleAsync(p => p.Id == seed.Patient);
        await db.WeeklyDayTemplates.Where(d => d.TemplateId == seed.Template && d.TaskCode == TaskCode.nut)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.NutritionPlanId, (Guid?)null));
        var legacy = new NutritionPlan { Id = Guid.NewGuid(), Name = "Legado 30", DurationDays = 30, PatientId = seed.Patient,
            IsTemplate = false, Status = NutritionPlanStatus.Active, ReviewStatus = "approved", ApprovedAt = seed.Start.AddDays(-8).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), ApprovedBy = patient.UserId,
            Days = Enumerable.Range(1, 30).Select(d => new NutritionPlanDay { Id = Guid.NewGuid(), DayNumber = d, MealType = MealType.Desayuno, Foods = $"Menú día {d}" }).ToList() };
        db.NutritionPlans.Add(legacy);
        db.NutritionPlanAssignments.Add(new() { PatientId = seed.Patient, PlanId = legacy.Id, StartDate = seed.Start.AddDays(-7).ToDateTime(TimeOnly.MinValue), Status = AssignmentStatus.Active });
        await db.SaveChangesAsync();
        var repo = EffectiveRepository(db); var snapshot = await repo.GetSnapshotAsync(seed.Enrollment, seed.Start, CancellationToken.None);
        var content = Assert.Single(snapshot!.TodayTasks, t => t.TaskCode == TaskCode.nut).Content;
        Assert.Equal(8, content!.NutritionPlanDayNumber); Assert.Equal("Menú día 8", Assert.Single(content.NutritionMeals!).Foods);
        await repo.LogNutritionAsync(seed.Patient, MealCode.des, seed.Start, ct: CancellationToken.None);
        await repo.CompleteTaskAsync(new(seed.Enrollment, seed.Start, TaskCode.nut, "legacy-8", null, null, null, null), CancellationToken.None);
        var completion = await db.TaskCompletions.AsNoTracking().SingleAsync(); Assert.Equal((short?)8, completion.NutritionPlanDayNumber);
        var migrations = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        var current = migrations.Single(m => m.EndsWith("_AllowThirtyDayLegacyNutritionCompletions"));
        var previous = migrations[Array.IndexOf(migrations, current) - 1]; var migrator = db.GetService<IMigrator>();
        await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync(previous));
        // Solo la fila sintética de esta BD efímera se ajusta para probar Down/Up sin pérdida de registros.
        await db.TaskCompletions.ExecuteUpdateAsync(s => s.SetProperty(t => t.NutritionPlanDayNumber, (short?)6));
        await migrator.MigrateAsync(previous);
        // Volumen anterior al Up; no representa pacientes ni mediciones reales.
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO app.task_completions (id,enrollment_id,program_week_id,daily_checkin_id,local_date,weekday,task_code,points_awarded,completed_at,source_ref_type) SELECT gen_random_uuid(),{completion.EnrollmentId},{completion.ProgramWeekId},{completion.DailyCheckinId},{completion.LocalDate} - n,1,'podcast',0,now(),'volume' FROM generate_series(1,10000) n");
        await migrator.MigrateAsync(current);
        Assert.Equal(10001, await db.TaskCompletions.CountAsync());
        Assert.True(await db.TaskCompletions.AnyAsync(t => t.Id == completion.Id));
    }

    [NutritionPostgresFact]
    public async Task Effective_ContinuidadYUltimoMenuCoincidenEnMisionPlanYCalendario()
    {
        await using var db = Context(); var seed = await EffectiveSeedAsync(db);
        var patient = await db.PatientProfiles.SingleAsync(p => p.Id == seed.Patient);
        var previous = new NutritionPrescription { PatientId = seed.Patient, State = "aprobado", IsPublished = true,
            StartDate = seed.Start.AddDays(-30), EndDate = seed.Start.AddDays(-1), CreatedBy = patient.UserId!.Value,
            ApprovedAt = seed.Start.AddDays(-30).ToDateTime(new TimeOnly(15, 0), DateTimeKind.Utc), ApprovedBy = patient.UserId,
            Meals = [new() { DayNumber = 30, MealType = "des", MenuName = "Último menú", ScheduledTime = new(7, 0) }] };
        var next = new NutritionPrescription { PatientId = seed.Patient, State = "aprobado", IsPublished = true,
            StartDate = seed.Start.AddDays(2), EndDate = seed.Start.AddDays(31), CreatedBy = patient.UserId.Value,
            ApprovedAt = seed.Start.AddDays(2).ToDateTime(new TimeOnly(15, 0), DateTimeKind.Utc), ApprovedBy = patient.UserId,
            Meals = [new() { DayNumber = 1, MealType = "des", MenuName = "Menú nuevo", ScheduledTime = new(7, 0) }] };
        db.NutritionPrescriptions.AddRange(previous, next); await db.SaveChangesAsync();
        var store = new EffectiveDayContentStore(db); var resolver = new EffectiveDayContentResolver(store);
        var bridge = new ProgramContentResolver(new WellnessRepository(db), NullLogger<ProgramContentResolver>.Instance,
            new NutritionPrescriptionRepository(db), store, resolver);
        var calendar = await bridge.ResolveRangeAsync(seed.Patient, seed.Start, seed.Start.AddDays(2), CancellationToken.None);
        Assert.Equal(previous.Id, calendar[seed.Start].SourcePrescriptionId); Assert.Equal(30, calendar[seed.Start].PrescriptionDayNumber);
        Assert.Equal(next.Id, calendar[seed.Start.AddDays(2)].SourcePrescriptionId);
        var snapshot = await EffectiveRepository(db).GetSnapshotAsync(seed.Enrollment, seed.Start, CancellationToken.None);
        var menu = Assert.Single(snapshot!.TodayTasks, t => t.TaskCode == TaskCode.nut).Content;
        Assert.Equal(previous.Id, menu!.SourcePrescriptionId); Assert.Null(menu.NutritionPlanId); Assert.Equal(30, menu.NutritionPlanDayNumber);
        var detail = await EffectiveRepository(db).GetEnrollmentWeekDetailAsync(seed.Enrollment, 1, Guid.Empty, CancellationToken.None);
        var detailNutrition = Assert.Single(detail!.Days[0].Tasks, t => t.TaskCode == "nut");
        Assert.Equal(previous.Id, detailNutrition.SourcePrescriptionId); Assert.Equal("continuity", detailNutrition.Source);
        var patients = NSubstitute.Substitute.For<CoppAddresd.Application.Interfaces.IPatientRepository>();
        patients.GetByUserIdAsync(patient.UserId.Value, Arg.Any<CancellationToken>()).Returns(patient);
        var clock = new EffectiveClock(seed.Start.ToDateTime(new TimeOnly(18, 0), DateTimeKind.Utc));
        var myPlan = await new CoppAddresd.Application.Features.Nutrition.Queries.GetMyNutritionPlan.GetMyNutritionPlanQueryHandler(
            patients, new WellnessRepository(db), NullLogger<CoppAddresd.Application.Features.Nutrition.Queries.GetMyNutritionPlan.GetMyNutritionPlanQueryHandler>.Instance,
            new NutritionPrescriptionRepository(db), clock, bridge).Handle(new(patient.UserId.Value), CancellationToken.None);
        Assert.Equal(previous.Id, myPlan!.Id); Assert.Equal(30, myPlan.CurrentDay);
        Assert.Equal(30, Assert.Single(myPlan.Days).DayNumber);
        previous.IsPublished = false; await db.SaveChangesAsync();
        Assert.Equal("engine", (await resolver.ResolveAsync(seed.Enrollment, seed.Start, CancellationToken.None)).Nutrition.Source);
    }

    private sealed class EffectiveClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc);
    }
    [NutritionPostgresFact]
    public async Task Effective_MotorSoloUsaDecisionFinalDelPacienteDeLaInscripcion()
    {
        await using var db = Context(); var seed = await EffectiveSeedAsync(db); var foreign = await PatientAsync(db);
        var recommendation = new ProgramAssignmentRecommendation { Id = Guid.NewGuid(), EnrollmentId = seed.Enrollment,
            PatientId = seed.Patient, State = "proposed", Proposal = JsonSerializer.Serialize(new { nutritionPlan = new { id = seed.Plan } }) };
        db.ProgramAssignmentRecommendations.Add(recommendation); await db.SaveChangesAsync();
        var store = new EffectiveDayContentStore(db);
        async Task<EffectiveContentData> Load() => await store.LoadAsync([seed.Enrollment], seed.Start, seed.Start, CancellationToken.None);
        Assert.Empty((await Load()).EngineNutrition);
        recommendation.State = "applied"; await db.SaveChangesAsync();
        Assert.Equal(seed.Plan, (await Load()).EngineNutrition[seed.Enrollment]);
        var foreignEnrollment = new ProgramEnrollment { Id = Guid.NewGuid(), PatientId = foreign.Id,
            TemplateId = seed.Template, StartLocalDate = seed.Start };
        db.ProgramEnrollments.Add(foreignEnrollment);
        recommendation.PatientId = foreign.Id; recommendation.EnrollmentId = foreignEnrollment.Id; await db.SaveChangesAsync();
        Assert.Empty((await Load()).EngineNutrition);
        recommendation.PatientId = seed.Patient; recommendation.EnrollmentId = seed.Enrollment; recommendation.State = "fallback";
        recommendation.Proposal = JsonSerializer.Serialize(new { nutritionPlan = new { id = seed.Plan }, appliedContent = new { nutritionPlan = (object?)null } });
        await db.SaveChangesAsync(); Assert.Empty((await Load()).EngineNutrition);
    }

}
