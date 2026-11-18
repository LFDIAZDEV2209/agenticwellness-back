using CoppAddresd.Application.Interfaces;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Api.Security;

/// <summary>Entrega confirmaciones idempotentes a Auth; jamás vuelve a crear cuentas ni modifica roles.</summary>
public sealed class WelcomeEmailFinalizationWorker(
    IServiceScopeFactory scopes,
    ILogger<WelcomeEmailFinalizationWorker> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = DateTime.UtcNow;
                var users = await db
                    .WelcomeEmailFinalizations.AsNoTracking()
                    .Where(x => x.CompletedAt == null && x.NextAttemptAt <= now)
                    .OrderBy(x => x.NextAttemptAt)
                    .Select(x => x.UserId)
                    .Take(20)
                    .ToListAsync(stoppingToken);
                var client = scope.ServiceProvider.GetRequiredService<IAuthInvitationsClient>();
                foreach (var userId in users)
                    await client.FlushWelcomeAsync(userId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    "Confirmaciones de bienvenida no disponibles: {ErrorType}",
                    ex.GetType().Name
                );
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
