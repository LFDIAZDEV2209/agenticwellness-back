using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoppAddresd.Application.Common;
using CoppAddresd.Application.Features.Nutrition.Cases;
using CoppAddresd.Application.Features.Nutrition.Commands;
using CoppAddresd.Application.Features.Nutrition.Queries;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Persistence;
using CoppAddresd.Infrastructure.Repositories;
using CoppAddresd.Infrastructure.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Polly;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    /// <summary>Dependencia HTTP real en loopback; no sustituye HttpClient y nunca llama a un proveedor externo.</summary>
    private sealed class CycleAiMock : IAsyncDisposable
    {
        private readonly WebApplication app;
        public string Address { get; private set; } = "";
        public List<JsonElement> Requests { get; } = [];
        public Func<HttpContext, Task<IResult>> Reply { get; set; } = _ => Task.FromResult(Ready());
        private CycleAiMock(WebApplication app) { this.app = app; }
        public static IResult Ready() => Results.Json(new NutritionCycleSummaryResponse("ok", "Resumen de registros.",
            ["Patrón descriptivo."], ["Faltan datos."], new("Borrador ES", "Draft EN"), "mock-cycle-model", new(2026, 12, 3, 18, 0, 0, TimeSpan.Zero)));
        public static IResult Insufficient() => Results.Json(new NutritionCycleSummaryResponse("insufficient", "", [], [], new("", ""), "", new(2026, 12, 3, 18, 0, 0, TimeSpan.Zero)));
        public static async Task<CycleAiMock> Start()
        {
            var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            var app = builder.Build(); var mock = new CycleAiMock(app);
            app.MapPost(NutritionCycleSummaryClient.Endpoint, async (HttpContext context) =>
            {
                Assert.Equal("synthetic-internal-key", context.Request.Headers["X-Internal-Key"].ToString());
                using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                mock.Requests.Add(body.RootElement.Clone()); return await mock.Reply(context);
            });
            app.MapFallback(() => Results.StatusCode(501));
            await app.StartAsync(); mock.Address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return mock;
        }
        public NutritionCycleSummaryClient Client(HttpClient http, int seconds = 25) => new(http,
            Options.Create(new AiServiceSettings { BaseUrl = Address, InternalApiKey = "synthetic-internal-key", TimeoutSeconds = seconds }));
        public async ValueTask DisposeAsync() { await app.DisposeAsync(); }
    }

    private static IPatientScopeActor CycleAiActor(Guid user, Guid professional)
    {
        var actor = Substitute.For<IPatientScopeActor>(); actor.UserId.Returns(user);
        actor.ProfessionalIdAsync(Arg.Any<CancellationToken>()).Returns(professional);
        actor.HasPermissionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true); return actor;
    }
    private GenerateCycleAiDraftHandler CycleAiHandler(AppDbContext db, IPatientScopeActor actor, INutritionCycleSummaryClient client, TimeProvider clock)
        => new(new PatientScopeGuard(actor, new PatientScopeStore(db)), actor, new NutritionCycleReviewStore(db, EffectiveRepository(db)),
            new NutritionCycleReadStore(db), new NutritionCycleAiReadStore(db), client, clock, NullLogger<GenerateCycleAiDraftHandler>.Instance);

    [NutritionPostgresFact]
    public async Task CicloIA_HttpReal_MapeaAgregadosMuestraAnteriorMediciones_SinPII_HashYBorradorSeparados()
    {
        await using var db = Context(); var patient = await PatientAsync(db); var other = await PatientAsync(db);
        patient.FirstName = "PRIVATE_PATIENT_313"; await db.SaveChangesAsync();
        var pro = NutritionProfessional(patient.Id); pro.Employee.UserId = patient.UserId; db.Professionals.Add(pro);
        db.PatientProfessionalAssignments.Add(new() { PatientId = patient.Id, ProfessionalId = pro.Id }); await db.SaveChangesAsync();
        var prior = await CycleAdherencePlanAsync(db, patient.Id, new(2026, 10, 1), new(2026, 10, 1, 15, 0, 0, DateTimeKind.Utc));
        var current = await CycleAdherencePlanAsync(db, patient.Id, new(2026, 11, 2), new(2026, 11, 2, 15, 0, 0, DateTimeKind.Utc), meals: 4);
        var foreign = await CycleAdherencePlanAsync(db, other.Id, new(2026, 11, 2), new(2026, 11, 2, 15, 0, 0, DateTimeKind.Utc));
        for (var i = 0; i < 31; i++) await AddCycleIntakesAsync(db, patient.Id, new DateOnly(2026, 11, 2).AddDays(i), MealCode.des, MealCode.alm, MealCode.cen, MealCode.agua);
        await AddCycleIntakesAsync(db, patient.Id, new(2026, 10, 2), MealCode.des);
        await AddCycleIntakesAsync(db, patient.Id, new(2026, 11, 1), MealCode.des); // Continuidad del anterior.
        await AddCycleIntakesAsync(db, patient.Id, new(2026, 12, 3), MealCode.des); // Futuro, fuera del snapshot.
        await AddCycleIntakesAsync(db, other.Id, new(2026, 12, 2), MealCode.des);
        await db.NutritionIntakeLogs.Where(l => l.PatientId == patient.Id && l.LocalDate >= new DateOnly(2026, 11, 2) && l.MealCode != MealCode.agua)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.Calories, 10m));
        await db.NutritionIntakeLogs.Where(l => l.PatientId == patient.Id && l.MealCode == MealCode.agua).ExecuteUpdateAsync(s => s.SetProperty(l => l.WaterMl, 1500));
        await db.NutritionIntakeLogs.Where(l => l.PatientId == patient.Id && l.LocalDate < new DateOnly(2026, 11, 2)).ExecuteUpdateAsync(s => s.SetProperty(l => l.Calories, 50m));
        var ownPhoto = IntakeAnalysis(patient.UserId); ownPhoto.SummaryCalories = 100; ownPhoto.ImageKey = "foodai/PRIVATE_PHOTO.jpg";
        var foreignPhoto = IntakeAnalysis(other.UserId); foreignPhoto.SummaryCalories = 999;
        db.FoodAnalyses.AddRange(ownPhoto, foreignPhoto); await db.SaveChangesAsync();
        await db.NutritionIntakeLogs.Where(l => l.PatientId == patient.Id && l.LocalDate == new DateOnly(2026, 12, 2) && l.MealCode == MealCode.des)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.Calories, 100m).SetProperty(l => l.FoodAnalysisId, ownPhoto.AnalysisId).SetProperty(l => l.Source, "ai_photo"));
        await db.NutritionIntakeLogs.Where(l => l.PatientId == patient.Id && l.LocalDate == new DateOnly(2026, 12, 2) && l.MealCode == MealCode.alm)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.FoodAnalysisId, foreignPhoto.AnalysisId).SetProperty(l => l.Source, "ai_photo"));
        async Task Measurement(string code, string unitCode, string symbol, decimal value, DateTime observed, Guid? who = null)
        {
            var unit = await db.Set<UnitOfMeasure>().FirstOrDefaultAsync(u => u.Code == unitCode) ?? new UnitOfMeasure { Id = Guid.NewGuid(), Code = unitCode, Symbol = symbol, Name = "Unidad sintética" };
            if (db.Entry(unit).State == EntityState.Detached) db.Set<UnitOfMeasure>().Add(unit);
            var metric = await db.MeasurementMetrics.FirstOrDefaultAsync(m => m.Code == code) ?? new MeasurementMetric { Id = Guid.NewGuid(), Code = code, Name = "Métrica sintética", DefaultUnitId = unit.Id, Category = "body_comp" };
            if (db.Entry(metric).State == EntityState.Detached) db.MeasurementMetrics.Add(metric);
            db.ClinicalMeasurements.Add(new() { Id = Guid.NewGuid(), PatientId = who ?? patient.Id, MetricId = metric.Id, UnitId = unit.Id, Value = value, ObservedAt = observed, Source = "professional" });
            await db.SaveChangesAsync();
        }
        await Measurement("weight", "kg", "kg", 72, new(2026, 12, 2, 17, 0, 0, DateTimeKind.Utc));
        await Measurement("weight", "kg", "kg", 99, new(2026, 12, 2, 19, 0, 0, DateTimeKind.Utc)); // Futuro del mismo día.
        await Measurement("weight", "kg", "kg", 400, new(2026, 12, 2, 17, 30, 0, DateTimeKind.Utc), other.Id);
        await Measurement("waist", "cm", "cm", 80, new(2026, 12, 1, 15, 0, 0, DateTimeKind.Utc));
        await Measurement("bmi", "kg_m2", "kg/m²", 24, new(2026, 12, 1, 15, 0, 0, DateTimeKind.Utc));
        var actor = CycleAiActor(patient.UserId!.Value, pro.Id); var clock = new EffectiveClock(new(2026, 12, 2, 18, 0, 0, DateTimeKind.Utc));
        var scope = new PatientScopeGuard(actor, new PatientScopeStore(db)); var store = new NutritionCycleReviewStore(db, EffectiveRepository(db));
        var manual = new CycleReviewHandler(scope, actor, store, new NutritionCycleReadStore(db), Substitute.For<INutritionAttachmentUrlSigner>(), Cases(db, time: clock), clock);
        var saved = await manual.Handle(new SaveNutritionCycleReview(current.CycleId, 0, "PRIVATE_NOTES", new("Mensaje humano", "Human message"), true), default);
        await using var mock = await CycleAiMock.Start(); using var http = new HttpClient();
        mock.Reply = _ => { Assert.Null(db.Database.CurrentTransaction); return Task.FromResult(CycleAiMock.Ready()); };
        var handler = CycleAiHandler(db, actor, mock.Client(http), clock);
        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(new GenerateCycleAiDraft(foreign.CycleId), default));
        var result = await handler.Handle(new GenerateCycleAiDraft(current.CycleId, saved.Revision, "en"), default);
        Assert.Equal("ok", result.Status); Assert.Equal(saved.Revision + 1, result.Revision); Assert.Null(result.Warning);
        var json = Assert.Single(mock.Requests); var wire = json.GetRawText();
        Assert.Equal(new[] { "cycleNumber", "startDate", "endDate", "language", "aggregates", "mealCounts", "previousCycleAggregates", "measurements", "records" }, json.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(2, json.GetProperty("cycleNumber").GetInt32()); Assert.Equal("en", json.GetProperty("language").GetString());
        Assert.Equal(31, json.GetProperty("aggregates").GetProperty("daysWithRecords").GetInt32());
        Assert.Equal(1m, json.GetProperty("aggregates").GetProperty("adherence").GetDecimal());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("aggregates").GetProperty("avgFiberG").ValueKind);
        Assert.Equal(1500m, json.GetProperty("aggregates").GetProperty("avgWaterMl").GetDecimal());
        Assert.Equal(31, json.GetProperty("mealCounts").GetProperty("des").GetInt32()); Assert.Equal(100, json.GetProperty("records").GetArrayLength());
        Assert.Equal(2, json.GetProperty("previousCycleAggregates").GetProperty("daysWithRecords").GetInt32());
        Assert.Equal(50m, json.GetProperty("previousCycleAggregates").GetProperty("avgCalories").GetDecimal());
        Assert.Equal(72m, json.GetProperty("measurements").GetProperty("weight").GetDecimal());
        Assert.Equal(80m, json.GetProperty("measurements").GetProperty("waist").GetDecimal()); Assert.Equal(24m, json.GetProperty("measurements").GetProperty("bmi").GetDecimal());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("measurements").GetProperty("glucose").ValueKind);
        var records = json.GetProperty("records").EnumerateArray().ToArray();
        Assert.Contains(records, r => r.GetProperty("origin").GetString() == "ai_estimated");
        Assert.Equal("patient_confirmed", records.Single(r => r.GetProperty("date").GetString() == "2026-12-02" && r.GetProperty("mealCode").GetString() == "alm").GetProperty("origin").GetString());
        foreach (var forbidden in new[] { patient.Id.ToString(), current.CycleId.ToString(), ownPhoto.AnalysisId.ToString(), "PRIVATE_", "storageKey", "photo", "url", "patientId", "source", "name" }) Assert.DoesNotContain(forbidden, wire);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(wire))), result.AiDraft!.InputHash);
        var persisted = await db.Set<NutritionCycleReview>().AsNoTracking().SingleAsync(r => r.CycleId == current.CycleId);
        Assert.False(persisted.AiDraftConfirmed); Assert.Equal("PRIVATE_NOTES", persisted.ProfessionalNotes);
        Assert.Contains("Mensaje humano", persisted.PatientMessageJson); Assert.Contains("mock-cycle-model", persisted.AiDraftJson);
        var clinicalAdherence = await new GetCycleAdherenceQueryHandler(scope, new NutritionCycleReadStore(db), clock).Handle(new(current.CycleId), default);
        Assert.Equal(.75m, clinicalAdherence.Adherence); // La cobertura IA no cambia la fórmula D2.
        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "copp-313-contract-request.json"), wire, new UTF8Encoding(false));
    }

    [NutritionPostgresFact]
    public async Task CicloIA_FalloTimeoutYContratoInvalido_NoEscriben_InsuficientePermitePublicacionManual()
    {
        await using var db = Context(); var patient = await PatientAsync(db); var pro = NutritionProfessional(patient.Id); pro.Employee.UserId = patient.UserId;
        db.Professionals.Add(pro); db.PatientProfessionalAssignments.Add(new() { PatientId = patient.Id, ProfessionalId = pro.Id }); await db.SaveChangesAsync();
        var actor = CycleAiActor(patient.UserId!.Value, pro.Id); var nutritionActor = new NutritionActor(patient.UserId.Value, pro.Id, false, true, true, true);
        var early = Cases(db, time: new EffectiveClock(new(2026, 11, 2, 18, 0, 0, DateTimeKind.Utc)));
        var first = await early.ExecuteAsync(new(NutritionOperation.Generate, patient.Id, null, null, nutritionActor, CompleteNutritionInput), default);
        first = await early.ExecuteAsync(Mutation(first, NutritionOperation.Submit, nutritionActor), default);
        first = await early.ExecuteAsync(Mutation(first, NutritionOperation.Approve, nutritionActor, confirm: true), default);
        var clock = new EffectiveClock(new(2026, 12, 3, 18, 0, 0, DateTimeKind.Utc)); var cases = Cases(db, time: clock);
        var next = await cases.ExecuteAsync(new(NutritionOperation.Generate, patient.Id, null, null, nutritionActor, CompleteNutritionInput), default);
        next = await cases.ExecuteAsync(Mutation(next, NutritionOperation.Submit, nutritionActor), default);
        var scope = new PatientScopeGuard(actor, new PatientScopeStore(db)); var store = new NutritionCycleReviewStore(db, EffectiveRepository(db));
        var manual = new CycleReviewHandler(scope, actor, store, new NutritionCycleReadStore(db), Substitute.For<INutritionAttachmentUrlSigner>(), cases, clock);
        var review = await manual.Handle(new SaveNutritionCycleReview(first.CycleId, 0, "Evaluación humana", new("Mensaje humano", "Human message"), true), default);
        await using var mock = await CycleAiMock.Start(); using var http = new HttpClient(); var handler = CycleAiHandler(db, actor, mock.Client(http, 1), clock);
        mock.Reply = _ => Task.FromResult(Results.StatusCode(503));
        var failed = await handler.Handle(new GenerateCycleAiDraft(first.CycleId, review.Revision), default);
        Assert.Equal("unavailable", failed.Status); Assert.Equal("ai_unavailable", failed.WarningCode); Assert.Contains("manual", failed.Warning);
        mock.Reply = async c => { await Task.Delay(TimeSpan.FromSeconds(10), c.RequestAborted); return CycleAiMock.Ready(); };
        Assert.Equal("ai_timeout", (await handler.Handle(new GenerateCycleAiDraft(first.CycleId, review.Revision), default)).WarningCode);
        mock.Reply = _ => Task.FromResult(Results.Content("invalid-json", "application/json"));
        Assert.Equal("unavailable", (await handler.Handle(new GenerateCycleAiDraft(first.CycleId, review.Revision), default)).Status);
        var unchanged = await db.Set<NutritionCycleReview>().AsNoTracking().SingleAsync(r => r.CycleId == first.CycleId);
        Assert.Null(unchanged.AiDraftJson); Assert.Equal(review.Revision, unchanged.Revision);
        mock.Reply = _ => Task.FromResult(CycleAiMock.Insufficient());
        var insufficient = await handler.Handle(new GenerateCycleAiDraft(first.CycleId, review.Revision), default);
        Assert.Equal("insufficient", insufficient.Status); Assert.Equal("ai_insufficient_data", insufficient.WarningCode);
        Assert.False((await db.Set<NutritionCycleReview>().AsNoTracking().SingleAsync(r => r.CycleId == first.CycleId)).AiDraftConfirmed);
        var published = await manual.Handle(new PublishNutritionCycleReview(first.CycleId, insufficient.Revision, next.Id, next.Revision), default);
        Assert.NotNull(published.PublishedAt); Assert.Equal("Evaluación humana", published.ProfessionalNotes);
        var calls = mock.Requests.Count;
        await Assert.ThrowsAsync<BusinessRuleViolationException>(() => handler.Handle(new GenerateCycleAiDraft(first.CycleId, published.Revision), default));
        Assert.Equal(calls, mock.Requests.Count);
    }

    [NutritionPostgresFact]
    public async Task CicloIA_EdicionYRevocacionDuranteHttp_NoPisanRevision_CancelacionYPermisoSinEscritura()
    {
        await using var db = Context(); var patient = await PatientAsync(db); var pro = NutritionProfessional(patient.Id);
        db.Professionals.Add(pro); db.PatientProfessionalAssignments.Add(new() { PatientId = patient.Id, ProfessionalId = pro.Id }); await db.SaveChangesAsync();
        var plan = await CycleAdherencePlanAsync(db, patient.Id, new(2026, 11, 2), new(2026, 11, 2, 15, 0, 0, DateTimeKind.Utc));
        var actor = CycleAiActor(patient.UserId!.Value, pro.Id); var clock = new EffectiveClock(new(2026, 11, 7, 18, 0, 0, DateTimeKind.Utc));
        await using var mock = await CycleAiMock.Start(); using var http = new HttpClient(); var handler = CycleAiHandler(db, actor, mock.Client(http), clock);
        mock.Reply = async _ =>
        {
            Assert.Null(db.Database.CurrentTransaction); await using var writer = Context();
            writer.Set<NutritionCycleReview>().Add(new() { CycleId = plan.CycleId, Revision = 1, ReviewerId = patient.UserId.Value, ReviewedAt = clock.GetUtcNow().UtcDateTime, ProfessionalNotes = "Edición concurrente" });
            await writer.SaveChangesAsync(); return CycleAiMock.Ready();
        };
        await Assert.ThrowsAsync<BusinessRuleViolationException>(() => handler.Handle(new GenerateCycleAiDraft(plan.CycleId), default));
        Assert.Null((await db.Set<NutritionCycleReview>().AsNoTracking().SingleAsync()).AiDraftJson);
        mock.Reply = async _ =>
        {
            await using var writer = Context(); await writer.PatientProfessionalAssignments.Where(a => a.PatientId == patient.Id && a.ProfessionalId == pro.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, "Inactive")); return CycleAiMock.Ready();
        };
        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(new GenerateCycleAiDraft(plan.CycleId, 1), default));
        await db.PatientProfessionalAssignments.Where(a => a.PatientId == patient.Id && a.ProfessionalId == pro.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, "Active"));
        var calls = mock.Requests.Count; actor.HasPermissionAsync("Nutrition.NotesView", Arg.Any<CancellationToken>()).Returns(false);
        await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(new GenerateCycleAiDraft(plan.CycleId, 1), default)); Assert.Equal(calls, mock.Requests.Count);
        actor.HasPermissionAsync("Nutrition.NotesView", Arg.Any<CancellationToken>()).Returns(true);
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mock.Reply = async c => { seen.TrySetResult(); await Task.Delay(Timeout.Infinite, c.RequestAborted); return CycleAiMock.Ready(); };
        using var cancelled = new CancellationTokenSource(); var running = handler.Handle(new GenerateCycleAiDraft(plan.CycleId, 1), cancelled.Token);
        await seen.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        var row = await db.Set<NutritionCycleReview>().AsNoTracking().SingleAsync(); Assert.Equal("Edición concurrente", row.ProfessionalNotes); Assert.Null(row.AiDraftJson); Assert.Equal(1, row.Revision);
    }
}
