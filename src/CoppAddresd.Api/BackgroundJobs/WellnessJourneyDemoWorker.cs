using System.Text.Json;
using CoppAddresd.Api.Configuration;
using CoppAddresd.Infrastructure.Services;

namespace CoppAddresd.Api.BackgroundJobs;

public sealed record WellnessJourneyDemoPlan(string Version, string Title, string[] Items);
public sealed record WellnessJourneyDemoMessage(Guid Id, string Title, string Body, DateTimeOffset CreatedAt);

public sealed record WellnessJourneyDemoOutput(
    WellnessJourneyDemoPlan Exercise,
    WellnessJourneyDemoPlan Nutrition,
    WellnessJourneyDemoMessage[] Inbox);

public sealed record WellnessJourneyDemoFixture(WellnessJourneyDemoOutput Es, WellnessJourneyDemoOutput En)
{
    private const string Version = "paid-wellness-demo-v1";

    public static WellnessJourneyDemoFixture Produce(Guid generationId, DateTimeOffset createdAt) =>
        new(
            new(
                new(Version, "Plan de ejercicio de demostración", ["Caminata de ejemplo", "Sesión de movilidad de ejemplo"]),
                new(Version, "Plan de nutrición de demostración", ["Desayuno de ejemplo: avena con fruta", "Almuerzo de ejemplo: lentejas con vegetales"]),
                [new(generationId, "Tu primera jornada", "Los planes de demostración ya están listos.", createdAt)]),
            new(
                new(Version, "Demo exercise plan", ["Sample walk", "Sample mobility session"]),
                new(Version, "Demo nutrition plan", ["Sample breakfast: oats and fruit", "Sample lunch: lentils and vegetables"]),
                [new(generationId, "Your first day", "Your demo plans are ready.", createdAt)]));
}

internal sealed class WellnessJourneyDemoWorker(
    IServiceScopeFactory scopes, WellnessJourneyDemoOptions options, TimeProvider clock,
    ILogger<WellnessJourneyDemoWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var interval = new PeriodicTimer(TimeSpan.FromMilliseconds(500), clock);
        try
        {
            while (await interval.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var store = scope.ServiceProvider.GetRequiredService<WellnessJourneyStore>();
                    var work = await store.ClaimNextAsync(stoppingToken);
                    if (work is null)
                        continue;

                    // La espera garantiza un estado visible para probar la recuperación.
                    // El trabajo ya está confirmado y no conserva ninguna transacción abierta.
                    await Task.Delay(options.Delay, clock, stoppingToken);
                    if (options.FailureOnFirstAttempt && work.Attempts == 1)
                    {
                        await store.FinishAsync(work, null, "fixture_generation_failed", stoppingToken);
                        continue;
                    }

                    var output = WellnessJourneyDemoFixture.Produce(work.GenerationId, clock.GetUtcNow());
                    await store.FinishAsync(work, JsonSerializer.Serialize(output, JsonSerializerOptions.Web), null, stoppingToken);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    logger.LogWarning("WellnessJourney.DemoWorkerError ErrorType={ErrorType}", error.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
