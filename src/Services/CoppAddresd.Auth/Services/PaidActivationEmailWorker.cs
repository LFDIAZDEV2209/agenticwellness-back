using System.Text.Encodings.Web;
using CoppAddresd.Auth.Data;
using CoppAddresd.Auth.Entities;
using CoppAddresd.Auth.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Auth.Services;

internal sealed class PaidActivationEmailWorker(
    IServiceScopeFactory scopes,
    ILogger<PaidActivationEmailWorker> logger) : BackgroundService
{
    internal const string TemplateCode = "PAID-ACTIVATION-DEMO";
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
            await sender.SendAsync(Message(job, links.Issue(job)), deadline.Token);
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
            activation.NextEmailAttemptAt <= now
            && activation.EmailAttempts < MaxAttempts
            && (activation.EmailLeaseUntil == null || activation.EmailLeaseUntil <= now));

    private static EmailMessage Message(PaidActivation activation, string url)
    {
        var (subject, intro, label) = activation.Language == "en"
            ? ("Welcome to Copp Adresd", "Your simulated payment was confirmed. Open this link to continue activation.", "Activate your membership")
            : ("Bienvenido a Copp Adresd", "Tu pago simulado se confirmó. Abre este enlace para continuar la activación.", "Activa tu membresía");
        var html = $"<p>{intro}</p><p><a href=\"{HtmlEncoder.Default.Encode(url)}\">{label}</a></p>";
        return new(activation.Email, subject, html, $"{intro}\n\n{url}") { TemplateCode = TemplateCode };
    }
}
