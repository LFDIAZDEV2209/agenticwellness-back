using System.Text.Json;
using CoppAddresd.Application.Features.ProgramProgress.Commands.CreateTemplate;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services;
using CoppAddresd.Domain.Entities;
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
    public async Task VersionPlantilla_Copia83YLoDemas_BorradorEditable_ActivaInscripcionesYOtraPlantillaIntactas()
    {
        await using var db = Context(); var seed = await EffectiveSeedAsync(db);
        var patient = await db.PatientProfiles.SingleAsync(p => p.Id == seed.Patient);
        await db.WeeklyDayTemplates.Where(d => d.TemplateId == seed.Template && d.TaskCode == TaskCode.podcast).ExecuteDeleteAsync();
        db.ChangeTracker.Clear();
        var source = await db.ProgramTemplates.SingleAsync(t => t.Id == seed.Template);
        source.Version = 4; source.Description = "Contenido aprobado"; source.StreakMinTasks = 2; source.EssentialTaskCodes = ["nut", "ejercicio"]; source.PublishedAt = DateTime.UtcNow;
        for (var week = 1; week <= 12; week++)
            for (short day = 1; day <= (week == 12 ? 6 : 7); day++)
                db.WeeklyDayTemplates.Add(new() { Id = Guid.NewGuid(), TemplateId = source.Id, WeekNumber = week, Weekday = day, TaskCode = TaskCode.podcast,
                    MediaId = seed.Podcast, Points = 10 + week, SortOrder = 1 });
        var foreignMedia = new MediaItem { Id = Guid.NewGuid(), Title = "Otro catálogo", Author = "Equipo", MediaType = MediaType.Podcast, Status = MediaStatus.Published, StorageKey = "media/otro.mp3" };
        var other = new ProgramTemplate { Id = Guid.NewGuid(), Code = Guid.NewGuid().ToString("N"), Name = "Otra plantilla", TotalWeeks = 12, Status = TemplateStatus.Active };
        db.MediaItems.Add(foreignMedia); db.ProgramTemplates.Add(other);
        db.WeeklyDayTemplates.Add(new() { TemplateId = other.Id, Weekday = 1, TaskCode = TaskCode.podcast, MediaId = foreignMedia.Id, Points = 300 });
        await db.SaveChangesAsync();
        var repo = EffectiveRepository(db);
        async Task<string> SourceState()
        {
            var dto = CoppAddresd.Application.DTOs.ProgramProgress.ProgramTemplateDto.FromEntity((await repo.GetTemplateAsync(seed.Template))!);
            return JsonSerializer.Serialize(dto with { Days = dto.Days.OrderBy(d => d.WeekNumber).ThenBy(d => d.Weekday).ThenBy(d => d.TaskCode).ToArray() });
        }
        var before = await SourceState(); var enrollmentCount = await db.ProgramEnrollments.CountAsync();
        var weekStates = (await db.ProgramWeeks.AsNoTracking().OrderBy(w => w.Id).Select(w => w.TasksSnapshot).ToArrayAsync()).Select(s => s.GetRawText()).ToArray();
        var actor = Substitute.For<IPatientScopeActor>(); actor.UserId.Returns(patient.UserId); actor.IsAdmin.Returns(true);
        actor.HasPermissionAsync("Program.Edit", Arg.Any<CancellationToken>()).Returns(true);
        var handler = new CreateTemplateVersionHandler(repo, new PatientScopeGuard(actor, new PatientScopeStore(db)), actor);
        var result = await handler.Handle(new(source.Id), default);
        Assert.NotEqual(source.Id, result.Id); Assert.Equal(TemplateStatus.Draft, result.Status); Assert.Equal(5, result.Version);
        var draft = await repo.GetTemplateAsync(result.Id);
        Assert.Equal(source.Name, draft!.Name); Assert.Equal(source.Description, draft.Description); Assert.Equal(source.StreakMinTasks, draft.StreakMinTasks);
        Assert.Equal(source.EssentialTaskCodes, draft.EssentialTaskCodes); Assert.Null(draft.PublishedAt); Assert.Equal(patient.UserId, draft.CreatedBy);
        Assert.Equal(83, draft.DayTemplates.Count(d => d.TaskCode == TaskCode.podcast)); Assert.Equal(14, draft.DayTemplates.Count(d => d.TaskCode != TaskCode.podcast));
        Assert.All(draft.DayTemplates.Where(d => d.TaskCode == TaskCode.podcast), d => Assert.Equal(seed.Podcast, d.MediaId));
        Assert.DoesNotContain(draft.DayTemplates, d => d.MediaId == foreignMedia.Id || d.WeekNumber == 12 && d.Weekday == 7);
        Assert.Equal(before, await SourceState());
        var edit = draft.DayTemplates.Where(d => d.TaskCode == TaskCode.podcast && d.WeekNumber == 1).ToArray(); edit[0].MediaId = foreignMedia.Id;
        await repo.ReplaceWeekdayTasksAsync(draft.Id, edit, patient.UserId, default, 1);
        Assert.Contains((await repo.GetTemplateAsync(draft.Id))!.DayTemplates, d => d.MediaId == foreignMedia.Id);
        Assert.Equal(before, await SourceState());
        Assert.Equal(enrollmentCount, await db.ProgramEnrollments.CountAsync());
        Assert.Equal(weekStates, (await db.ProgramWeeks.AsNoTracking().OrderBy(w => w.Id).Select(w => w.TasksSnapshot).ToArrayAsync()).Select(s => s.GetRawText()));
        Assert.Equal(1, await db.WeeklyDayTemplates.CountAsync(d => d.TemplateId == other.Id));
        var templateCount = await db.ProgramTemplates.CountAsync();
        await Assert.ThrowsAsync<BusinessRuleViolationException>(() => handler.Handle(new(draft.Id), default));
        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(new(Guid.NewGuid()), default));
        actor.HasPermissionAsync("Program.Edit", Arg.Any<CancellationToken>()).Returns(false);
        await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(new(source.Id), default));
        Assert.Equal(templateCount, await db.ProgramTemplates.CountAsync());
    }
}
