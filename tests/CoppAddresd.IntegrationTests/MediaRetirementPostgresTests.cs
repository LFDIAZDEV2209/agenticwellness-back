using CoppAddresd.Application.Features.Media;
using CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    [NutritionPostgresFact]
    public async Task Retiro_ConflictoSustitucionAlcanceHistorialIndividualYDeleteAtomico()
    {
        await using var db = Context(); var seed = await EffectiveSeedAsync(db); var foreign = await PatientAsync(db);
        var professional = NutritionProfessional(seed.Patient); db.Professionals.Add(professional);
        db.PatientProfessionalAssignments.Add(new() { PatientId = seed.Patient, ProfessionalId = professional.Id });
        db.ProgramEnrollments.Add(new() { Id = Guid.NewGuid(), PatientId = foreign.Id, TemplateId = seed.Template, StartLocalDate = seed.Start });
        var substitute = new MediaItem { Id = Guid.NewGuid(), Title = "Sustituto", Author = "Equipo", MediaType = MediaType.Podcast,
            Status = MediaStatus.Published, StorageKey = "media/podcasts/sustituto.mp3" };
        db.MediaItems.Add(substitute);
        var future = await db.ProgramWeeks.SingleAsync(w => w.EnrollmentId == seed.Enrollment && w.WeekNumber == 2);
        future.TasksSnapshot = WeekSnapshotJson.Serialize([new(1, "podcast", 10, 1, MediaId: seed.Podcast, Origin: "individual"),
            new(2, "podcast", 10, 1, MediaId: seed.Podcast, Origin: "program")]);
        await db.SaveChangesAsync();
        var frozen = WeekSnapshotJson.Parse((await db.ProgramWeeks.AsNoTracking().SingleAsync(w => w.EnrollmentId == seed.Enrollment && w.WeekNumber == 1)).TasksSnapshot).ToArray();
        var actor = Substitute.For<IPatientScopeActor>(); actor.UserId.Returns(Guid.NewGuid());
        actor.ProfessionalIdAsync(Arg.Any<CancellationToken>()).Returns(professional.Id);
        actor.HasPermissionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var content = new EffectiveDayContentStore(db, new PatientScopeGuard(actor, new PatientScopeStore(db)));
        var store = new MediaRetirementStore(db, content, new EffectiveDayContentResolver(content),
            new EffectiveClock(seed.Start.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc)));
        Assert.Equal(new MediaRetirementImpact(1, 1), await store.PreviewAsync(seed.Podcast, CancellationToken.None));
        actor.IsAdmin.Returns(true);
        Assert.Equal(new MediaRetirementImpact(2, 1), await store.PreviewAsync(seed.Podcast, CancellationToken.None));
        actor.IsAdmin.Returns(false);
        foreach (var operation in Enum.GetValues<MediaRetirementOperation>())
            Assert.True((await store.ExecuteAsync(new(seed.Podcast, operation), CancellationToken.None)).Blocked);
        var storage = Substitute.For<IObjectStorageService>();
        var handler = new RetireMediaCommandHandler(store, storage, NullLogger<RetireMediaCommandHandler>.Instance);
        var blockedDelete = await handler.Handle(new(seed.Podcast, MediaRetirementOperation.Delete, substitute.Id), CancellationToken.None);
        Assert.True(blockedDelete.Blocked);
        Assert.All(await db.WeeklyDayTemplates.AsNoTracking().Where(d => d.TaskCode == CoppAddresd.Domain.Enums.ProgramProgress.TaskCode.podcast).ToArrayAsync(), d => Assert.Equal(seed.Podcast, d.MediaId));
        Assert.Equal(seed.Podcast, WeekSnapshotJson.Parse((await db.ProgramWeeks.AsNoTracking().SingleAsync(w => w.Id == future.Id)).TasksSnapshot)[1].MediaId);
        await storage.DidNotReceive().DeleteObjectsAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
        var withdrawn = await handler.Handle(new(seed.Podcast, MediaRetirementOperation.Unpublish, substitute.Id), CancellationToken.None);
        Assert.False(withdrawn.Blocked); Assert.Equal(MediaStatus.Draft, withdrawn.Media.Status);
        Assert.All(await db.WeeklyDayTemplates.AsNoTracking().Where(d => d.TaskCode == CoppAddresd.Domain.Enums.ProgramProgress.TaskCode.podcast).ToArrayAsync(), d => Assert.Equal(substitute.Id, d.MediaId));
        var after = WeekSnapshotJson.Parse((await db.ProgramWeeks.AsNoTracking().SingleAsync(w => w.Id == future.Id)).TasksSnapshot);
        Assert.Equal(seed.Podcast, after[0].MediaId); Assert.Equal(substitute.Id, after[1].MediaId);
        Assert.Equal(frozen, WeekSnapshotJson.Parse((await db.ProgramWeeks.AsNoTracking().SingleAsync(w => w.EnrollmentId == seed.Enrollment && w.WeekNumber == 1)).TasksSnapshot).ToArray());
        Assert.Equal(MediaStatus.Archived, (await handler.Handle(new(seed.Podcast, MediaRetirementOperation.Archive, substitute.Id), CancellationToken.None)).Media.Status);
        await Assert.ThrowsAsync<UnprocessableEntityException>(() => store.ExecuteAsync(new(seed.Podcast, MediaRetirementOperation.Archive, seed.Podcast), CancellationToken.None));
        actor.HasPermissionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        await Assert.ThrowsAsync<ForbiddenException>(() => store.PreviewAsync(seed.Podcast, CancellationToken.None));
        actor.HasPermissionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var orphan = new MediaItem { Id = Guid.NewGuid(), Title = "Sin referencias", Author = "Equipo", StorageKey = "media/podcasts/huerfano.mp3" };
        db.MediaItems.Add(orphan); await db.SaveChangesAsync();
        Assert.True((await handler.Handle(new(orphan.Id, MediaRetirementOperation.Delete), CancellationToken.None)).Deleted);
        Assert.False(await db.MediaItems.AsNoTracking().AnyAsync(m => m.Id == orphan.Id));
        await storage.Received(1).DeleteObjectsAsync(Arg.Is<IEnumerable<string>>(keys => keys.Contains(orphan.StorageKey)), Arg.Any<CancellationToken>());
    }
}
