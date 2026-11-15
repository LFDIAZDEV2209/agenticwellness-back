using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Data.Common;
using CoppAddresd.Application.Common;
using CoppAddresd.Application.DTOs.Ai;
using CoppAddresd.Application.Features.ProgramProgress;
using CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent;
using CoppAddresd.Application.Features.ProgramProgress.PatientSummary;
using CoppAddresd.Application.Features.ProgramProgress.WeeklySuggestions;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Persistence;
using CoppAddresd.Infrastructure.Repositories;
using CoppAddresd.Infrastructure.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    private static readonly TimeProvider WeeklyClock = new EffectiveClock(new(2026, 11, 15, 23, 0, 0, DateTimeKind.Utc));
    private sealed class WeeklyMock(WebApplication app) : IAsyncDisposable
    {
        public List<string> Requests { get; } = [];
        public string Address = "";
        public Func<HttpContext, WeeklySuggestionsInput, Task<IResult>> Reply = (_, input) => Task.FromResult(Ready(input));
        public static IResult Ready(WeeklySuggestionsInput input) => Results.Json(new WeeklySuggestionsResponse("ok", "weekly_plan_suggestions", true,
            input.Slots.Where(s => s.Options.Count > 0).Take(2).Select(s => new WeeklySuggestion(s.Ref, s.Options[0].Ref,
                "Valorar la alternativa con el profesional.", [input.Facts[0].Ref])).ToArray(), "weekly-synthetic-model", new(2026, 11, 15, 23, 0, 0, TimeSpan.Zero)));
        public static async Task<WeeklyMock> Start()
        {
            var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            var app = builder.Build(); var mock = new WeeklyMock(app);
            app.MapPost(WeeklySuggestionsClient.Endpoint, async (HttpContext context) =>
            {
                Assert.Equal("synthetic-weekly-key", context.Request.Headers["X-Internal-Key"].ToString());
                using var reader = new StreamReader(context.Request.Body); var wire = await reader.ReadToEndAsync(context.RequestAborted); mock.Requests.Add(wire);
                return await mock.Reply(context, JsonSerializer.Deserialize<WeeklySuggestionsInput>(wire, PatientSummaryJson.Options)!);
            });
            await app.StartAsync(); mock.Address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single(); return mock;
        }
        public WeeklySuggestionsClient Client(HttpClient http, int seconds = 25) => new(http,
            Options.Create(new AiServiceSettings { BaseUrl = Address, InternalApiKey = "synthetic-weekly-key", TimeoutSeconds = seconds }));
        public async ValueTask DisposeAsync() => await app.DisposeAsync();
    }
    private sealed record WeeklySeed(EffectiveSeed Base, Guid Professional, Guid AlternativeRoutine, Guid AlternativePodcast);
    private async Task<WeeklySeed> WeeklySeedAsync(AppDbContext db)
    {
        var seed = await EffectiveSeedAsync(db); var pro = NutritionProfessional(seed.Patient); db.Professionals.Add(pro);
        db.PatientProfessionalAssignments.Add(new() { PatientId = seed.Patient, ProfessionalId = pro.Id });
        var alternative = new ExerciseRoutine { Id = Guid.NewGuid(), Name = "Alternativa suave", Status = NutritionPlanStatus.Active, Difficulty = (RoutineDifficulty)1 };
        var podcast = new MediaItem { Id = Guid.NewGuid(), Title = "Podcast alternativo", Author = "Equipo", Status = MediaStatus.Published,
            MediaType = MediaType.Podcast, Category = MediaCategory.Biologia, StorageKey = "media/podcasts/alternative.mp3" };
        db.ExerciseRoutines.Add(alternative); db.MediaItems.Add(podcast); await db.SaveChangesAsync();
        await db.WeeklyDayTemplates.Where(t => t.TemplateId == seed.Template && t.Weekday == 2 && t.TaskCode == TaskCode.ejercicio)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RoutineId, alternative.Id));
        return new(seed, pro.Id, alternative.Id, podcast.Id);
    }
    private WeeklySuggestionsStore WeeklyStore(AppDbContext db, IPatientScopeActor actor, ILegalConsentService consent, IConfiguration? config = null)
    {
        config ??= new ConfigurationBuilder().Build(); var guard = new PatientScopeGuard(actor, new PatientScopeStore(db));
        var data = new EffectiveDayContentStore(db); var resolver = new EffectiveDayContentResolver(data);
        return new(db, data, resolver, new PatientSummaryStore(db), clinical, safety, new ProgramRepository(db, config, clock: WeeklyClock),
            guard, consent, config, WeeklyClock);
    }
    private static GenerateWeeklySuggestionsHandler WeeklyGenerate(AppDbContext db, IPatientScopeActor actor, ILegalConsentService consent,
        WeeklySuggestionsStore store, IWeeklySuggestionsClient client) => new(new PatientScopeGuard(actor, new PatientScopeStore(db)), consent, store,
            client, NullLogger<GenerateWeeklySuggestionsHandler>.Instance);
    private static ApplyWeeklySuggestionHandler WeeklyApply(AppDbContext db, IPatientScopeActor actor, ILegalConsentService consent, IWeeklySuggestionsStore store)
        => new(new PatientScopeGuard(actor, new PatientScopeStore(db)), actor, consent, store);
    private static Task<int> WeeklyAuditCount(AppDbContext db) => db.ActivityLogs.CountAsync(a => a.TableName == "program_weeks"
        && a.Metadata != null && a.Metadata.Value.GetProperty("event").GetString() == "ProgramWeekTasksChanged");

    private sealed class WeeklyCancelBriefUpdate(CancellationTokenSource source) : DbCommandInterceptor
    {
        public bool Reached { get; private set; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE app.patient_ai_briefs", StringComparison.Ordinal))
            { Reached = true; source.Cancel(); cancellationToken.ThrowIfCancellationRequested(); }
            return ValueTask.FromResult(result);
        }
    }

    [NutritionPostgresFact]
    public async Task WeeklyAI_CancelarDespuesDeSnapshotYBitacora_RevierteTodaLaAplicacion()
    {
        await using var db = Context(); var seed = await WeeklySeedAsync(db); var actor = SummaryActor(seed.Professional);
        var consent = Substitute.For<ILegalConsentService>(); var store = WeeklyStore(db, actor, consent);
        await using var mock = await WeeklyMock.Start(); using var http = new HttpClient();
        var draft = (await WeeklyGenerate(db, actor, consent, store, mock.Client(http)).Handle(new(seed.Base.Enrollment, 3), default)).Draft!;
        var before = (await db.ProgramWeeks.AsNoTracking().SingleAsync(w => w.EnrollmentId == seed.Base.Enrollment && w.WeekNumber == 3)).TasksSnapshot.GetRawText();
        using var cancellation = new CancellationTokenSource(); var interceptor = new WeeklyCancelBriefUpdate(cancellation);
        await using (var cancelling = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).AddInterceptors(interceptor).Options))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WeeklyApply(cancelling, actor, consent, WeeklyStore(cancelling, actor, consent))
                .Handle(new(seed.Base.Enrollment, 3, draft.Id, 0, true), cancellation.Token));
        }
        Assert.True(interceptor.Reached); Assert.Equal(0, await WeeklyAuditCount(db));
        Assert.Equal(before, (await db.ProgramWeeks.AsNoTracking().SingleAsync(w => w.EnrollmentId == seed.Base.Enrollment && w.WeekNumber == 3)).TasksSnapshot.GetRawText());
        Assert.All(WeeklySuggestionsAccess.Read(await db.PatientAiBriefs.AsNoTracking().SingleAsync(), seed.Base.Enrollment, 3).Suggestions, s => Assert.Null(s.AppliedAt));
    }

    [NutritionPostgresFact]
    public async Task WeeklyAI_GenerarListarNoAplica_HashHTTP_ConfirmacionIndividualBitacoraYAplicacionParcial()
    {
        await using var db = Context(); var seed = await WeeklySeedAsync(db); var actor = SummaryActor(seed.Professional); var consent = Substitute.For<ILegalConsentService>();
        var store = WeeklyStore(db, actor, consent); await using var mock = await WeeklyMock.Start(); using var http = new HttpClient();
        var snapshots = await db.ProgramWeeks.AsNoTracking().OrderBy(w => w.Id).Select(w => w.TasksSnapshot).ToArrayAsync();
        var generate = WeeklyGenerate(db, actor, consent, store, mock.Client(http)); var result = await generate.Handle(new(seed.Base.Enrollment, 3), default);
        Assert.Equal("ok", result.Status); Assert.Equal(2, result.Draft!.Suggestions.Count);
        Assert.Equal(snapshots.Select(x => x.GetRawText()), (await db.ProgramWeeks.AsNoTracking().OrderBy(w => w.Id).Select(w => w.TasksSnapshot).ToArrayAsync()).Select(x => x.GetRawText()));
        Assert.Equal(0, await WeeklyAuditCount(db));
        var read = new GetWeeklySuggestionsHandler(new PatientScopeGuard(actor, new PatientScopeStore(db)), consent, store);
        Assert.False((await read.Handle(new(seed.Base.Enrollment, 3), default)).Draft!.Stale); Assert.Single(mock.Requests);
        var wire = Assert.Single(mock.Requests); using var payload = JsonDocument.Parse(wire);
        Assert.Equal(new[] { "startDate", "endDate", "language", "facts", "slots" }, payload.RootElement.EnumerateObject().Select(x => x.Name));
        foreach (var forbidden in new[] { seed.Base.Patient.ToString(), seed.Base.Enrollment.ToString(), seed.AlternativeRoutine.ToString(), "resourceId", "patientId", "storageKey" }) Assert.DoesNotContain(forbidden, wire);
        var row = await db.PatientAiBriefs.AsNoTracking().SingleAsync(); Assert.Equal("draft", row.State);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(wire))), row.InputHash);
        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "copp-45b-contract-request.json"), wire, new UTF8Encoding(false));
        var apply = WeeklyApply(db, actor, consent, store);
        await Assert.ThrowsAsync<UnprocessableEntityException>(() => apply.Handle(new(seed.Base.Enrollment, 3, row.Id, 0, false), default));
        Assert.Equal(0, await WeeklyAuditCount(db));
        Assert.Equal("applied", (await apply.Handle(new(seed.Base.Enrollment, 3, row.Id, 0, true), default)).Status);
        var partial = (await read.Handle(new(seed.Base.Enrollment, 3), default)).Draft!;
        Assert.False(partial.Stale); Assert.Equal("draft", partial.State); Assert.Equal("applied", partial.Suggestions[0].State); Assert.Equal("draft", partial.Suggestions[1].State);
        Assert.Equal("applied", (await apply.Handle(new(seed.Base.Enrollment, 3, row.Id, 1, true), default)).Status);
        var week = await db.ProgramWeeks.AsNoTracking().SingleAsync(w => w.EnrollmentId == seed.Base.Enrollment && w.WeekNumber == 3);
        var tasks = WeekSnapshotJson.Parse(week.TasksSnapshot);
        Assert.Equal(seed.AlternativeRoutine, tasks.Single(t => t.Weekday == 1 && t.TaskCode == "ejercicio").RoutineId);
        Assert.Equal(seed.AlternativePodcast, tasks.Single(t => t.Weekday == 1 && t.TaskCode == "podcast").MediaId);
        Assert.All(tasks.Where(t => t.Weekday == 1 && t.TaskCode is "ejercicio" or "podcast"), t =>
        { Assert.Equal("individual", t.Origin); Assert.Equal(actor.UserId, t.EditedBy); Assert.NotNull(t.EditedAt); Assert.Equal(10, t.Points); });
        Assert.All(tasks.Where(t => t.Weekday != 1 || t.TaskCode == "nut"), t => Assert.Equal("program", t.Origin));
        Assert.Equal(2, await WeeklyAuditCount(db));
        var audit = await db.ActivityLogs.AsNoTracking().Where(a => a.TableName == "program_weeks").ToListAsync();
        Assert.Contains(audit, a => a.UserId == actor.UserId && a.OldData != null && a.NewData != null
            && a.Metadata!.Value.GetProperty("ai_application").GetProperty("brief_id").GetGuid() == row.Id);
        Assert.Equal("already_applied", (await apply.Handle(new(seed.Base.Enrollment, 3, row.Id, 0, true), default)).Status);
        Assert.Equal(2, await WeeklyAuditCount(db)); Assert.Equal("applied", (await db.PatientAiBriefs.AsNoTracking().SingleAsync()).State); Assert.Single(mock.Requests);
        Assert.Empty(await db.RoutineAssignments.ToListAsync()); Assert.Empty(await db.NutritionPlanAssignments.ToListAsync());
    }

    [NutritionPostgresFact]
    public async Task WeeklyAI_ConfirmacionesConcurrentes_UnaEscrituraYUnaBitacora()
    {
        await using var db = Context(); var seed = await WeeklySeedAsync(db); var actor = SummaryActor(seed.Professional);
        var consent = Substitute.For<ILegalConsentService>(); var store = WeeklyStore(db, actor, consent);
        await using var mock = await WeeklyMock.Start(); using var http = new HttpClient();
        var draft = (await WeeklyGenerate(db, actor, consent, store, mock.Client(http)).Handle(new(seed.Base.Enrollment, 3), default)).Draft!;
        async Task<string> Apply()
        {
            await using var separate = Context(); var isolated = WeeklyStore(separate, actor, consent);
            return (await WeeklyApply(separate, actor, consent, isolated).Handle(new(seed.Base.Enrollment, 3, draft.Id, 0, true), default)).Status;
        }
        var statuses = await Task.WhenAll(Apply(), Apply());
        Assert.Equal(new[] { "already_applied", "applied" }, statuses.Order().ToArray()); Assert.Equal(1, await WeeklyAuditCount(db));
        Assert.Equal(1, WeeklySuggestionsAccess.Read((await db.PatientAiBriefs.AsNoTracking().SingleAsync()), seed.Base.Enrollment, 3).Suggestions.Count(s => s.AppliedAt != null));
    }

    [NutritionPostgresFact]
    public async Task WeeklyAI_CambioManualYRecursoRetirado_RechazanBorradorSinEscribir()
    {
        await using var db = Context(); var seed = await WeeklySeedAsync(db); var actor = SummaryActor(seed.Professional);
        var consent = Substitute.For<ILegalConsentService>(); var store = WeeklyStore(db, actor, consent);
        await using var mock = await WeeklyMock.Start(); using var http = new HttpClient();
        var generate = WeeklyGenerate(db, actor, consent, store, mock.Client(http)); var apply = WeeklyApply(db, actor, consent, store);
        var draft = (await generate.Handle(new(seed.Base.Enrollment, 3), default)).Draft!;
        await db.ExerciseRoutines.Where(r => r.Id == seed.AlternativeRoutine).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, NutritionPlanStatus.Archived));
        var invalid = await Assert.ThrowsAsync<BusinessRuleViolationException>(() => apply.Handle(new(seed.Base.Enrollment, 3, draft.Id, 0, true), default));
        Assert.Contains("AI_OPTION_UNAVAILABLE", invalid.Message); Assert.Equal(0, await WeeklyAuditCount(db));
        await db.ExerciseRoutines.Where(r => r.Id == seed.AlternativeRoutine).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, NutritionPlanStatus.Active));
        await new ProgramRepository(db, new ConfigurationBuilder().Build(), clock: WeeklyClock).ReplaceEnrollmentWeekTasksAsync(seed.Base.Enrollment, 3,
            [new() { Weekday = 5, TaskCode = TaskCode.podcast, MediaId = seed.AlternativePodcast, Points = 10 }], actor.UserId, default);
        var read = new GetWeeklySuggestionsHandler(new PatientScopeGuard(actor, new PatientScopeStore(db)), consent, store);
        Assert.True((await read.Handle(new(seed.Base.Enrollment, 3), default)).Draft!.Stale);
        var stale = await Assert.ThrowsAsync<BusinessRuleViolationException>(() => apply.Handle(new(seed.Base.Enrollment, 3, draft.Id, 0, true), default));
        Assert.Contains("AI_PLAN_CHANGED", stale.Message); Assert.Equal(1, await WeeklyAuditCount(db));
        Assert.All(WeeklySuggestionsAccess.Read(await db.PatientAiBriefs.AsNoTracking().SingleAsync(), seed.Base.Enrollment, 3).Suggestions, s => Assert.Null(s.AppliedAt));
    }

    [NutritionPostgresFact]
    public async Task WeeklyAI_ErrorAlMarcarBorrador_RevierteSnapshotYBitacora_DespuesPermiteReintento()
    {
        await using var db = Context(); var seed = await WeeklySeedAsync(db); var actor = SummaryActor(seed.Professional);
        var consent = Substitute.For<ILegalConsentService>(); var store = WeeklyStore(db, actor, consent);
        await using var mock = await WeeklyMock.Start(); using var http = new HttpClient();
        var draft = (await WeeklyGenerate(db, actor, consent, store, mock.Client(http)).Handle(new(seed.Base.Enrollment, 3), default)).Draft!;
        var before = (await db.ProgramWeeks.AsNoTracking().SingleAsync(w => w.EnrollmentId == seed.Base.Enrollment && w.WeekNumber == 3)).TasksSnapshot.GetRawText();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION app.reject_weekly_brief_update() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'synthetic atomicity failure'; END $$;
            CREATE TRIGGER reject_weekly_brief_update BEFORE UPDATE ON app.patient_ai_briefs FOR EACH ROW EXECUTE FUNCTION app.reject_weekly_brief_update();
            """);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => WeeklyApply(db, actor, consent, store).Handle(new(seed.Base.Enrollment, 3, draft.Id, 0, true), default));
        Assert.Equal(before, (await db.ProgramWeeks.AsNoTracking().SingleAsync(w => w.EnrollmentId == seed.Base.Enrollment && w.WeekNumber == 3)).TasksSnapshot.GetRawText());
        Assert.Equal(0, await WeeklyAuditCount(db));
        Assert.All(WeeklySuggestionsAccess.Read(await db.PatientAiBriefs.AsNoTracking().SingleAsync(), seed.Base.Enrollment, 3).Suggestions, s => Assert.Null(s.AppliedAt));
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_weekly_brief_update ON app.patient_ai_briefs; DROP FUNCTION app.reject_weekly_brief_update();");
        Assert.Equal("applied", (await WeeklyApply(db, actor, consent, store).Handle(new(seed.Base.Enrollment, 3, draft.Id, 0, true), default)).Status);
        Assert.Equal(1, await WeeklyAuditCount(db));
    }

    [NutritionPostgresFact]
    public async Task WeeklyAI_FallosIAInsuficienciaCancelacionYConsentimiento_NoPersistenNiAplican()
    {
        await using var db = Context(); var seed = await WeeklySeedAsync(db); var actor = SummaryActor(seed.Professional);
        var consent = Substitute.For<ILegalConsentService>(); var store = WeeklyStore(db, actor, consent);
        await using var mock = await WeeklyMock.Start(); using var http = new HttpClient();
        var generate = WeeklyGenerate(db, actor, consent, store, mock.Client(http, 1));
        foreach (var reply in new Func<HttpContext, WeeklySuggestionsInput, Task<IResult>>[] {
            (_, _) => Task.FromResult(Results.StatusCode(503)),
            (_, _) => Task.FromResult(Results.Content("invalid", "application/json")),
            (_, _) => Task.FromResult(Results.Json(new WeeklySuggestionsResponse("ok", "weekly_plan_suggestions", true, [new("invented", "p1", "Motivo", ["program.current"])], null, WeeklyClock.GetUtcNow()))),
            async (context, _) => { await Task.Delay(1500, context.RequestAborted); return Results.StatusCode(503); }
        })
        {
            mock.Reply = reply; Assert.Equal("unavailable", (await generate.Handle(new(seed.Base.Enrollment, 3), default)).Status);
        }
        mock.Reply = (_, _) => Task.FromResult(Results.Json(new WeeklySuggestionsResponse("insufficient", "weekly_plan_suggestions", true, [], null, WeeklyClock.GetUtcNow())));
        Assert.Equal("insufficient", (await generate.Handle(new(seed.Base.Enrollment, 3), default)).Status);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generate.Handle(new(seed.Base.Enrollment, 3), cancelled.Token));
        var calls = mock.Requests.Count;
        consent.EnsurePatientAsync(seed.Base.Patient, "patient-ai", Arg.Any<CancellationToken>()).Returns(Task.FromException(new AiConsentException("AI_CONSENT_REQUIRED", "patient-ai", 422)));
        Assert.Equal("unavailable", (await generate.Handle(new(seed.Base.Enrollment, 3), default)).Status);
        Assert.Equal(calls, mock.Requests.Count); Assert.Empty(await db.PatientAiBriefs.ToListAsync()); Assert.Equal(0, await WeeklyAuditCount(db));
    }

    [NutritionPostgresFact]
    public async Task WeeklyAI_RevalidaAlcanceDespuesDeIA_YNoAplicaSemanaAjenaOIniciada()
    {
        await using var db = Context(); var seed = await WeeklySeedAsync(db); var actor = SummaryActor(seed.Professional);
        var consent = Substitute.For<ILegalConsentService>(); var store = WeeklyStore(db, actor, consent);
        await using var mock = await WeeklyMock.Start(); using var http = new HttpClient();
        var generate = WeeklyGenerate(db, actor, consent, store, mock.Client(http));
        mock.Reply = async (_, input) => {
            await db.PatientProfessionalAssignments.Where(a => a.PatientId == seed.Base.Patient).ExecuteDeleteAsync(); return WeeklyMock.Ready(input);
        };
        await Assert.ThrowsAsync<NotFoundException>(() => generate.Handle(new(seed.Base.Enrollment, 3), default));
        Assert.Empty(await db.PatientAiBriefs.ToListAsync());
        db.ChangeTracker.Clear();
        db.PatientProfessionalAssignments.Add(new() { PatientId = seed.Base.Patient, ProfessionalId = seed.Professional }); await db.SaveChangesAsync();
        mock.Reply = (_, input) => Task.FromResult(WeeklyMock.Ready(input));
        var draft = (await generate.Handle(new(seed.Base.Enrollment, 3), default)).Draft!;
        await Assert.ThrowsAsync<NotFoundException>(() => WeeklyApply(db, actor, consent, store).Handle(new(seed.Base.Enrollment, 2, draft.Id, 0, true), default));
        await db.ProgramWeeks.Where(w => w.EnrollmentId == seed.Base.Enrollment && w.WeekNumber == 3).ExecuteUpdateAsync(s => s.SetProperty(w => w.ActivatedAt, WeeklyClock.GetUtcNow().UtcDateTime));
        await Assert.ThrowsAsync<BusinessRuleViolationException>(() => WeeklyApply(db, actor, consent, store).Handle(new(seed.Base.Enrollment, 3, draft.Id, 0, true), default));
        Assert.Equal(0, await WeeklyAuditCount(db));
    }

    [NutritionPostgresFact]
    public async Task WeeklyAI_SeguridadDescansoYVersionCambian_RevalidacionSinRecomendacionAutomatica()
    {
        await using var db = Context(); var seed = await WeeklySeedAsync(db); var actor = SummaryActor(seed.Professional);
        var consent = Substitute.For<ILegalConsentService>();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [ProgramRestDayPolicy.ConfigurationKey] = "true" }).Build();
        var store = WeeklyStore(db, actor, consent, config);
        var capture = await store.CaptureAsync(seed.Base.Enrollment, 3, "es", default);
        Assert.Contains(capture.Targets, t => t.Kind == "rest");
        safety.EvaluateAsync(Arg.Any<ClinicalContextDto>(), Arg.Any<CancellationToken>()).Returns(new[] { new RestrictionDto("Advertencia registrada", "warning") });
        var safe = await store.CaptureAsync(seed.Base.Enrollment, 3, "es", default);
        Assert.DoesNotContain(safe.Targets, t => t.SlotKind == "routine"); Assert.Contains(safe.Targets, t => t.Kind == "podcast");
        safety.EvaluateAsync(Arg.Any<ClinicalContextDto>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<RestrictionDto>());
        await using var mock = await WeeklyMock.Start(); using var http = new HttpClient();
        mock.Reply = (_, input) => Task.FromResult(Results.Json(new WeeklySuggestionsResponse("ok", "weekly_plan_suggestions", true,
            [new(input.Slots[0].Ref, "rest", "Valorar descanso.", [input.Facts[0].Ref])], null, WeeklyClock.GetUtcNow())));
        var draft = (await WeeklyGenerate(db, actor, consent, store, mock.Client(http)).Handle(new(seed.Base.Enrollment, 3), default)).Draft!;
        config[ProgramRestDayPolicy.ConfigurationKey] = "false";
        await Assert.ThrowsAsync<UnprocessableEntityException>(() => WeeklyApply(db, actor, consent, store).Handle(new(seed.Base.Enrollment, 3, draft.Id, 0, true), default));
        config[ProgramRestDayPolicy.ConfigurationKey] = "true";
        await WeeklyApply(db, actor, consent, store).Handle(new(seed.Base.Enrollment, 3, draft.Id, 0, true), default);
        var week = await db.ProgramWeeks.AsNoTracking().SingleAsync(w => w.EnrollmentId == seed.Base.Enrollment && w.WeekNumber == 3);
        Assert.True(WeekSnapshotJson.Parse(week.TasksSnapshot).Single(t => t.TaskCode == "ejercicio" && t.Weekday == 1).Rest);
        Assert.Equal(1, await WeeklyAuditCount(db));
        mock.Reply = async (_, input) => {
            await db.ProgramTemplates.Where(t => t.Id == seed.Base.Template).ExecuteUpdateAsync(s => s.SetProperty(t => t.Version, t => t.Version + 1));
            return WeeklyMock.Ready(input);
        };
        await Assert.ThrowsAsync<BusinessRuleViolationException>(() => WeeklyGenerate(db, actor, consent, store, mock.Client(http)).Handle(new(seed.Base.Enrollment, 3), default));
        Assert.Equal(1, await db.PatientAiBriefs.CountAsync()); Assert.Equal(1, await WeeklyAuditCount(db));
    }
}
