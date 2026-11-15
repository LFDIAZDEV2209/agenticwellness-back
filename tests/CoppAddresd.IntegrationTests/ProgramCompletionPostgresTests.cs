using System.Text.Json;
using CoppAddresd.Application.DTOs.ProgramProgress;
using CoppAddresd.Application.Features.ProgramProgress.Commands.ReconcileStreaks;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    private async Task<(Guid PatientId, Guid EnrollmentId, Guid TemplateId, DateOnly Start)> CompletionSeedAsync()
    {
        await using var db = Context();
        var patient = await PatientAsync(db);
        var start = new DateOnly(2026, 8, 24); // Lunes; día 83 = sábado 14 de noviembre.
        var template = new ProgramTemplate { Id = Guid.NewGuid(), Code = Guid.NewGuid().ToString("N"),
            Name = "Programa de cierre QA", TotalWeeks = 12, Status = TemplateStatus.Active };
        db.ProgramTemplates.Add(template);
        var rows = Enumerable.Range(1, 7).Select(d => new WeeklyDayTemplate { Id = Guid.NewGuid(),
            TemplateId = template.Id, Weekday = (short)d, TaskCode = TaskCode.podcast, Points = 10, SortOrder = 1 }).ToArray();
        db.WeeklyDayTemplates.AddRange(rows);
        var enrollment = new ProgramEnrollment { Id = Guid.NewGuid(), PatientId = patient.Id, TemplateId = template.Id,
            StartLocalDate = start, Timezone = "America/Bogota", CurrentWeekNumber = 12 };
        db.ProgramEnrollments.Add(enrollment);
        db.StreakStates.Add(new() { EnrollmentId = enrollment.Id });
        for (var week = 1; week <= 12; week++)
        {
            var date = start.AddDays((week - 1) * 7);
            db.ProgramWeeks.Add(new() { Id = Guid.NewGuid(), EnrollmentId = enrollment.Id, WeekNumber = week,
                Status = week == 12 ? ProgramWeekStatus.Active : ProgramWeekStatus.Completed,
                WeekStartDateLocal = date, WeekEndDateLocal = date.AddDays(6), TemplateVersionAtStart = 1,
                // Conserva deliberadamente la fila histórica de día 84; el límite canónico debe rechazarla.
                TasksSnapshot = JsonSerializer.SerializeToElement(rows.Select(d => new { weekday = d.Weekday,
                    task_code = "podcast", points = 10, sort_order = 1 })) });
        }
        await db.SaveChangesAsync();
        return (patient.Id, enrollment.Id, template.Id, start);
    }

    private static ProgramRepository CompletionRepository(CoppAddresd.Infrastructure.Persistence.AppDbContext db, DateTime now)
        => new(db, new ConfigurationBuilder().Build(), clock: new EffectiveClock(now));

    [NutritionPostgresFact]
    public async Task ProgramaCierre_SeisDiasFinales_CierraSinRepetirYReintentoUltimaTareaEsReplay()
    {
        var seed = await CompletionSeedAsync();
        await using var db = Context();
        var repository = CompletionRepository(db, new(2026, 11, 14, 18, 0, 0, DateTimeKind.Utc));
        for (var day = 77; day < 82; day++)
            await repository.CompleteTaskAsync(new CompleteTaskInput(seed.EnrollmentId, seed.Start.AddDays(day), TaskCode.podcast, null, null, null, null, null), default);
        Assert.Null(await repository.GetCompletionAsync(seed.PatientId, default));
        var last = new CompleteTaskInput(seed.EnrollmentId, seed.Start.AddDays(82), TaskCode.podcast, null, null, null, null, "ultimo-dia83");
        var completed = await repository.CompleteTaskAsync(last, default);
        Assert.Equal(CompleteTaskOutcome.Created, completed.Outcome);
        var replay = await repository.CompleteTaskAsync(last, default);
        Assert.Equal(CompleteTaskOutcome.Replay, replay.Outcome);
        // El contrato de replay conserva los puntos originales, sin volver a otorgarlos.
        Assert.Equal(completed.TaskCompletionId, replay.TaskCompletionId);
        Assert.Equal(completed.XpBalanceAfter, replay.XpBalanceAfter);
        var summary = (await repository.GetCompletionAsync(seed.PatientId, default))!;
        Assert.Equal(83, summary.TotalDays); Assert.Equal(seed.Start.AddDays(82), summary.EndLocalDate);
        Assert.Equal(12, summary.CompletedWeeks); Assert.Equal(6, summary.DaysWithActivity);
        Assert.Equal(6, summary.PerfectDays); Assert.Equal(6, summary.TasksCompleted);
        Assert.True(summary.CanStartNewProgram); Assert.True(summary.XpEarned > 0);
        Assert.Equal(new DateTime(2026, 11, 14, 18, 0, 0, DateTimeKind.Utc), summary.CompletedAt);
        Assert.Equal(1, await db.ProgramEnrollments.CountAsync(e => e.PatientId == seed.PatientId));
        Assert.Equal(12, await db.ProgramWeeks.CountAsync(w => w.EnrollmentId == seed.EnrollmentId));
        Assert.Equal(summary, await repository.GetCompletionAsync(seed.PatientId, default));
        Assert.Null(await repository.GetCompletionAsync(Guid.NewGuid(), default));
    }

    [NutritionPostgresFact]
    public async Task ProgramaCierre_Dia84Rechazado_SinTareaNiXp()
    {
        var seed = await CompletionSeedAsync(); await using var db = Context();
        var repository = CompletionRepository(db, new(2026, 11, 15, 18, 0, 0, DateTimeKind.Utc));
        await Assert.ThrowsAsync<UnprocessableEntityException>(() => repository.CompleteTaskAsync(
            new CompleteTaskInput(seed.EnrollmentId, seed.Start.AddDays(83), TaskCode.podcast, null, null, null, null, null), default));
        Assert.Empty(await db.TaskCompletions.Where(t => t.EnrollmentId == seed.EnrollmentId).ToListAsync());
        Assert.Empty(await db.XpLedgerEntries.Where(t => t.EnrollmentId == seed.EnrollmentId).ToListAsync());
    }

    [NutritionPostgresFact]
    public async Task ProgramaCierre_JobPorFechaLocal_CierraIncompletoUnaVezYExigeNuevoInicioExplicito()
    {
        var seed = await CompletionSeedAsync(); await using var db = Context();
        var beforeMidnight = CompletionRepository(db, new(2026, 11, 15, 4, 59, 0, DateTimeKind.Utc));
        Assert.Equal(0, await beforeMidnight.CompleteExpiredProgramsAsync(default));
        var repository = CompletionRepository(db, new(2026, 11, 15, 5, 0, 0, DateTimeKind.Utc));
        // El job ya existente llama al cierre; ninguna apertura de la app es necesaria.
        var job = new ReconcileStreakJob(repository, NullLogger<ReconcileStreakJob>.Instance);
        await job.RunAsync();
        var summary = (await repository.GetCompletionAsync(seed.PatientId, default))!;
        Assert.Equal(0, summary.TasksCompleted); Assert.Equal(0, summary.PerfectDays);
        Assert.Equal(12, summary.CompletedWeeks); Assert.True(summary.CanStartNewProgram);
        Assert.Equal(0, await repository.CompleteExpiredProgramsAsync(default));
        Assert.Equal(summary, await repository.GetCompletionAsync(seed.PatientId, default));
        Assert.Equal(1, await db.ProgramEnrollments.CountAsync(e => e.PatientId == seed.PatientId));
        var next = await repository.EnrollAsync(seed.PatientId, seed.TemplateId, "America/Bogota", new(2026, 11, 16));
        Assert.NotEqual(seed.EnrollmentId, next.Id);
        var after = (await repository.GetCompletionAsync(seed.PatientId, default))!;
        Assert.Equal(summary with { CanStartNewProgram = false }, after);
        Assert.Equal(2, await db.ProgramEnrollments.CountAsync(e => e.PatientId == seed.PatientId));
        Assert.Equal(new DateOnly(2027, 2, 6), await db.ProgramWeeks.Where(w => w.EnrollmentId == next.Id && w.WeekNumber == 12).Select(w => w.WeekEndDateLocal).SingleAsync());
    }

    [NutritionPostgresFact]
    public async Task ProgramaCierre_Concurrente_SoloUnaPasadaCierraYSinCambiarPausadoOLegado()
    {
        var seed = await CompletionSeedAsync(); await using var setup = Context();
        var pausedPatient = await PatientAsync(setup);
        setup.ProgramEnrollments.Add(new() { Id = Guid.NewGuid(), PatientId = pausedPatient.Id, TemplateId = seed.TemplateId,
            StartLocalDate = seed.Start, Status = ProgramEnrollmentStatus.Paused });
        var legacyPatient = await PatientAsync(setup);
        var legacy = new ProgramTemplate { Id = Guid.NewGuid(), Code = Guid.NewGuid().ToString("N"), Name = "Legado", TotalWeeks = 83 };
        setup.ProgramTemplates.Add(legacy);
        setup.ProgramEnrollments.Add(new() { Id = Guid.NewGuid(), PatientId = legacyPatient.Id, TemplateId = legacy.Id, StartLocalDate = seed.Start });
        await setup.SaveChangesAsync();
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CompletionRepository(setup,
                new(2026, 11, 15, 6, 0, 0, DateTimeKind.Utc)).CompleteExpiredProgramsAsync(cancelled.Token));
        }
        // Un fallo al sellar la inscripción revierte también las semanas de la misma sentencia.
        await setup.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION app.reject_program_closure_qa() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.status='Completed' THEN RAISE EXCEPTION 'rollback cierre QA'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER reject_program_closure_qa BEFORE UPDATE ON app.program_enrollments
            FOR EACH ROW EXECUTE FUNCTION app.reject_program_closure_qa();
            """);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => CompletionRepository(setup,
            new(2026, 11, 15, 6, 0, 0, DateTimeKind.Utc)).CompleteExpiredProgramsAsync(default));
        Assert.Equal(ProgramWeekStatus.Active, await setup.ProgramWeeks.AsNoTracking()
            .Where(w => w.EnrollmentId == seed.EnrollmentId && w.WeekNumber == 12).Select(w => w.Status).SingleAsync());
        await setup.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_program_closure_qa ON app.program_enrollments; DROP FUNCTION app.reject_program_closure_qa();");
        await using var a = Context(); await using var b = Context();
        var now = new DateTime(2026, 11, 15, 6, 0, 0, DateTimeKind.Utc);
        var closed = await Task.WhenAll(CompletionRepository(a, now).CompleteExpiredProgramsAsync(default), CompletionRepository(b, now).CompleteExpiredProgramsAsync(default));
        Assert.Equal(1, closed.Sum());
        Assert.Null(await CompletionRepository(setup, now).GetCompletionAsync(pausedPatient.Id, default));
        Assert.Null(await CompletionRepository(setup, now).GetCompletionAsync(legacyPatient.Id, default));
    }
}
