using CoppAddresd.Auth.Configuration;
using CoppAddresd.Auth.Data;
using CoppAddresd.Auth.Entities;
using CoppAddresd.Auth.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoppAddresd.Auth.Services;

internal sealed class PaidActivationEmailWorker(
    IServiceScopeFactory scopes,
    IOptions<EmailSettings> emailSettings,
    ILogger<PaidActivationEmailWorker> logger) : BackgroundService
{
    private const int MaxAttempts = 3;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var interval = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await interval.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
                var payments = await Due(db, DateTime.UtcNow)
                    .OrderBy(activation => activation.NextEmailAttemptAt)
                    .Select(activation => activation.PaymentId)
                    .Take(16)
                    .ToListAsync(stoppingToken);
                var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
                var links = scope.ServiceProvider.GetRequiredService<PaidActivationLinks>();
                foreach (var paymentId in payments)
                    await DeliverAsync(db, sender, links, paymentId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                logger.LogWarning("Error al procesar la cola de activación de prueba ({ErrorType}).", error.GetType().Name);
            }
        }
    }

    private async Task DeliverAsync(
        AuthDbContext db, IEmailSender sender, PaidActivationLinks links, Guid paymentId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var leaseEnd = now.AddSeconds(30);
        var claimed = await Due(db, now).Where(activation => activation.PaymentId == paymentId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(activation => activation.EmailLeaseUntil, leaseEnd)
                .SetProperty(activation => activation.EmailAttempts, activation => activation.EmailAttempts + 1),
                ct);
        if (claimed == 0)
            return;

        var job = await db.PaidActivations.AsNoTracking()
            .Where(activation => activation.PaymentId == paymentId && activation.EmailLeaseUntil == leaseEnd)
            .SingleOrDefaultAsync(ct);
        if (job is null)
            return;

        DateTime? nextAttempt = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            var message = TransactionalEmailTemplate.Build(new(
                TransactionalEmailTemplate.PaidActivationDemoCode,
                job.Email,
                job.Language,
                new Dictionary<string, string?> { ["enlace_activacion"] = links.Issue(job) }
            ), emailSettings.Value);
            await sender.SendAsync(message, deadline.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            logger.LogWarning("Falló el correo de activación de prueba ({ErrorType}).", error.GetType().Name);
            if (job.EmailAttempts < MaxAttempts)
                nextAttempt = DateTime.UtcNow.AddSeconds(job.EmailAttempts == 1 ? 10 : 30);
        }

        await db.PaidActivations
            .Where(activation =>
                activation.PaymentId == paymentId
                && activation.EmailRevision == job.EmailRevision
                && activation.EmailLeaseUntil == leaseEnd)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(activation => activation.NextEmailAttemptAt, nextAttempt)
                .SetProperty(activation => activation.EmailLeaseUntil, (DateTime?)null),
                ct);
    }

    private static IQueryable<PaidActivation> Due(AuthDbContext db, DateTime now) =>
        db.PaidActivations.Where(activation =>
            activation.MembershipPaid && activation.MembershipState == "active"
            && (activation.MembershipExpiresAt ?? activation.ConfirmedAt.AddMonths(activation.Plan == "annual" ? 12 : 1)) > now
            && activation.NextEmailAttemptAt <= now
            && activation.EmailAttempts < MaxAttempts
            && (activation.EmailLeaseUntil == null || activation.EmailLeaseUntil <= now));
}
