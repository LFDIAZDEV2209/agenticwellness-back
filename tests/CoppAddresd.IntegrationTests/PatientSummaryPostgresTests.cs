using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoppAddresd.Application.Common;
using CoppAddresd.Application.DTOs.ProgramProgress;
using CoppAddresd.Application.Features.ProgramProgress.PatientSummary;
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
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    private sealed class SummaryMock(WebApplication app) : IAsyncDisposable
    {
        public List<string> Requests { get; } = [];
        public string Address = "";
        public Func<HttpContext, Task<IResult>> Reply = _ => Task.FromResult(Ready());
        public static IResult Ready(string? model = "summary-test-model") => Results.Json(new PatientSummaryResponse("ok", "patient_summary", true,
            "Hay un programa registrado [program.current].", ["program.current"], [new("Revisar los registros.", ["program.current"])],
            model, new(2026, 11, 15, 23, 0, 0, TimeSpan.Zero)));
        public static async Task<SummaryMock> Start()
        {
            var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0"); var app = builder.Build(); var mock = new SummaryMock(app);
            app.MapPost(PatientSummaryClient.Endpoint, async (HttpContext context) =>
            {
                Assert.Equal("synthetic-summary-key", context.Request.Headers["X-Internal-Key"].ToString());
                using var reader = new StreamReader(context.Request.Body); mock.Requests.Add(await reader.ReadToEndAsync(context.RequestAborted));
                return await mock.Reply(context);
            });
            await app.StartAsync(); mock.Address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return mock;
        }
        public PatientSummaryClient Client(HttpClient http, int seconds = 25) => new(http,
            Options.Create(new AiServiceSettings { BaseUrl = Address, InternalApiKey = "synthetic-summary-key", TimeoutSeconds = seconds }));
        public async ValueTask DisposeAsync() => await app.DisposeAsync();
    }
    private static readonly TimeProvider SummaryClock = new EffectiveClock(new(2026, 11, 15, 23, 0, 0, DateTimeKind.Utc));
    private static IPatientScopeActor SummaryActor(Guid professional)
    {
        var actor = Substitute.For<IPatientScopeActor>(); actor.UserId.Returns(Guid.NewGuid());
        actor.ProfessionalIdAsync(Arg.Any<CancellationToken>()).Returns(professional);
        actor.HasPermissionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true); return actor;
    }
    private static GeneratePatientSummaryHandler SummaryHandler(AppDbContext db, IPatientScopeActor actor,
        ILegalConsentService consent, IPatientSummaryClient client) => new(new PatientScopeGuard(actor, new PatientScopeStore(db)),
            consent, new PatientSummaryStore(db), client, SummaryClock);

    [NutritionPostgresFact]
    public async Task PatientSummary_HttpReal_HechosPropiosDesidentificados_HashMetadataHistorialYLecturaPorIdioma()
    {
        var seed = await CompletionSeedAsync(); await using var db = Context(); var foreign = await PatientAsync(db);
        var patient = await db.PatientProfiles.SingleAsync(p => p.Id == seed.PatientId); patient.FirstName = "PRIVATE_NAME"; patient.DocumentNumber = "PRIVATE_DOCUMENT";
        var pro = NutritionProfessional(seed.PatientId); db.Professionals.Add(pro);
        db.PatientProfessionalAssignments.Add(new() { PatientId = seed.PatientId, ProfessionalId = pro.Id }); await db.SaveChangesAsync();
        await AddCycleIntakesAsync(db, seed.PatientId, new(2026, 11, 14), MealCode.des, MealCode.alm);
        await AddCycleIntakesAsync(db, seed.PatientId, new(2026, 11, 8), MealCode.des);
        await AddCycleIntakesAsync(db, seed.PatientId, new(2026, 11, 16), MealCode.des);
        await AddCycleIntakesAsync(db, foreign.Id, new(2026, 11, 14), MealCode.des, MealCode.alm, MealCode.cen);
        await CompletionRepository(db, new(2026, 11, 14, 18, 0, 0, DateTimeKind.Utc)).CompleteTaskAsync(
            new CompleteTaskInput(seed.EnrollmentId, seed.Start.AddDays(82), TaskCode.podcast, null, null, null, null, null), default);
        // CompleteTask conserva el timestamp real del servidor. Este resumen usa
        // captura fija: ubicar su evidencia sintética dentro del intervalo de prueba.
        await db.TaskCompletions.Where(t => t.EnrollmentId == seed.EnrollmentId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.CompletedAt, new DateTime(2026, 11, 14, 18, 0, 0, DateTimeKind.Utc)));
        var actor = SummaryActor(pro.Id); var consent = Substitute.For<ILegalConsentService>();
        await using var mock = await SummaryMock.Start(); using var http = new HttpClient();
        mock.Reply = _ => { Assert.Null(db.Database.CurrentTransaction); return Task.FromResult(SummaryMock.Ready(new string('m', 200))); };
        var generate = SummaryHandler(db, actor, consent, mock.Client(http));
        var result = await generate.Handle(new(seed.PatientId, "en"), default);
        Assert.Equal("ok", result.Status); Assert.True(result.Summary!.IsEstimate); Assert.Equal(200, result.Summary.Model!.Length);
        Assert.Equal(new DateOnly(2026, 11, 9), result.Summary.StartDate); Assert.Equal(new DateOnly(2026, 11, 15), result.Summary.EndDate);
        var wire = Assert.Single(mock.Requests); using var json = JsonDocument.Parse(wire);
        Assert.Equal(new[] { "startDate", "endDate", "language", "facts" }, json.RootElement.EnumerateObject().Select(p => p.Name));
        var facts = json.RootElement.GetProperty("facts").EnumerateArray().ToArray();
        Assert.Contains(facts, f => f.GetProperty("ref").GetString() == "nutrition.records" && f.GetProperty("text").GetString()!.Contains("2 registros")
            && f.GetProperty("text").GetString()!.Contains("1 días"));
        Assert.Contains(facts, f => f.GetProperty("ref").GetString() == "podcasts.completions");
        foreach (var privateValue in new[] { "PRIVATE_", patient.Id.ToString(), patient.UserId.ToString(), foreign.Id.ToString(), "foodai/", "http", "imageKey", "patientId" })
            Assert.DoesNotContain(privateValue!, wire);
        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "copp-44b-contract-request.json"), wire, new UTF8Encoding(false));
        var row = await db.PatientAiBriefs.AsNoTracking().SingleAsync();
        Assert.Equal("draft", row.State); Assert.Equal("patient_summary", row.Kind);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(wire))), row.InputHash);
        Assert.Equal(result.Summary.Model, row.ModelName); Assert.Equal(result.Summary.GeneratedAt.UtcDateTime, row.GeneratedAt);
        var get = new GetPatientSummaryHandler(new PatientScopeGuard(actor, new PatientScopeStore(db)), consent, new PatientSummaryStore(db));
        Assert.Equal("empty", (await get.Handle(new(seed.PatientId, "es"), default)).Status);
        Assert.Equal(row.Id, (await get.Handle(new(seed.PatientId, "en"), default)).Summary!.Id); Assert.Single(mock.Requests);
        actor.HasPermissionAsync("Program.AiManage", Arg.Any<CancellationToken>()).Returns(false);
        Assert.Equal("ok", (await get.Handle(new(seed.PatientId, "en"), default)).Status);
        await Assert.ThrowsAsync<ForbiddenException>(() => generate.Handle(new(seed.PatientId), default));
        actor.HasPermissionAsync("Program.AiManage", Arg.Any<CancellationToken>()).Returns(true);
        await generate.Handle(new(seed.PatientId, "en"), default); Assert.Equal(2, await db.PatientAiBriefs.CountAsync());
        await Assert.ThrowsAsync<NotFoundException>(() => get.Handle(new(foreign.Id, "en"), default));
        actor.IsAdmin.Returns(true); Assert.Equal("empty", (await get.Handle(new(foreign.Id), default)).Status);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [NutritionPostgresFact]
    public async Task PatientSummary_IAFallida_InsuficienteTimeoutYCancelacion_NoPersistenNiBorranBorradorPrevio()
    {
        var seed = await CompletionSeedAsync(); await using var db = Context(); var actor = SummaryActor(Guid.NewGuid()); actor.IsAdmin.Returns(true);
        var consent = Substitute.For<ILegalConsentService>(); await using var mock = await SummaryMock.Start(); using var http = new HttpClient();
        var generate = SummaryHandler(db, actor, consent, mock.Client(http, 1));
        var previous = await generate.Handle(new(seed.PatientId), default); Assert.Equal("ok", previous.Status);
        mock.Reply = _ => Task.FromResult(Results.StatusCode(503));
        Assert.Null((await generate.Handle(new(seed.PatientId), default)).Summary);
        mock.Reply = _ => Task.FromResult(Results.Content("invalid", "application/json"));
        Assert.Equal("unavailable", (await generate.Handle(new(seed.PatientId), default)).Status);
        mock.Reply = _ => Task.FromResult(Results.Json(new PatientSummaryResponse("insufficient", "patient_summary", true, "", [], [], null,
            new(2026, 11, 15, 23, 0, 0, TimeSpan.Zero))));
        Assert.Equal("insufficient", (await generate.Handle(new(seed.PatientId), default)).Status);
        mock.Reply = async context => { await Task.Delay(Timeout.Infinite, context.RequestAborted); return SummaryMock.Ready(); };
        Assert.Equal("unavailable", (await generate.Handle(new(seed.PatientId), default)).Status);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generate.Handle(new(seed.PatientId), cancelled.Token));
        var remaining = await db.PatientAiBriefs.AsNoTracking().SingleAsync(); Assert.Equal(previous.Summary!.Id, remaining.Id);
    }

    [NutritionPostgresFact]
    public async Task PatientSummary_RevocacionDeAlcanceOConsentimientoDuranteRed_RechazaEscritura_SinDatosNoLlamaIA()
    {
        var seed = await CompletionSeedAsync(); await using var db = Context(); var pro = NutritionProfessional(seed.PatientId); db.Professionals.Add(pro);
        db.PatientProfessionalAssignments.Add(new() { PatientId = seed.PatientId, ProfessionalId = pro.Id }); await db.SaveChangesAsync();
        var actor = SummaryActor(pro.Id); var consent = Substitute.For<ILegalConsentService>(); await using var mock = await SummaryMock.Start(); using var http = new HttpClient();
        var generate = SummaryHandler(db, actor, consent, mock.Client(http));
        mock.Reply = async _ =>
        {
            await using var writer = Context(); await writer.PatientProfessionalAssignments.Where(a => a.PatientId == seed.PatientId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, "Inactive")); return SummaryMock.Ready();
        };
        await Assert.ThrowsAsync<NotFoundException>(() => generate.Handle(new(seed.PatientId), default));
        actor.IsAdmin.Returns(true); mock.Reply = _ => Task.FromResult(SummaryMock.Ready());
        var checks = 0;
        consent.EnsurePatientAsync(seed.PatientId, "patient-ai", Arg.Any<CancellationToken>()).Returns(_ =>
            ++checks == 1 ? Task.CompletedTask : Task.FromException(new AiConsentException("CONSENT_REQUIRED", "patient-ai", 403)));
        Assert.Equal("unavailable", (await generate.Handle(new(seed.PatientId), default)).Status);
        Assert.Empty(await db.PatientAiBriefs.ToListAsync());
        var empty = await PatientAsync(db); var calls = mock.Requests.Count;
        Assert.Equal("insufficient", (await generate.Handle(new(empty.Id), default)).Status); Assert.Equal(calls, mock.Requests.Count);
        await consent.Received().EnsurePatientAsync(seed.PatientId, "patient-ai", Arg.Any<CancellationToken>());
    }

    [NutritionPostgresFact]
    public async Task PatientSummary_Modelo200_MigracionReversibleSinTruncarDatos()
    {
        await using var db = Context(); var patient = await PatientAsync(db); var row = Brief(patient.Id); row.ModelName = new string('m', 200);
        db.PatientAiBriefs.Add(row); await db.SaveChangesAsync();
        var migrator = db.GetService<IMigrator>();
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => migrator.MigrateAsync("20261115174139_AddPatientAiBriefs"));
        Assert.Equal(200, (await db.PatientAiBriefs.AsNoTracking().SingleAsync()).ModelName!.Length);
        await db.PatientAiBriefs.ExecuteDeleteAsync(); await migrator.MigrateAsync("20261115174139_AddPatientAiBriefs");
        await migrator.MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges());
    }
}
