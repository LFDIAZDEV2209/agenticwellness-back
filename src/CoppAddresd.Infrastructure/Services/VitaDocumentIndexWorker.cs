using CoppAddresd.Application.Features.LegalDocuments;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Persistence;
using CoppAddresd.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CoppAddresd.Infrastructure.Services;

public sealed class VitaDocumentIndexProcessor(AppDbContext db, VitaDocumentIndexOutbox outbox,
    VitaDocumentTextReader reader, VitaDocumentIndexClient ai, ILegalConsentService consent,
    ILogger<VitaDocumentIndexProcessor> logger)
{
    public async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        var job = await outbox.ClaimAsync(ct);
        if (job is null) return false;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(50));
            await DeliverAsync(job, deadline.Token);
            await outbox.CompleteAsync(job, ct);
        }
        catch (AiConsentException error) when (error.StatusCode == 403)
        {
            // El consentimiento puede aceptarse después de la subida.
            await outbox.RetryAsync(job, 1800, ct);
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Indexación privada pendiente de reintento. Intento={Attempt}, causa={Reason}.",
                job.Attempts, error.GetType().Name);
            await outbox.RetryAsync(job, Math.Min(300, 5 << Math.Min(6, job.Attempts - 1)), ct);
        }
        return true;
    }

    private async Task DeliverAsync(VitaDocumentIndexJob job, CancellationToken ct)
    {
        var document = await db.Documents.AsNoTracking()
            .Where(d => d.Id == job.DocumentId && d.PatientId == job.PatientId && d.Status == "Ready" && d.DeletedAt == null
                && d.Patient != null && d.Patient.DeletedAt == null
                && (d.ParentDocumentId == null || (d.Parent!.PatientId == job.PatientId
                    && d.Parent.Status == "Ready" && d.Parent.DeletedAt == null)))
            .Select(d => new { d.Title, d.CreatedAt, d.StorageKey, d.FileSizeBytes })
            .SingleOrDefaultAsync(ct);
        if (document is null)
        {
            // El paciente de la baja viene exclusivamente del evento persistido.
            await ai.DeleteAsync(job.PatientId, job.DocumentId, ct);
            return;
        }

        await consent.EnsurePatientAsync(job.PatientId, LegalConsentScopes.Chat, ct);
        var text = await reader.ReadAsync(job.PatientId, job.DocumentId, document.StorageKey, document.FileSizeBytes, ct);
        if (!await outbox.IsCurrentAsync(job, ct)) return;
        if (text is null)
        {
            await ai.DeleteAsync(job.PatientId, job.DocumentId, ct);
            return;
        }
        await consent.EnsurePatientAsync(job.PatientId, LegalConsentScopes.Chat, ct);
        await ai.IndexAsync(job.PatientId, job.DocumentId, document.Title, document.CreatedAt, text, ct);
    }
}

public sealed class VitaDocumentIndexWorker(IServiceScopeFactory scopes, IConfiguration configuration,
    ILogger<VitaDocumentIndexWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue<bool>("Vita:DocumentIndexingEnabled")) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var worker = scope.ServiceProvider.GetRequiredService<VitaDocumentIndexProcessor>();
                if (await worker.ProcessNextAsync(stoppingToken)) continue;
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("No se pudo procesar la cola documental. Causa={Reason}.", error.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
