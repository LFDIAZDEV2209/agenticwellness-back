using CoppAddresd.Application.Features.ProgramProgress.Queries.Erp;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    [NutritionPostgresFact]
    public async Task Huecos_ResolvedorTresClasesPaginacionAlcanceAdminYSinEscrituras()
    {
        await using var db = Context(); var seed = await EffectiveSeedAsync(db); var foreign = await PatientAsync(db);
        var exhausted = await PatientAsync(db); var paused = await PatientAsync(db); var inactive = await PatientAsync(db);
        var professional = NutritionProfessional(seed.Patient); db.Professionals.Add(professional);
        db.PatientProfessionalAssignments.Add(new() { PatientId = seed.Patient, ProfessionalId = professional.Id });
        db.ProgramEnrollments.Add(new() { Id = Guid.NewGuid(), PatientId = foreign.Id, TemplateId = seed.Template, StartLocalDate = seed.Start });
        // Una inscripción inactiva y una agotada no generan huecos.
        db.ProgramEnrollments.Add(new() { Id = Guid.NewGuid(), PatientId = exhausted.Id, TemplateId = seed.Template,
            StartLocalDate = seed.Start.AddDays(-200), Status = ProgramEnrollmentStatus.Active });
        db.ProgramEnrollments.Add(new() { Id = Guid.NewGuid(), PatientId = paused.Id, TemplateId = seed.Template,
            StartLocalDate = seed.Start, Status = ProgramEnrollmentStatus.Paused });
        inactive.Status = "Inactivo";
        db.ProgramEnrollments.Add(new() { Id = Guid.NewGuid(), PatientId = inactive.Id, TemplateId = seed.Template, StartLocalDate = seed.Start });
        await db.SaveChangesAsync();
        var actor = Substitute.For<IPatientScopeActor>(); actor.UserId.Returns(Guid.NewGuid());
        actor.ProfessionalIdAsync(Arg.Any<CancellationToken>()).Returns(professional.Id);
        actor.HasPermissionAsync("Program.View", Arg.Any<CancellationToken>()).Returns(true);
        var store = new EffectiveDayContentStore(db, new PatientScopeGuard(actor, new PatientScopeStore(db)));
        var handler = new GetContentGapsQueryHandler(store, new EffectiveDayContentResolver(store),
            new EffectiveClock(seed.Start.AddDays(7).ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc)));
        Assert.Equal(0, (await handler.Handle(new(), CancellationToken.None)).Total);
        // Retirar las tres fuentes fuerza huecos incluso con snapshots futuros vacíos.
        await db.MediaItems.Where(m => m.Id == seed.Podcast).ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, MediaStatus.Draft));
        await db.ExerciseRoutines.Where(r => r.Id == seed.Routine).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, NutritionPlanStatus.Archived));
        await db.NutritionPlans.Where(p => p.Id == seed.Plan).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, NutritionPlanStatus.Archived));
        var snapshots = (await db.ProgramWeeks.AsNoTracking().OrderBy(w => w.Id).Select(w => w.TasksSnapshot).ToArrayAsync()).Select(s => s.GetRawText()).ToArray();
        var own = await handler.Handle(new(1, 2), CancellationToken.None);
        Assert.Equal(76, own.Total); Assert.Equal(38, own.TotalPages); Assert.Equal(2, own.Items.Count);
        Assert.All(own.Items, d => { Assert.Equal(seed.Patient, d.PatientId); Assert.Equal(new[] { "podcast", "ejercicio", "nut" }, d.MissingClasses); });
        Assert.Equal(seed.Start.AddDays(7), own.Items[0].Date);
        var last = await handler.Handle(new(38, 2), CancellationToken.None);
        Assert.Equal(seed.Start.AddDays(82), last.Items[^1].Date);
        Assert.Empty((await handler.Handle(new(int.MaxValue, 100), CancellationToken.None)).Items);
        actor.IsAdmin.Returns(true);
        var all = await handler.Handle(new(1, 100), CancellationToken.None);
        Assert.Equal(152, all.Total); Assert.Contains(all.Items, d => d.PatientId == foreign.Id);
        Assert.Equal(snapshots, (await db.ProgramWeeks.AsNoTracking().OrderBy(w => w.Id).Select(w => w.TasksSnapshot).ToArrayAsync()).Select(s => s.GetRawText()).ToArray());
        actor.HasPermissionAsync("Program.View", Arg.Any<CancellationToken>()).Returns(false);
        await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(new(), CancellationToken.None));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.Handle(new(), cancellation.Token));
    }
}
