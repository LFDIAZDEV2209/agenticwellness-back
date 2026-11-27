using CoppAddresd.Auth.Data;
using CoppAddresd.Auth.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Auth.Services;

public sealed record VerifyPrivacyDeletion(Guid UserId, string? IdentityEvidence, string? PolicyReference,
    DateTimeOffset? EarlierDueAt);
public sealed record ErasePrivacyPreferences(Guid UserId, bool ConfirmedEligible, string? EvidenceReference);
public sealed record PrivacyDeletionReview(DateTime VerifiedAt, DateTime DueAt);
public sealed record PrivacyPreferencesErasure(DateTime CompletedAt, int RowsErased);

public enum DeletionOperationStatus { Ok, Invalid, NotFound, Conflict }
public sealed record DeletionOperation<T>(DeletionOperationStatus Status, T? Value = null) where T : class;

public sealed class PrivacyDeletionService(AuthDbContext db, IConfiguration configuration)
{
    private const int DefaultCompletionDays = 90;

    public bool Enabled => configuration.GetValue<bool>("PrivacyRequests:Enabled") &&
        configuration.GetValue<bool>("PrivacyRequests:DeletionEnabled");

    public async Task<DeletionOperation<PrivacyDeletionReview>> VerifyAsync(
        Guid requestId, Guid actorId, VerifyPrivacyDeletion input, CancellationToken ct)
    {
        if (input.UserId == Guid.Empty || !IsReference(input.IdentityEvidence) || !IsReference(input.PolicyReference))
            return new(DeletionOperationStatus.Invalid);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var request = await LockRequestAsync(requestId, input.UserId, ct);
        if (request is null)
            return new(DeletionOperationStatus.NotFound);

        var now = AtPostgresPrecision(DateTime.UtcNow);
        var earlier = input.EarlierDueAt is { } providedDueAt
            ? AtPostgresPrecision(providedDueAt.UtcDateTime)
            : (DateTime?)null;
        if (earlier < request.ReceivedAt)
            return new(DeletionOperationStatus.Invalid);

        var review = await db.PrivacyDeletionCases.SingleOrDefaultAsync(x => x.RequestId == requestId, ct);
        var verifiedAt = review?.VerifiedAt ?? now;
        var standardDueAt = verifiedAt.AddDays(DefaultCompletionDays);
        var dueAt = earlier is { } applicable && applicable < standardDueAt ? applicable : standardDueAt;

        if (review is null)
        {
            review = new()
            {
                RequestId = requestId,
                VerifiedAt = now,
                VerifiedBy = actorId,
                IdentityEvidence = input.IdentityEvidence!,
                DueAt = dueAt,
                PolicyReference = input.PolicyReference!
            };
            db.PrivacyDeletionCases.Add(review);
        }
        else
        {
            if (review.IdentityEvidence != input.IdentityEvidence || dueAt > review.DueAt ||
                (dueAt == review.DueAt && review.PolicyReference != input.PolicyReference))
                return new(DeletionOperationStatus.Conflict);

            if (dueAt < review.DueAt)
            {
                // Una regla aplicable descubierta después puede acortar, no extender.
                review.DueAt = dueAt;
                review.PolicyReference = input.PolicyReference!;
                review.DeadlineChangedAt = now;
                review.DeadlineChangedBy = actorId;
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(DeletionOperationStatus.Ok, new(review.VerifiedAt, review.DueAt));
    }

    public async Task<DeletionOperation<PrivacyPreferencesErasure>> ErasePreferencesAsync(
        Guid requestId, Guid actorId, ErasePrivacyPreferences input, CancellationToken ct)
    {
        if (input.UserId == Guid.Empty || !input.ConfirmedEligible || !IsReference(input.EvidenceReference))
            return new(DeletionOperationStatus.Invalid);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var request = await LockRequestAsync(requestId, input.UserId, ct);
        if (request is null)
            return new(DeletionOperationStatus.NotFound);
        var review = await db.PrivacyDeletionCases.SingleOrDefaultAsync(x => x.RequestId == requestId, ct);
        if (review is null)
            return new(DeletionOperationStatus.Conflict);
        if (review.PreferencesErasedAt is { } completedAt)
        {
            if (review.PreferencesErasureEvidence != input.EvidenceReference)
                return new(DeletionOperationStatus.Conflict);
            return new(DeletionOperationStatus.Ok, new(completedAt, review.PreferencesRowsErased!.Value));
        }

        // La fecha límite nunca es una espera mínima. La autorización concreta
        // cubre solo las preferencias no clínicas que el operador comprobó.
        var rows = await db.UserPreferences.Where(x => x.UserId == request.UserId).ExecuteDeleteAsync(ct);
        var erasedAt = AtPostgresPrecision(DateTime.UtcNow);
        review.PreferencesRowsErased = rows;
        review.PreferencesErasedAt = erasedAt;
        review.PreferencesErasedBy = actorId;
        review.PreferencesErasureEvidence = input.EvidenceReference;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new(DeletionOperationStatus.Ok, new(erasedAt, rows));
    }

    internal async Task<PrivacyRequest?> LockRequestAsync(Guid requestId, Guid userId, CancellationToken ct,
        bool includeClosed = false)
    {
        // El recibo serializa la primera verificación y los reintentos del mismo
        // paso. Se toma el bloqueo solo durante escrituras locales.
        var rows = await db.PrivacyRequests.FromSqlInterpolated($"""
            SELECT * FROM auth."PrivacyRequests"
            WHERE "Id" = {requestId} AND "Kind" = 'deletion'
              AND "UserId" = {userId} AND ({includeClosed} OR "ClosedAt" IS NULL)
            FOR UPDATE
            """).AsNoTracking().ToListAsync(ct);
        return rows.SingleOrDefault();
    }

    internal static bool IsReference(string? value) =>
        value is { Length: > 0 and <= 128 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is ':' or '.' or '_' or '/' or '-');

    // PostgreSQL guarda microsegundos. El primer comprobante debe ser idéntico
    // al de un reintento, incluso al comparar el texto ISO.
    internal static DateTime AtPostgresPrecision(DateTime value) => value.AddTicks(-(value.Ticks % 10));
}
