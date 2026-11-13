using CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent;
using CoppAddresd.Application.Features.ProgramProgress.Commands.PublishTemplate;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    [NutritionPostgresFact]
    public async Task Impacto_UsaResolvedorAlcanceAdminYNoPublicaNiEscribeSnapshots()
    {
        await using var db = Context(); var seed = await EffectiveSeedAsync(db); var foreign = await PatientAsync(db);
        var professional = NutritionProfessional(seed.Patient); db.Professionals.Add(professional);
        db.PatientProfessionalAssignments.Add(new() { PatientId = seed.Patient, ProfessionalId = professional.Id });
        db.ProgramEnrollments.Add(new() { Id = Guid.NewGuid(), PatientId = foreign.Id, TemplateId = seed.Template, StartLocalDate = seed.Start });
        var future = await db.ProgramWeeks.SingleAsync(w => w.EnrollmentId == seed.Enrollment && w.WeekNumber == 2);
        future.TasksSnapshot = WeekSnapshotJson.Serialize([new(1, "podcast", 1, 1, MediaId: seed.Podcast, Origin: "individual")]);
        await db.SaveChangesAsync();
        var before = WeekSnapshotJson.Parse(future.TasksSnapshot).ToArray();
        var version = await db.ProgramTemplates.Where(t => t.Id == seed.Template).Select(t => t.Version).SingleAsync();
        var actor = Substitute.For<IPatientScopeActor>(); actor.UserId.Returns(Guid.NewGuid());
        actor.ProfessionalIdAsync(Arg.Any<CancellationToken>()).Returns(professional.Id);
        actor.HasPermissionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var guard = new PatientScopeGuard(actor, new PatientScopeStore(db));
        var store = new EffectiveDayContentStore(db, guard); var resolver = new EffectiveDayContentResolver(store);
        var handler = new PreviewTemplateImpactQueryHandler(new ProgramRepository(db, new ConfigurationBuilder().Build()), store, resolver,
            new EffectiveClock(seed.Start.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc)));
        var own = await handler.Handle(new(seed.Template), CancellationToken.None);
        Assert.Equal(new TemplateImpactDto(1, 1, 1), own);
        actor.IsAdmin.Returns(true);
        var all = await handler.Handle(new(seed.Template), CancellationToken.None);
        Assert.Equal(new TemplateImpactDto(2, 1, 2), all);
        await db.Entry(future).ReloadAsync(); Assert.Equal(before, WeekSnapshotJson.Parse(future.TasksSnapshot).ToArray());
        Assert.Equal(version, await db.ProgramTemplates.Where(t => t.Id == seed.Template).Select(t => t.Version).SingleAsync());
        actor.HasPermissionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(new(seed.Template), CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(new(Guid.NewGuid()), CancellationToken.None));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadScopedActiveAsync(seed.Template, "Program.Edit", cancellation.Token));
    }
}
