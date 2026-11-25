namespace CoppAddresd.Auth.Services;

/// <summary>Retira la carga cifrada vencida sin eliminar el comprobante de cumplimiento.</summary>
public sealed class PrivacyArtifactExpiryService(IServiceScopeFactory scopes, ILogger<PrivacyArtifactExpiryService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
                if (!string.IsNullOrEmpty(configuration["PrivacyRequests:ArtifactKey"]))
                    await scope.ServiceProvider.GetRequiredService<PrivacyFulfillmentService>().ClearExpiredAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Could not retire expired privacy export artifacts");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
