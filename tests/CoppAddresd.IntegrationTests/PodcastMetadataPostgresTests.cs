using System.Text;
using System.Text.Json;
using CoppAddresd.Application.Common;
using CoppAddresd.Application.Features.Media.PodcastMetadata;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Infrastructure.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    private sealed class PodcastMetadataMock(WebApplication app) : IAsyncDisposable
    {
        public string Address = "";
        public List<string> Requests { get; } = [];
        public Func<HttpContext, Task<IResult>> Reply = _ => Task.FromResult(Ready());
        public static IResult Ready(string? model = "synthetic-podcast-model") => Results.Json(new PodcastMetadataResponse("ok", true,
            new("Una introducción a los hábitos cotidianos.", "Habitos", [" Constancia ", "hábitos", "constancia"]), model,
            new(2026, 11, 15, 23, 0, 0, TimeSpan.Zero)));
        public static async Task<PodcastMetadataMock> Start()
        {
            var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            var app = builder.Build(); var mock = new PodcastMetadataMock(app);
            app.MapPost(PodcastMetadataClient.Endpoint, async (HttpContext context) =>
            {
                Assert.Equal("synthetic-podcast-key", context.Request.Headers["X-Internal-Key"].ToString());
                using var reader = new StreamReader(context.Request.Body); mock.Requests.Add(await reader.ReadToEndAsync(context.RequestAborted));
                return await mock.Reply(context);
            });
            await app.StartAsync(); mock.Address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return mock;
        }
        public PodcastMetadataClient Client(HttpClient http, int seconds = 25) => new(http,
            Options.Create(new AiServiceSettings { BaseUrl = Address, InternalApiKey = "synthetic-podcast-key", TimeoutSeconds = seconds }));
        public async ValueTask DisposeAsync() => await app.DisposeAsync();
    }

    [NutritionPostgresFact]
    public async Task PodcastMetadata_HTTPReal_NoCreaEditaPublicaNiAfectaPlanes_TimeoutDeCuerpoYCancelacion()
    {
        await using var db = Context(); var seed = await EffectiveSeedAsync(db);
        async Task<string> Catalog() => JsonSerializer.Serialize(await db.MediaItems.AsNoTracking().OrderBy(m => m.Id).Select(m => new
            { m.Id, m.Title, m.Description, m.Category, m.Tags, m.Status, m.StorageKey, m.UpdatedAt }).ToArrayAsync());
        async Task<string> Weeks() => JsonSerializer.Serialize(await db.ProgramWeeks.AsNoTracking().OrderBy(w => w.Id).Select(w => new
            { w.Id, w.TasksSnapshot, w.UpdatedAt }).ToArrayAsync());
        var beforeMedia = await Catalog(); var beforeWeeks = await Weeks();
        var briefs = await db.PatientAiBriefs.CountAsync(); var audit = await db.ActivityLogs.CountAsync();
        var actor = SummaryActor(Guid.NewGuid());
        await using var mock = await PodcastMetadataMock.Start(); using var http = new HttpClient();
        SuggestPodcastMetadataCommandHandler Handler(int seconds = 25) => new(actor, mock.Client(http, seconds), NullLogger<SuggestPodcastMetadataCommandHandler>.Instance);
        mock.Reply = _ => { Assert.Null(db.Database.CurrentTransaction); return Task.FromResult(PodcastMetadataMock.Ready(new('m', 200))); };
        var input = new PodcastMetadataInput("Crear hábitos sostenibles", "es", "Introducción a la constancia y a los pequeños pasos.");
        var result = await Handler().Handle(new(input), default);
        Assert.Equal("ok", result.Status); Assert.True(result.IsEstimate); Assert.Equal(200, result.Model!.Length);
        Assert.Equal(new[] { "constancia", "hábitos" }, result.Draft!.Tags);
        Assert.Equal(TimeSpan.Zero, result.GeneratedAt!.Value.Offset);
        var wire = Assert.Single(mock.Requests); using var payload = JsonDocument.Parse(wire);
        Assert.Equal(new[] { "title", "language", "context" }, payload.RootElement.EnumerateObject().Select(x => x.Name));
        foreach (var value in new[] { seed.Patient.ToString(), seed.Enrollment.ToString(), seed.Podcast.ToString(), "storageKey", "status", "patientId" }) Assert.DoesNotContain(value, wire);
        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "copp-46b-contract-request.json"), wire, new UTF8Encoding(false));
        mock.Reply = _ => Task.FromResult(PodcastMetadataMock.Ready(null));
        Assert.Null((await Handler().Handle(new(new("Editorial", "en")), default)).Model);
        mock.Reply = _ => Task.FromResult(Results.StatusCode(503));
        Assert.Null((await Handler().Handle(new(input), default)).Draft);
        mock.Reply = _ => Task.FromResult(Results.Content("synthetic-private-error", "application/json"));
        Assert.Equal("unavailable", (await Handler().Handle(new(input), default)).Status);
        mock.Reply = async context => {
            context.Response.ContentType = "application/json"; await context.Response.StartAsync(context.RequestAborted);
            await Task.Delay(2000, context.RequestAborted); return PodcastMetadataMock.Ready();
        };
        Assert.Equal("unavailable", (await Handler(1).Handle(new(input), default)).Status);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mock.Reply = async context => { entered.SetResult(); await Task.Delay(5000, context.RequestAborted); return PodcastMetadataMock.Ready(); };
        using var cancellation = new CancellationTokenSource(); var pending = Handler().Handle(new(input), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(beforeMedia, await Catalog()); Assert.Equal(beforeWeeks, await Weeks());
        Assert.Equal(briefs, await db.PatientAiBriefs.CountAsync()); Assert.Equal(audit, await db.ActivityLogs.CountAsync());
        Assert.Empty(await db.RoutineAssignments.ToListAsync()); Assert.Empty(await db.NutritionPlanAssignments.ToListAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
