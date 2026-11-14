using System.Data.Common;
using CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Persistence;
using CoppAddresd.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    [NutritionPostgresFact]
    public async Task PodcastMasivo_DiezPacientes_DosIniciadosDosAjenos_IndividualIdempotenteYAdmin()
    {
        await using var seedDb = Context(); var seed = await EffectiveSeedAsync(seedDb);
        var professional = NutritionProfessional(seed.Patient); seedDb.Professionals.Add(professional);
        var patients = new List<Guid> { seed.Patient };
        var target = new MediaItem { Id = Guid.NewGuid(), Title = "Podcast del lote", Author = "Equipo", MediaType = MediaType.Podcast,
            Status = MediaStatus.Published, StorageKey = "media/podcasts/lote-pacientes.mp3" };
        seedDb.MediaItems.Add(target);
        for (var i = 0; i < 10; i++)
        {
            var patientId = i == 0 ? seed.Patient : (await PatientAsync(seedDb)).Id;
            if (i > 0) patients.Add(patientId);
            if (i < 8) seedDb.PatientProfessionalAssignments.Add(new() { PatientId = patientId, ProfessionalId = professional.Id });
            var enrollmentId = seed.Enrollment;
            if (i > 0)
            {
                enrollmentId = Guid.NewGuid();
                seedDb.ProgramEnrollments.Add(new() { Id = enrollmentId, PatientId = patientId, TemplateId = seed.Template,
                    StartLocalDate = i == 7 ? seed.Start.AddDays(-7) : seed.Start });
            }
            if (i == 1) continue; // Semana todavía no creada: la asignación debe preservarse hasta la activación.
            var week = i == 0 ? await seedDb.ProgramWeeks.SingleAsync(w => w.EnrollmentId == enrollmentId && w.WeekNumber == 2)
                : new ProgramWeek { Id = Guid.NewGuid(), EnrollmentId = enrollmentId, WeekNumber = 2,
                    WeekStartDateLocal = i == 7 ? seed.Start : seed.Start.AddDays(7), WeekEndDateLocal = seed.Start.AddDays(13),
                    Status = i == 6 ? ProgramWeekStatus.Active : ProgramWeekStatus.Locked };
            week.TasksSnapshot = WeekSnapshotJson.Serialize([new(1, "podcast", 10, 1, MediaId: seed.Podcast, Origin: "program"),
                new(1, "ejercicio", 17, 2, RoutineId: seed.Routine, Origin: "individual")]);
            if (i > 0) seedDb.ProgramWeeks.Add(week);
        }
        await seedDb.SaveChangesAsync();
        var counter = new BulkPodcastReadCounter();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection)
            .AddInterceptors(counter).Options);
        var actor = Substitute.For<IPatientScopeActor>(); actor.UserId.Returns(Guid.NewGuid());
        actor.ProfessionalIdAsync(Arg.Any<CancellationToken>()).Returns(professional.Id);
        actor.HasPermissionAsync("Program.Edit", Arg.Any<CancellationToken>()).Returns(true);
        var store = new BulkPatientPodcastStore(db, new PatientScopeGuard(actor, new PatientScopeStore(db)), actor,
            new EffectiveClock(seed.Start.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc)));
        var protectedIds = await db.ProgramWeeks.AsNoTracking().Where(w => w.WeekNumber == 2 && db.ProgramEnrollments
            .Any(e => e.Id == w.EnrollmentId && patients.Skip(6).Contains(e.PatientId))).Select(w => w.Id).ToArrayAsync();
        var before = await db.ProgramWeeks.AsNoTracking().Where(w => protectedIds.Contains(w.Id)).OrderBy(w => w.Id).Select(w => w.TasksSnapshot).ToArrayAsync();
        counter.Reads = 0;
        await store.AssignAsync(new([seed.Patient], target.Id, 2, 2), CancellationToken.None);
        var oneReads = counter.Reads; counter.Reads = 0;
        var request = new BulkAssignPatientPodcastsCommand(patients, target.Id, 2, 2);
        var result = await store.AssignAsync(request, CancellationToken.None);
        Assert.Equal(6, result.Aplicadas); Assert.Equal(2, result.OmitidasSemanaIniciada);
        Assert.Equal(patients.Skip(8), result.OmitidasSinAlcance); Assert.Equal(oneReads, counter.Reads);
        var after = await db.ProgramWeeks.AsNoTracking().Where(w => protectedIds.Contains(w.Id)).OrderBy(w => w.Id).Select(w => w.TasksSnapshot).ToArrayAsync();
        Assert.Equal(before.Select(x => x.GetRawText()), after.Select(x => x.GetRawText()));
        var ownFuture = await db.ProgramWeeks.AsNoTracking().Where(w => w.WeekNumber == 2 && db.ProgramEnrollments.Any(e =>
            e.Id == w.EnrollmentId && patients.Take(6).Contains(e.PatientId))).OrderBy(w => w.Id).ToArrayAsync();
        Assert.Equal(6, ownFuture.Length);
        foreach (var week in ownFuture)
        {
            var rows = WeekSnapshotJson.Parse(week.TasksSnapshot);
            Assert.Equal(7, rows.Count(r => r.TaskCode == "podcast"));
            Assert.All(rows.Where(r => r.TaskCode == "podcast"), r =>
            { Assert.Equal(target.Id, r.MediaId); Assert.Equal("individual", r.Origin); Assert.Equal(actor.UserId, r.EditedBy); Assert.NotNull(r.EditedAt); });
            if (rows.Any(r => r.TaskCode == "ejercicio")) Assert.Equal(17, rows.Single(r => r.TaskCode == "ejercicio").Points);
        }
        var timestamps = ownFuture.Select(w => w.UpdatedAt).ToArray();
        var repeated = await store.AssignAsync(request with { PatientIds = patients.Concat(patients).ToArray() }, CancellationToken.None);
        Assert.Equal(6, repeated.Aplicadas);
        Assert.Equal(timestamps, await db.ProgramWeeks.AsNoTracking().Where(w => ownFuture.Select(x => x.Id).ToArray().Contains(w.Id)).OrderBy(w => w.Id).Select(w => w.UpdatedAt).ToArrayAsync());
        actor.IsAdmin.Returns(true);
        var admin = await store.AssignAsync(request, CancellationToken.None);
        Assert.Equal(8, admin.Aplicadas); Assert.Empty(admin.OmitidasSinAlcance);
        actor.HasPermissionAsync("Program.Edit", Arg.Any<CancellationToken>()).Returns(false);
        await Assert.ThrowsAsync<ForbiddenException>(() => store.AssignAsync(request, CancellationToken.None));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.AssignAsync(request, cancelled.Token));
    }

    private sealed class BulkPodcastReadCounter : DbCommandInterceptor
    {
        public int Reads;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) Reads++;
            return ValueTask.FromResult(result);
        }
    }
}
