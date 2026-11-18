using CoppAddresd.Application.Features.Medications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CoppAddresd.Infrastructure.Services;

/// <summary>Revisa vencimientos y registros pendientes sin enviar comunicaciones externas.</summary>
public sealed class MedicationMonitoringWorker(IServiceScopeFactory scopes, IConfiguration configuration,
    ILogger<MedicationMonitoringWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Se inicia tras el arranque; el pipeline aplica las migraciones antes del servicio.
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!configuration.GetValue<bool>("Medications:Enabled") || !configuration.GetValue("Medications:MonitoringWorkerEnabled", true)) continue;
            try
            {
                Guid? after = null;
                while (!stoppingToken.IsCancellationRequested)
                {
                    await using var batchScope = scopes.CreateAsyncScope();
                    var reader = batchScope.ServiceProvider.GetRequiredService<IMedicationMonitoringStore>();
                    var patients = await reader.MonitoredPatientsAsync(after, 50, stoppingToken);
                    if (patients.Count == 0) break;
                    foreach (var id in patients)
                    {
                        try {
                            await using var scope = scopes.CreateAsyncScope();
                            await scope.ServiceProvider.GetRequiredService<IMedicationMonitoringStore>().RefreshAlertsAsync(id, stoppingToken);
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                        catch (Exception ex) { logger.LogWarning(ex, "No se pudo actualizar un seguimiento de medicamentos"); }
                    }
                    after = patients[^1];
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "No se pudo recorrer el seguimiento de medicamentos"); }
        }
    }
}
