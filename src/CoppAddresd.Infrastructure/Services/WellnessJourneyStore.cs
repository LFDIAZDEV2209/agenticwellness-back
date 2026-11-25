using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Services;

public sealed record WellnessJourneyAccess(Guid PatientId, Guid PaymentId, bool ConsentCurrent, bool MembershipActive);

public sealed record WellnessJourneySnapshot(
    Guid GenerationId,
    Guid SubmissionId,
    string Status,
    int Attempts,
    DateTimeOffset? StartedAt,
    string? ErrorCode,
    string? ResultJson);

public sealed record WellnessJourneyClaim(Guid GenerationId, Guid LeaseId, int Attempts);

public sealed class WellnessJourneyStore(AppDbContext db, TimeProvider clock, IAccountLegalConsentService accountConsents)
{
    private sealed record PaidJourneyMember(Guid PatientId, Guid PaymentId, string Locale, bool MembershipActive);
    private const string Pending = "pending";
    private const string Running = "running";
    private const string Ready = "ready";
    private const string Failed = "failed";

    public async Task<WellnessJourneyAccess?> GetAccessAsync(Guid userId, CancellationToken ct)
    {
        var member = await ReadMemberAsync(userId, ct);
        if (member is null) return null;
        try
        {
            var status = await accountConsents.GetJourneyStatusAsync(userId, member.Locale, ct);
            return new(member.PatientId, member.PaymentId, !status.AcceptanceRequired, member.MembershipActive);
        }
        catch (AccountLegalConsentException error) when (error.StatusCode == 503)
        {
            return new(member.PatientId, member.PaymentId, false, member.MembershipActive);
        }
    }

    public async Task<string?> GetLegalLocaleAsync(Guid userId, CancellationToken ct) =>
        (await ReadMemberAsync(userId, ct))?.Locale;

    private Task<PaidJourneyMember?> ReadMemberAsync(Guid userId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return db.Database.SqlQuery<PaidJourneyMember>($"""
            SELECT p.id AS "PatientId", payment."PaymentId" AS "PaymentId", payment."Language" AS "Locale",
                (payment."MembershipPaid" AND payment."MembershipState" = 'active'
                    AND COALESCE(payment."MembershipExpiresAt",
                        ((payment."ConfirmedAt" AT TIME ZONE 'UTC') +
                            CASE WHEN payment."Plan" = 'annual' THEN INTERVAL '12 months'
                                ELSE INTERVAL '1 month' END) AT TIME ZONE 'UTC') > {now}) AS "MembershipActive"
            FROM app.patient_profiles p
            JOIN LATERAL (
                SELECT purchase."PaymentId", purchase."Language", purchase."MembershipPaid",
                    purchase."MembershipState", purchase."MembershipExpiresAt", purchase."ConfirmedAt", purchase."Plan"
                FROM auth."PaidActivations" purchase
                WHERE purchase."LinkedUserId" = p.user_id AND purchase."RedeemedAt" IS NOT NULL
                ORDER BY purchase."RedeemedAt" DESC, purchase."PaymentId" DESC
                LIMIT 1
            ) payment ON TRUE
            WHERE p.user_id = {userId} AND p.deleted_at IS NULL AND p.status = 'Activo'
            """).SingleOrDefaultAsync(ct);
    }

