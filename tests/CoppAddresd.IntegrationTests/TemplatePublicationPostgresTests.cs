using CoppAddresd.Application.Features.ProgramProgress.Commands.PublishTemplate;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    [NutritionPostgresFact]
    public async Task Publicacion_HuecoRechazaSinCambiarVersion_CompletaPublica83Dias()
    {
        await using var db = Context(); var seed = await EffectiveSeedAsync(db);
        var template = await db.ProgramTemplates.SingleAsync(t => t.Id == seed.Template); template.Status = TemplateStatus.Draft;
        var generic = await db.WeeklyDayTemplates.SingleAsync(d => d.TemplateId == seed.Template && d.Weekday == 1 && d.TaskCode == TaskCode.podcast);
        db.WeeklyDayTemplates.Remove(generic);
        for (var n = 1; n <= 12; n++)
            if (n != 2) db.WeeklyDayTemplates.Add(new() { TemplateId = seed.Template, WeekNumber = n, Weekday = 1, TaskCode = TaskCode.podcast, MediaId = seed.Podcast });
        await db.SaveChangesAsync(); var version = template.Version;
        var store = new EffectiveDayContentStore(db); var resolver = new EffectiveDayContentResolver(store);
        var handler = new PublishTemplateCommandHandler(new ProgramRepository(db, new ConfigurationBuilder().Build()),
            NullLogger<PublishTemplateCommandHandler>.Instance, store, resolver);
        var error = await Assert.ThrowsAsync<TemplateContentGapsException>(() => handler.Handle(new(seed.Template), CancellationToken.None));
        Assert.Equal(new TemplateDayGapDto(8, 2, 1, "podcast"), Assert.Single(error.Gaps));
        await db.Entry(template).ReloadAsync(); Assert.Equal(TemplateStatus.Draft, template.Status); Assert.Equal(version, template.Version); Assert.Null(template.PublishedAt);
        db.WeeklyDayTemplates.Add(new() { TemplateId = seed.Template, WeekNumber = 2, Weekday = 1, TaskCode = TaskCode.podcast, MediaId = seed.Podcast });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var published = await handler.Handle(new(seed.Template), CancellationToken.None);
        Assert.Equal(TemplateStatus.Active, published.Status); Assert.Equal(version + 1, published.Version); Assert.NotNull(published.PublishedAt);
        Assert.Equal(12, await db.WeeklyDayTemplates.CountAsync(d => d.TemplateId == seed.Template && d.WeekNumber != null));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.Handle(new(seed.Template), cancellation.Token));
    }
}
