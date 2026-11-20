using CoppAddresd.Application.Features.Medications;
using CoppAddresd.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CoppAddresd.Infrastructure.Services;

public sealed class MedicationReminderWorker(IServiceScopeFactory scopes, IConfiguration configuration,
    ILogger<MedicationReminderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!configuration.GetValue<bool>("Medications:Enabled")) continue;
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "No se pudo recorrer la agenda de recordatorios"); }
        }
    }

    internal async Task SweepAsync(CancellationToken ct)
    {
        await using var listingScope = scopes.CreateAsyncScope();
        var reader = listingScope.ServiceProvider.GetRequiredService<MedicationControlStore>();
        if (!listingScope.ServiceProvider.GetServices<IMedicationReminderChannel>().Any(channel => channel.Enabled)) return;
        var followUp = TimeSpan.FromMinutes(Math.Clamp(
            configuration.GetValue("Medications:ReminderFollowUpMinutes", 30), 15, 90));
        Guid? cursor = null;
        while (!ct.IsCancellationRequested)
        {
            var patients = await reader.ListReminderPatientsAsync(cursor, ct,
                demoOnly: MedicationReminderTestScope.IsDemoOnly(configuration));
            if (patients.Count == 0) return;
            foreach (var patientId in patients)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var store = scope.ServiceProvider.GetRequiredService<MedicationControlStore>();
                    foreach (var channel in scope.ServiceProvider.GetServices<IMedicationReminderChannel>().Where(c => c.Enabled))
                    {
                        var claimed = await store.ClaimRemindersAsync(patientId, channel.Code, followUp, ct);
                        foreach (var (id, occurrence) in claimed)
                        {
                            try
                            {
                                // Segunda comprobación tras el commit y antes de llamar al proveedor.
                                var current = await store.GetPendingAsync(patientId, occurrence.Dose.Id, occurrence.Dose.ScheduledAt, ct);
                                if (current is not null) await channel.DispatchAsync(current, id, ct);
                            }
                            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                            catch (Exception ex)
                            {
                                logger.LogWarning("Recordatorio {DeliveryId} no confirmado en {Channel}: {ErrorType}",
                                    id, channel.Code, ex.GetType().Name);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { logger.LogWarning(ex, "No se pudieron evaluar los recordatorios del paciente"); }
            }
            cursor = patients[^1];
        }
    }
}