    public async Task<WellnessJourneySnapshot?> SubmitAsync(
        WellnessJourneyAccess access, Guid submissionId, string draftHash, string draftJson, CancellationToken ct)
    {
        var generationId = Guid.NewGuid();
        var now = clock.GetUtcNow();
        // La fila es el trabajo durable. Se registra junto con las respuestas
        // aceptadas en una sola escritura, antes de devolver la confirmación.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO app.wellness_journey_generations
                (id, patient_id, payment_id, submission_id, draft_hash, draft_json, created_at, status, attempts)
            VALUES ({generationId}, {access.PatientId}, {access.PaymentId}, {submissionId}, {draftHash},
                CAST({draftJson} AS jsonb), {now}, {Pending}, 1)
            ON CONFLICT (payment_id) DO NOTHING
            """, ct);

        var row = await ForPayment(access).SingleOrDefaultAsync(ct);
        return row?.DraftHash == draftHash ? Snapshot(row) : null;
    }

    public async Task<WellnessJourneySnapshot?> GetAsync(
        WellnessJourneyAccess access, Guid? generationId, CancellationToken ct)
    {
        var query = ForPayment(access);
        if (generationId is { } id)
            query = query.Where(row => row.Id == id);
        var row = await query.SingleOrDefaultAsync(ct);
        return row is null ? null : Snapshot(row);
    }

    public async Task<WellnessJourneySnapshot?> RetryAsync(
        WellnessJourneyAccess access, Guid generationId, int expectedAttempts, CancellationToken ct)
    {
        var job = db.Set<WellnessJourneyGeneration>().Where(row =>
            row.Id == generationId && row.PatientId == access.PatientId && row.PaymentId == access.PaymentId);
        await job.Where(row => row.Status == Failed && row.Attempts == expectedAttempts)
            .ExecuteUpdateAsync(update => update
                .SetProperty(row => row.Status, Pending)
                .SetProperty(row => row.Attempts, row => row.Attempts + 1)
                .SetProperty(row => row.ErrorCode, (string?)null),
                ct);

        return await GetAsync(access, generationId, ct);
    }

    public async Task<WellnessJourneyClaim?> ClaimNextAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var candidates = db.Set<WellnessJourneyGeneration>()
            .Where(row => row.Status == Pending || (row.Status == Running && row.LeaseUntil <= now));
        var next = await candidates.AsNoTracking().OrderBy(row => row.CreatedAt).ThenBy(row => row.Id)
            .Select(row => new { row.Id, row.Attempts })
            .FirstOrDefaultAsync(ct);

        if (next is null)
            return null;

        var lease = Guid.NewGuid();
        var deadline = now.AddSeconds(20);
        var claimed = await candidates.Where(row => row.Id == next.Id && row.Attempts == next.Attempts)
            .ExecuteUpdateAsync(update => update
                .SetProperty(row => row.Status, Running)
                .SetProperty(row => row.StartedAt, row => row.StartedAt ?? now)
                .SetProperty(row => row.LeaseId, lease)
                .SetProperty(row => row.LeaseUntil, deadline),
                ct);
        return claimed == 0 ? null : new(next.Id, lease, next.Attempts);
    }

    public async Task FinishAsync(WellnessJourneyClaim claim, string? resultJson, string? errorCode, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var status = resultJson is null ? Failed : Ready;
        // El lease y la revisión evitan que un worker atrasado publique sobre otro intento.
        await db.Set<WellnessJourneyGeneration>()
            .Where(row => row.Id == claim.GenerationId && row.Attempts == claim.Attempts
                && row.Status == Running && row.LeaseId == claim.LeaseId && row.LeaseUntil > now)
            .ExecuteUpdateAsync(update => update
                .SetProperty(row => row.Status, status)
                .SetProperty(row => row.ResultJson, resultJson)
                .SetProperty(row => row.ErrorCode, errorCode)
                .SetProperty(row => row.LeaseId, (Guid?)null)
                .SetProperty(row => row.LeaseUntil, (DateTimeOffset?)null),
                ct);
    }

    private IQueryable<WellnessJourneyGeneration> ForPayment(WellnessJourneyAccess access) =>
        db.Set<WellnessJourneyGeneration>().AsNoTracking()
            .Where(row => row.PatientId == access.PatientId && row.PaymentId == access.PaymentId);

    private static WellnessJourneySnapshot Snapshot(WellnessJourneyGeneration row) =>
        new(row.Id, row.SubmissionId, row.Status, row.Attempts, row.StartedAt, row.ErrorCode, row.ResultJson);
}
