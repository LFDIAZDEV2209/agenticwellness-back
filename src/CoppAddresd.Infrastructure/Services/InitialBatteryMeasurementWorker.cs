using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CoppAddresd.Infrastructure.Services;

internal sealed class InitialBatteryMeasurementWorker(
    IServiceScopeFactory scopes, ILogger<InitialBatteryMeasurementWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var sync = scope.ServiceProvider.GetRequiredService<InitialBatteryMeasurementSync>();
                if (await sync.ReconcileBatchAsync(stoppingToken) > 0) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogWarning("Se reanudará la sincronización de la batería inicial: tipo={FailureType}", ex.GetType().Name);
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
