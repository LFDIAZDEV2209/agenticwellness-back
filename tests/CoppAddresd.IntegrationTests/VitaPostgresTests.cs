using CoppAddresd.Api.Security;
using CoppAddresd.Application.Features.Agents.Vita;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Persistence;
using CoppAddresd.Infrastructure.Repositories;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MediatR;
using NSubstitute;

namespace CoppAddresd.IntegrationTests;

/// <summary>PostgreSQL real en BD única descartable del fixture; sin datos ni migraciones compartidos.</summary>
public sealed partial class NutritionPlanWorkflowTests
{
    private AppDbContext VitaDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(connection, o => o.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(100), null)).Options);

    private async Task<(EffectiveSeed Seed, Guid User, DateOnly Today)> SeedVitaAsync(AppDbContext db)
    {
        var seed = await EffectiveSeedAsync(db);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById("America/Bogota")).DateTime);
        var monday = today.AddDays(-((int)today.DayOfWeek + 6) % 7);
        var delta = monday.DayNumber - seed.Start.DayNumber;
        var enrollment = await db.ProgramEnrollments.SingleAsync(e => e.Id == seed.Enrollment);
        enrollment.StartLocalDate = monday;
        foreach (var week in await db.ProgramWeeks.Where(w => w.EnrollmentId == seed.Enrollment).ToListAsync())
        { week.WeekStartDateLocal = week.WeekStartDateLocal.AddDays(delta); week.WeekEndDateLocal = week.WeekEndDateLocal.AddDays(delta); }
        await db.SaveChangesAsync();
        var user = (await db.PatientProfiles.SingleAsync(p => p.Id == seed.Patient)).UserId!.Value;
        return (seed with { Start = monday }, user, today);
    }

    private (GetVitaContextQueryHandler Query, ProgramRepository Repo, VitaPatientAccess Access, ISender Sender) Vita(AppDbContext db)
    {
        var store = new EffectiveDayContentStore(db); var effective = new EffectiveDayContentResolver(store);
        var wellness = new WellnessRepository(db); var prescriptions = new NutritionPrescriptionRepository(db);
        var bridge = new ProgramContentResolver(wellness, NullLogger<ProgramContentResolver>.Instance, prescriptions, store, effective);
        var access = new VitaPatientAccess(new PatientRepository(db), Substitute.For<ILegalConsentService>(), TimeProvider.System);
        var query = new GetVitaContextQueryHandler(access, EffectiveRepository(db), wellness, prescriptions, bridge,
            new HealthTestRepository(db), Workflow(db), new PatientMeasurementRepository(db), TimeProvider.System, NullLogger<GetVitaContextQueryHandler>.Instance);
        var sender = Substitute.For<ISender>();
        sender.Send(Arg.Any<GetVitaContextQuery>(), Arg.Any<CancellationToken>()).Returns(c =>
            query.Handle(c.Arg<GetVitaContextQuery>(), c.Arg<CancellationToken>()));
        return (query, EffectiveRepository(db), access, sender);
    }

    [NutritionPostgresFact]
    public async Task Vita_LecturasRealesCoincidenConSnapshotSinMutarSemanasTestsNiXP()
    {
        await using var db = VitaDb(); var (seed, user, today) = await SeedVitaAsync(db);
        var components = Vita(db);
        var beforeWeeks = await db.ProgramWeeks.AsNoTracking().OrderBy(w => w.Id).Select(w => new { w.Id, w.Status, w.TasksSnapshot }).ToArrayAsync();
        var beforeTests = await db.HealthTestAssignments.CountAsync();
        var snapshot = await components.Repo.GetSnapshotAsync(seed.Enrollment, today, default);
        foreach (var scope in GetVitaContextQueryValidator.Scopes)
        {
            var result = await components.Query.Handle(new(user, scope), default);
            Assert.Contains(result.Status, new[] { "ok", "empty" });
            if (scope == "day") Assert.Equal(snapshot!.TodayTasks.Count, result.Data!.Value.GetProperty("tasks").GetArrayLength());
            if (scope == "podcast") Assert.Equal(snapshot!.TodayTasks.Single(t => t.TaskCode == TaskCode.podcast).Content!.MediaId,
                result.Data!.Value.GetProperty("mediaId").GetGuid());
            if (scope == "exercise") Assert.Equal(seed.Routine, result.Data!.Value.GetProperty("routineId").GetGuid());
        }
        var afterWeeks = await db.ProgramWeeks.AsNoTracking().OrderBy(w => w.Id).Select(w => new { w.Id, w.Status, w.TasksSnapshot }).ToArrayAsync();
        Assert.Equal(beforeWeeks.Select(w => (w.Id, w.Status, w.TasksSnapshot.GetRawText())),
            afterWeeks.Select(w => (w.Id, w.Status, w.TasksSnapshot.GetRawText())));
        Assert.Equal(beforeTests, await db.HealthTestAssignments.CountAsync());
        Assert.Empty(await db.TaskCompletions.ToListAsync()); Assert.Empty(await db.XpLedgerEntries.ToListAsync());
        Assert.Empty(await db.DailyCheckIns.ToListAsync());
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [NutritionPostgresFact]
    public async Task Vita_DescansoRealYIdentidadAjenaSinContenidoInventado()
    {
        await using var db = VitaDb(); var (seed, user, today) = await SeedVitaAsync(db);
        var weekday = (short)(today.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)today.DayOfWeek);
        // Una semana activa conserva el snapshot individual; descanso explícito en su fuente efectiva.
        var week = await db.ProgramWeeks.SingleAsync(w => w.EnrollmentId == seed.Enrollment && w.WeekNumber == 1);
        var rows = CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent.WeekSnapshotJson.Parse(week.TasksSnapshot);
        week.TasksSnapshot = CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent.WeekSnapshotJson.Serialize(
            rows.Select(r => r.TaskCode == "ejercicio" && r.Weekday == weekday ? r with { Rest = true, RoutineId = null, Origin = "individual" } : r).ToArray());
        await db.SaveChangesAsync();
        var result = await Vita(db).Query.Handle(new(user, "exercise"), default);
        Assert.Equal("ok", result.Status); Assert.True(result.Data!.Value.GetProperty("rest").GetBoolean()); Assert.Empty(result.Actions);
        Assert.Equal("forbidden", (await Vita(db).Query.Handle(new(Guid.NewGuid(), "exercise"), default)).Status);
    }

    [NutritionPostgresFact]
    public async Task Vita_CatalogoComunSinInstancias_PublicacionRevocacionYVersionPropiaSinConfig()
    {
        await using var db = VitaDb(); var (_, user, _) = await SeedVitaAsync(db);
        var other = await PatientAsync(db);
        var type = new AgentType { Id = Guid.NewGuid(), Name = "Nuevo especialista", Slug = "repetido", Status = AgentStatus.Activo,
            Metadata = """{"routing":{"topics":["movimiento"],"channels":["text","voice"]}}""" };
        var specialist = new AgentType { Id = Guid.NewGuid(), Name = "Otro especialista", Status = AgentStatus.Activo };
        var draft = new AgentType { Id = Guid.NewGuid(), Name = "Borrador", Status = AgentStatus.Borrador };
        var inactive = new AgentType { Id = Guid.NewGuid(), Name = "Inactivo", Status = AgentStatus.Inactivo };
        var unpublished = new AgentType { Id = Guid.NewGuid(), Name = "Version no activa", Status = AgentStatus.Activo };
        var wrongType = new AgentType { Id = Guid.NewGuid(), Name = "Version ajena", Status = AgentStatus.Activo };
        var noVersion = new AgentType { Id = Guid.NewGuid(), Name = "Sin version", Status = AgentStatus.Activo };
        db.AgentTypes.AddRange(type, specialist, draft, inactive, unpublished, wrongType, noVersion);
        await db.SaveChangesAsync();
        var version = new AgentTypeVersion { Id = Guid.NewGuid(), AgentTypeId = type.Id, IsActive = true, VersionNumber = 1, Config = "{\"system_prompt\":\"secreto\"}" };
        AgentTypeVersion Version(AgentType owner, bool active = true) => new()
            { Id = Guid.NewGuid(), AgentTypeId = owner.Id, IsActive = active, VersionNumber = 1, Config = "{\"knowledge_bases\":[\"privado\"],\"api_key\":\"synthetic-private\"}" };
        var specialistVersion = Version(specialist); var draftVersion = Version(draft);
        var inactiveVersion = Version(inactive); var unpublishedVersion = Version(unpublished, false);
        db.AgentTypeVersions.AddRange(version, specialistVersion, draftVersion, inactiveVersion, unpublishedVersion);
        await db.SaveChangesAsync();
        type.ActiveVersionId = version.Id; specialist.ActiveVersionId = specialistVersion.Id;
        draft.ActiveVersionId = draftVersion.Id; inactive.ActiveVersionId = inactiveVersion.Id;
        unpublished.ActiveVersionId = unpublishedVersion.Id; wrongType.ActiveVersionId = specialistVersion.Id;
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        Assert.Empty(await db.AgentInstances.AsNoTracking().ToListAsync());
        var reader = new VitaAgentCatalogReader(db);
        var handler = new GetVitaAgentsQueryHandler(Vita(db).Access, reader);
        var own = await handler.Handle(new(user), default);
        Assert.Equal(2, own.Count);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(own, VitaVersions.JsonOptions),
            System.Text.Json.JsonSerializer.Serialize(await handler.Handle(new(other.UserId!.Value), default), VitaVersions.JsonOptions));
        var result = Assert.Single(own, a => a.Id == type.Id);
        Assert.Equal(type.Id, result.Id); Assert.Equal(version.Id, result.VersionId);
        Assert.Equal(new[] { "text", "voice" }, result.Routing.Channels);
        Assert.DoesNotContain("secreto", System.Text.Json.JsonSerializer.Serialize(result));
        Assert.DoesNotContain("synthetic-private", System.Text.Json.JsonSerializer.Serialize(own));
        Assert.DoesNotContain("knowledge_bases", System.Text.Json.JsonSerializer.Serialize(own));
        Assert.All(await reader.ListAuthorizedAsync(user, default), t => Assert.Equal("{}", t.ActiveVersion!.Config));

        await db.AgentTypeVersions.Where(v => v.Id == version.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.IsActive, false));
        Assert.Equal(specialist.Id, Assert.Single(await handler.Handle(new(user), default)).Id);
        await db.AgentTypeVersions.Where(v => v.Id == version.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.IsActive, true));
        await db.AgentTypes.Where(t => t.Id == type.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, AgentStatus.Inactivo));
        Assert.Equal(specialist.Id, Assert.Single(await handler.Handle(new(other.UserId.Value), default)).Id);
        await db.AgentTypes.Where(t => t.Id == type.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, AgentStatus.Activo)
            .SetProperty(t => t.ActiveVersionId, specialistVersion.Id));
        Assert.Equal(specialist.Id, Assert.Single(await handler.Handle(new(user), default)).Id);
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [NutritionPostgresFact]
    public async Task Vita_DobleConfirmacionConcurrente_IdempotenteConUnaSolaXP()
    {
        await using var seedDb = VitaDb(); var (seed, user, today) = await SeedVitaAsync(seedDb);
        var tokens = new VitaProposalTokens(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))); var c = Vita(seedDb);
        var context = await c.Query.Handle(new(user, "podcast"), default);
        var prepared = await new PrepareVitaTaskCommandHandler(c.Access, c.Repo, c.Sender, tokens, TimeProvider.System)
            .Handle(new(user, new("podcast", today, context.ResourceVersion!)), default);
        Assert.Empty(await seedDb.TaskCompletions.ToListAsync());
        async Task<CoppAddresd.Application.DTOs.ProgramProgress.CompleteTaskResponseDto> Confirm()
        {
            await using var db = VitaDb(); var item = Vita(db);
            return await new ConfirmVitaTaskCommandHandler(item.Access, item.Sender, tokens, item.Repo, TimeProvider.System)
                .Handle(new(user, new(prepared.ProposalToken, true)), default);
        }
        await Task.WhenAll(Confirm(), Confirm());
        Assert.Single(await seedDb.TaskCompletions.AsNoTracking().ToListAsync());
        Assert.Single(await seedDb.XpLedgerEntries.AsNoTracking().Where(x => x.Reason == XpReason.TaskCompletion).ToListAsync());
        await Confirm(); Assert.Single(await seedDb.TaskCompletions.AsNoTracking().ToListAsync());
    }

    [NutritionPostgresFact]
    public async Task Vita_PropuestaObsoletaYCambioDeCuentaNoEscriben()
    {
        await using var db = VitaDb(); var (seed, user, today) = await SeedVitaAsync(db);
        var item = Vita(db); var tokens = new VitaProposalTokens(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var context = await item.Query.Handle(new(user, "podcast"), default);
        var prepared = await new PrepareVitaTaskCommandHandler(item.Access, item.Repo, item.Sender, tokens, TimeProvider.System)
            .Handle(new(user, new("podcast", today, context.ResourceVersion!)), default);
        var command = new ConfirmVitaTaskCommand(user, new(prepared.ProposalToken, true));
        var handler = new ConfirmVitaTaskCommandHandler(item.Access, item.Sender, tokens, item.Repo, TimeProvider.System);
        await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(command with { UserId = Guid.NewGuid() }, default));
        await db.MediaItems.Where(m => m.Id == seed.Podcast).ExecuteUpdateAsync(s => s.SetProperty(m => m.Title, "Revisión nueva"));
        await Assert.ThrowsAsync<BusinessRuleViolationException>(() => handler.Handle(command, default));
        Assert.Empty(await db.TaskCompletions.AsNoTracking().ToListAsync()); Assert.Empty(await db.XpLedgerEntries.AsNoTracking().ToListAsync());
    }

    [NutritionPostgresFact]
    public async Task Vita_CancelarDentroDeTransaccionNoDejaCompletacion()
    {
        await using var db = VitaDb(); var (seed, user, today) = await SeedVitaAsync(db);
        using var cts = new CancellationTokenSource();
        var proposal = new VitaTaskProposal(Guid.NewGuid(), user, seed.Enrollment, TaskCode.podcast, today,
            new string('a', 64), DateTimeOffset.UtcNow.AddMinutes(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EffectiveRepository(db).CompleteAsync(proposal, token =>
        { cts.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; }, cts.Token));
        Assert.Empty(await db.TaskCompletions.AsNoTracking().ToListAsync()); Assert.Empty(await db.XpLedgerEntries.AsNoTracking().ToListAsync());
    }

    [NutritionPostgresFact]
    public async Task Vita_WearableRealSinPrograma_AislaOrigenPacientePeriodoYNoSumaSnapshots()
    {
        await using var db = VitaDb(); var patient = await PatientAsync(db); var other = await PatientAsync(db);
        var metric = await db.MeasurementMetrics.SingleOrDefaultAsync(m => m.Code == "step_count");
        if (metric is null)
        {
            var unit = new UnitOfMeasure { Id = Guid.NewGuid(), Code = "vita_count", Name = "Pasos", Symbol = "pasos" };
            metric = new() { Id = Guid.NewGuid(), Code = "step_count", Name = "Pasos", Category = "vital", DefaultUnit = unit };
            db.MeasurementMetrics.Add(metric); await db.SaveChangesAsync();
        }
        var now = DateTime.UtcNow;
        ClinicalMeasurement Row(Guid owner, decimal value, DateTime at, string source = "device") => new()
        { Id = Guid.NewGuid(), PatientId = owner, MetricId = metric.Id, UnitId = metric.DefaultUnitId,
            Value = value, ObservedAt = at, Source = source, Notes = "Nota clínica privada", SourceKey = "private/source" };
        db.ClinicalMeasurements.AddRange(Row(patient.Id, 1000, now.AddMinutes(-2)), Row(patient.Id, 2500, now.AddMinutes(-1)),
            Row(other.Id, 99999, now), Row(patient.Id, 7777, now, "patient"), Row(patient.Id, 8888, now.AddDays(-8)));
        await db.SaveChangesAsync();
        var item = Vita(db); var result = await item.Query.Handle(new(patient.UserId!.Value, "progress"), default);
        Assert.Equal("ok", result.Status);
        var data = result.Data!.Value; var device = Assert.Single(data.GetProperty("deviceMetrics").EnumerateArray());
        Assert.Equal(2500, device.GetProperty("value").GetDecimal()); Assert.Equal("device", device.GetProperty("source").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, data.GetProperty("program").ValueKind);
        Assert.DoesNotContain("private", data.GetRawText()); Assert.DoesNotContain("notes", data.GetRawText());
        var repeat = await item.Query.Handle(new(patient.UserId.Value, "progress", result.ResourceVersion), default);
        Assert.Equal("ok", repeat.Status); Assert.False(db.ChangeTracker.HasChanges());
    }
}
