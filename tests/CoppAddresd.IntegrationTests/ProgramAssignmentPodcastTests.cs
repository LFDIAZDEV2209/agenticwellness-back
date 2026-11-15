using System.Text.Json.Nodes;
using CoppAddresd.Application.Features.HealthTests.Events;
using CoppAddresd.Application.Features.ProgramProgress.Assignment;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Entities.HealthTests;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Domain.Enums.HealthTests;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Infrastructure.Repositories;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace CoppAddresd.IntegrationTests;

public sealed partial class ProgramAssignmentIntegrationTests
{
    [AssignmentPostgresFact]
    public async Task Podcasts_SinRegla_PropuestaPersistidaConservaPlantillaEspecificaGenericaYHuecos()
    {
        var data = await SeedPodcastAssignmentAsync(false);
        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IPublisher>()
            .Publish(new BatteryCompletedEvent(data.PatientId, data.BatteryId));

        await using var db = NewDb();
        var recommendation = await db.ProgramAssignmentRecommendations.AsNoTracking().SingleAsync(r => r.PatientId == data.PatientId);
        var proposal = JsonNode.Parse(recommendation.Proposal)!;
        var catalog = await new ProgramAssignmentRepository(db).ReadCatalogAsync(data.TemplateId, CancellationToken.None);
        Assert.Equal(catalog.TemplatePodcasts.Select(p => p.MediaId),
            proposal["podcastSequence"]!.AsArray().Select(p => p!["mediaId"]?.GetValue<Guid>()));
        Assert.Equal(new Guid?[] { data.BaseId, data.OtherId, data.BaseId, null }, catalog.TemplatePodcasts.Select(p => p.MediaId));
        Assert.True(proposal["useTemplatePodcastDefault"]!.GetValue<bool>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(recommendation.ProfileSnapshot)!["templatePodcastSequence"], proposal["podcastSequence"]));
        Assert.Equal(AssignmentEngine.Version, recommendation.EngineVersion);
        // El replay no recalcula ni sustituye la decisión guardada.
        await scope.ServiceProvider.GetRequiredService<IPublisher>()
            .Publish(new BatteryCompletedEvent(data.PatientId, data.BatteryId));
        Assert.Equal(recommendation.Proposal, await db.ProgramAssignmentRecommendations.Where(r => r.Id == recommendation.Id).Select(r => r.Proposal).SingleAsync());
    }

    [AssignmentPostgresFact]
    public async Task Podcasts_ConRegla_UsaPlantillaDeInscripcionPendienteYGuardaOverrideConRazon()
    {
        var data = await SeedPodcastAssignmentAsync(true);
        await using (var setup = NewDb())
        {
            setup.ProgramAssignmentRules.Add(new()
            {
                Id = Guid.NewGuid(), Code = "podcast-perfil-qa", Priority = 1000,
                Criteria = """{"field":"sex","op":"eq","value":"female"}""",
                Target = """{"podcastReason":"Perfil medido: episodio específico para esta evaluación","podcastOverrides":[{"weekNumber":2,"weekday":1,"mediaTitle":"Episodio adaptado QA"}]}""",
            });
            await setup.SaveChangesAsync();
        }
        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IPublisher>()
            .Publish(new BatteryCompletedEvent(data.PatientId, data.BatteryId));

        await using var db = NewDb();
        var enrollment = await db.ProgramEnrollments.AsNoTracking().SingleAsync(e => e.PatientId == data.PatientId);
        Assert.Equal(data.TemplateId, enrollment.TemplateId);
        var rec = await db.ProgramAssignmentRecommendations.AsNoTracking().SingleAsync(r => r.EnrollmentId == enrollment.Id);
        var proposal = JsonNode.Parse(rec.Proposal)!;
        Assert.Equal(new Guid?[] { data.BaseId, data.OtherId, data.OtherId, null },
            proposal["podcastSequence"]!.AsArray().Select(p => p!["mediaId"]?.GetValue<Guid>()));
        Assert.False(proposal["useTemplatePodcastDefault"]!.GetValue<bool>());
        var match = Assert.Single(proposal["matchedRules"]!.AsArray(), r => r!["appliesPodcasts"]!.GetValue<bool>())!;
        Assert.Equal("podcast-perfil-qa", match["ruleCode"]!.GetValue<string>());
        Assert.Contains("Perfil medido", match["reason"]!.GetValue<string>());
        var change = Assert.Single(match["podcastChanges"]!.AsArray())!;
        Assert.Equal(data.BaseId, change["previousMediaId"]!.GetValue<Guid>());
        Assert.Equal(data.OtherId, change["mediaId"]!.GetValue<Guid>());
        Assert.Equal(new Guid?[] { data.BaseId, data.OtherId, data.BaseId, null },
            (await new ProgramAssignmentRepository(db).ReadCatalogAsync(data.TemplateId, CancellationToken.None)).TemplatePodcasts.Select(p => p.MediaId));
        Assert.Single(await db.ProgramEnrollments.Where(e => e.PatientId == data.PatientId).ToListAsync());
    }

    [AssignmentPostgresFact]
    public async Task Podcasts_CatalogoExcluyeDestinosNoPublicadosDemoSinArchivoYOtrosTipos()
    {
        var data = await SeedPodcastAssignmentAsync(false);
        await using var db = NewDb();
        var invalid = new[]
        {
            Podcast("Borrador", MediaStatus.Draft), Podcast("Retirado", MediaStatus.Archived),
            Podcast("Demo"), Podcast("Sin archivo"), Podcast("Video"),
        };
        invalid[2].StorageKey = CoppAddresd.Application.Features.Media.MediaPublishRules.DemoStorageKey;
        invalid[3].StorageKey = "  "; invalid[4].MediaType = MediaType.Video;
        db.MediaItems.AddRange(invalid);
        await db.SaveChangesAsync();
        var catalog = await new ProgramAssignmentRepository(db).ReadCatalogAsync(data.TemplateId, CancellationToken.None);
        Assert.Equal(2, catalog.Podcasts.Count);
        Assert.DoesNotContain(catalog.Podcasts, p => invalid.Any(m => m.Id == p.Id));
    }

    private sealed record PodcastScenario(Guid PatientId, Guid BatteryId, Guid TemplateId, Guid BaseId, Guid OtherId);

    private static MediaItem Podcast(string title, MediaStatus status = MediaStatus.Published) => new()
    {
        Id = Guid.NewGuid(), Title = title, Author = "Equipo QA", MediaType = MediaType.Podcast,
        Status = status, StorageKey = "media/podcasts/qa-real.mp3", CreatedAt = DateTimeOffset.UtcNow,
    };

    private async Task<PodcastScenario> SeedPodcastAssignmentAsync(bool pendingOnOtherTemplate)
    {
        await using var db = NewDb();
        var template = await db.ProgramTemplates.SingleAsync();
        if (pendingOnOtherTemplate)
        {
            template = new() { Id = Guid.NewGuid(), Code = "plantilla-pendiente-qa", Name = "Plantilla pendiente", TotalWeeks = 12, Status = TemplateStatus.Active };
            db.ProgramTemplates.Add(template);
        }
        var patient = new PatientProfile { Id = Guid.NewGuid(), FirstName = "Paciente", LastName = "Podcasts QA", Gender = "Femenino", DocumentNumber = Guid.NewGuid().ToString("N") };
        db.PatientProfiles.Add(patient);
        var first = Podcast("Episodio base QA"); var other = Podcast("Episodio adaptado QA");
        db.MediaItems.AddRange(first, other);
        foreach (var slot in new AssignmentPodcastSlot[] { new(null, 1, first.Id), new(1, 1, other.Id), new(2, 1, first.Id), new(12, 6, null) })
            db.WeeklyDayTemplates.Add(new() { Id = Guid.NewGuid(), TemplateId = template.Id, TaskCode = TaskCode.podcast, WeekNumber = slot.WeekNumber, Weekday = slot.Weekday, MediaId = slot.MediaId });
        var battery = await db.HealthTestBatteries.SingleAsync(b => b.Code == "bateria-inicial");
        battery.AutoAssignOnPatientCreate = true;
        var parent = new HealthTestBatteryAssignment { Id = Guid.NewGuid(), PatientId = patient.Id, BatteryId = battery.Id, Status = HealthTestAssignmentStatus.completed };
        db.HealthTestBatteryAssignments.Add(parent);
        var instruments = await db.HealthTestBatteryItems.Where(i => i.BatteryId == battery.Id && i.IsRequired).Select(i => i.InstrumentId).ToListAsync();
        var versions = await db.HealthTestVersions.Where(v => instruments.Contains(v.InstrumentId) && v.IsCurrent).ToListAsync();
        foreach (var version in versions)
        {
            var assignment = new HealthTestAssignment { Id = Guid.NewGuid(), PatientId = patient.Id, BatteryAssignmentId = parent.Id, VersionId = version.Id, Status = HealthTestAssignmentStatus.completed };
            db.HealthTestAssignments.Add(assignment);
            db.HealthTestEvaluations.Add(new() { Id = Guid.NewGuid(), AssignmentId = assignment.Id, PatientId = patient.Id, VersionId = version.Id, Status = HealthTestEvaluationStatus.completed, CompletedAt = _lifecycleClock.Current.UtcDateTime });
        }
        await db.SaveChangesAsync();
        if (pendingOnOtherTemplate)
        {
            var repository = new ProgramAssignmentRepository(db);
            await repository.ExecuteForPatientAsync(patient.Id, async () =>
                await new ProgramRepository(db, new ConfigurationBuilder().Build()).CreatePendingAssignmentAsync(patient.Id, template.Code, CancellationToken.None), CancellationToken.None);
        }
        return new(patient.Id, parent.Id, template.Id, first.Id, other.Id);
    }
}
