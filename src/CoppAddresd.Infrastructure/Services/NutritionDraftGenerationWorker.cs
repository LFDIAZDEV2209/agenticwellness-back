using CoppAddresd.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CoppAddresd.Infrastructure.Services;

public sealed class NutritionDraftGenerationWorker(IServiceScopeFactory scopes, ILogger<NutritionDraftGenerationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextReconcile = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var workflow = scope.ServiceProvider.GetRequiredService<INutritionDraftWorkflow>();
                if (DateTime.UtcNow >= nextReconcile)
                {
                    // Reutiliza el tick existente; el aviso ERP no introduce otra cola ni otro hosted service.
                    if (scope.ServiceProvider.GetService<CoppAddresd.Application.Features.Nutrition.Queries.INutritionCycleClosingNoticeStore>() is { } notices)
                    {
                        try { await notices.EmitAsync(DateTimeOffset.UtcNow, stoppingToken); }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                        catch (Exception ex) { logger.LogWarning("Nutrition.ClosingNoticeRetry FailureType={FailureType}", ex.GetType().Name); }
                    }
                    await workflow.ReconcileAsync(stoppingToken);
                    nextReconcile = DateTime.UtcNow.AddMinutes(1);
                }
                if (await workflow.ProcessNextAsync(stoppingToken)) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Nutrition.DraftWorkerRetry FailureType={FailureType}", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
