using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

public sealed record VitaDocumentIndexJob(Guid DocumentId, Guid PatientId, Guid Generation, Guid ClaimToken, int Attempts);

/// <summary>El lease se confirma antes de leer storage o invocar IA.</summary>
public sealed class VitaDocumentIndexOutbox(AppDbContext db)
{
    public async Task<VitaDocumentIndexJob?> ClaimAsync(CancellationToken ct)
    {
        var token = Guid.NewGuid();
        var jobs = await db.Database.SqlQuery<VitaDocumentIndexJob>($"""
            WITH candidate AS (
                SELECT document_id, patient_id FROM app.vita_document_index_outbox
                WHERE next_attempt_at <= now() AND (lease_until IS NULL OR lease_until <= now())
                ORDER BY next_attempt_at, document_id, patient_id LIMIT 1 FOR UPDATE SKIP LOCKED
            )
            UPDATE app.vita_document_index_outbox q
            SET claim_token = {token}, lease_until = now() + interval '2 minutes', attempts = LEAST(q.attempts, 1000) + 1
            FROM candidate c WHERE q.document_id = c.document_id AND q.patient_id = c.patient_id
            RETURNING q.document_id AS "DocumentId", q.patient_id AS "PatientId",
                q.generation AS "Generation", q.claim_token AS "ClaimToken", q.attempts AS "Attempts"
            """).ToListAsync(ct);
        return jobs.SingleOrDefault();
    }

    public async Task CompleteAsync(VitaDocumentIndexJob job, CancellationToken ct)
    {
        var completed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM app.vita_document_index_outbox
            WHERE document_id = {job.DocumentId} AND patient_id = {job.PatientId}
              AND claim_token = {job.ClaimToken} AND generation = {job.Generation}
            """, ct);
        // Si cambió el documento mientras se procesaba, se libera el lease y queda la generación nueva.
        if (completed == 0) await RetryAsync(job, 0, ct);
    }

    public Task RetryAsync(VitaDocumentIndexJob job, int delaySeconds, CancellationToken ct)
        => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE app.vita_document_index_outbox SET claim_token = NULL, lease_until = NULL,
                next_attempt_at = CASE WHEN generation = {job.Generation}
                    THEN now() + make_interval(secs => {delaySeconds}) ELSE next_attempt_at END
            WHERE document_id = {job.DocumentId} AND patient_id = {job.PatientId} AND claim_token = {job.ClaimToken}
            """, ct);

    public Task<bool> IsCurrentAsync(VitaDocumentIndexJob job, CancellationToken ct)
        => db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (SELECT 1 FROM app.vita_document_index_outbox
                WHERE document_id = {job.DocumentId} AND patient_id = {job.PatientId}
                  AND claim_token = {job.ClaimToken} AND generation = {job.Generation}
                  AND lease_until > now()) AS "Value"
            """).SingleAsync(ct);
}
