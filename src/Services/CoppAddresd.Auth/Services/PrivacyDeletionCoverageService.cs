using CoppAddresd.Auth.Data;
using CoppAddresd.Auth.Entities;
using Microsoft.EntityFrameworkCore;
using static CoppAddresd.Auth.Services.PrivacyDeletionService;

namespace CoppAddresd.Auth.Services;

public sealed record RecordPrivacyDisposition(Guid UserId, string? Outcome, long? RemainingCount,
    string? EvidenceReference, string? ActionReference, string? PolicyReference,
    string? ArchiveReference, string? RestoreReference, DateTimeOffset? ReviewAt);
public sealed record CompletePrivacyDeletion(Guid UserId, string? InventoryReference, string? RestoreReference);
public sealed record PrivacySourceReceipt(string Source, string Outcome, DateTime RecordedAt, int? RowsErased);
public sealed record PrivacyDeletionCompletion(DateTime? CompletedAt, int SourcesVerified, IReadOnlyList<string> MissingSources);

public sealed class PrivacyDeletionCoverageService(AuthDbContext db, PrivacyDeletionService deletion)
{
    private const string AuthPreferences = "auth-preferences";
    private const string AppDeviceTokens = "app-device-tokens";
    private const string Deleted = "erased";
    private const string Absent = "absent";
    private const string Retained = "retained";
    private const string Expired = "expired";

    private static readonly IReadOnlyDictionary<string, SourceKind> Sources = new Dictionary<string, SourceKind>
    {
        [AuthPreferences] = SourceKind.Internal,
        [AppDeviceTokens] = SourceKind.Internal,
        ["identity-profile"] = SourceKind.Data,
        ["wellness-legacy"] = SourceKind.Data,
        ["wellness-motor365"] = SourceKind.Data,
        ["consumer-vitals"] = SourceKind.Data,
        ["clinical-records"] = SourceKind.Protected,
        ["telemedicine"] = SourceKind.Protected,
        ["community"] = SourceKind.Data,
        ["ai-service"] = SourceKind.Data,
        ["food-ai-storage"] = SourceKind.Data,
        ["audit-consents"] = SourceKind.Protected,
        ["service-logs"] = SourceKind.Data,
        ["openai"] = SourceKind.Provider,
        ["anthropic"] = SourceKind.Provider,
        ["elevenlabs"] = SourceKind.Provider,
        ["twilio"] = SourceKind.Provider,
        ["whatsapp-meta"] = SourceKind.Provider,
        ["transactional-email"] = SourceKind.Provider,
        ["mobile-push"] = SourceKind.Provider,
        ["retired-archives"] = SourceKind.Archive,
        ["rds-pitr"] = SourceKind.Backup,
        ["rds-manual"] = SourceKind.Backup,
        ["s3-versions"] = SourceKind.Backup,
        ["external-backups"] = SourceKind.Backup
    };

    public async Task<DeletionOperation<PrivacySourceReceipt>> EraseDeviceTokensAsync(
        Guid requestId, Guid actorId, ErasePrivacyPreferences input, CancellationToken ct)
    {
        if (input.UserId == Guid.Empty || !input.ConfirmedEligible || !IsReference(input.EvidenceReference))
            return new(DeletionOperationStatus.Invalid);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var request = await deletion.LockRequestAsync(requestId, input.UserId, ct);
        if (request is null)
            return new(DeletionOperationStatus.NotFound);
        if (!await HasVerificationAsync(requestId, ct))
            return new(DeletionOperationStatus.Conflict);

        var prior = await FindEvidenceAsync(requestId, AppDeviceTokens, input.EvidenceReference!, ct);
        if (prior is not null)
            return new(DeletionOperationStatus.Ok, ToReceipt(prior));

        var removed = await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM app.device_tokens WHERE user_id = {request.UserId}", ct);
        var row = NewEvidence(requestId, actorId, AppDeviceTokens, removed == 0 ? Absent : Deleted,
            input.EvidenceReference!, AtPostgresPrecision(DateTime.UtcNow));
        row.RowsErased = removed;
        db.PrivacyDispositionEvidence.Add(row);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(DeletionOperationStatus.Ok, ToReceipt(row));
    }

    public async Task<DeletionOperation<PrivacySourceReceipt>> RecordAsync(
        Guid requestId, Guid actorId, string source, RecordPrivacyDisposition input, CancellationToken ct)
    {
        if (!Sources.TryGetValue(source, out var kind) || kind == SourceKind.Internal ||
            input.UserId == Guid.Empty || !ValidEvidence(kind, input))
            return new(DeletionOperationStatus.Invalid);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await deletion.LockRequestAsync(requestId, input.UserId, ct) is null)
            return new(DeletionOperationStatus.NotFound);
        if (!await HasVerificationAsync(requestId, ct))
            return new(DeletionOperationStatus.Conflict);

        var reviewAt = input.ReviewAt is { } review ? AtPostgresPrecision(review.UtcDateTime) : (DateTime?)null;
        var prior = await FindEvidenceAsync(requestId, source, input.EvidenceReference!, ct);
        if (prior is not null)
            return prior.Outcome == input.Outcome && prior.RemainingCount == input.RemainingCount &&
                prior.ActionReference == input.ActionReference && prior.PolicyReference == input.PolicyReference &&
                prior.ArchiveReference == input.ArchiveReference && prior.RestoreReference == input.RestoreReference &&
                prior.ReviewAt == reviewAt
                ? new(DeletionOperationStatus.Ok, ToReceipt(prior))
                : new(DeletionOperationStatus.Conflict);

        var now = AtPostgresPrecision(DateTime.UtcNow);
        if (input.Outcome == Retained && reviewAt <= now)
            return new(DeletionOperationStatus.Invalid);

        var row = NewEvidence(requestId, actorId, source, input.Outcome!, input.EvidenceReference!, now);
        row.RemainingCount = input.RemainingCount!.Value;
        row.ActionReference = input.ActionReference;
        row.PolicyReference = input.PolicyReference;
        row.ArchiveReference = input.ArchiveReference;
        row.RestoreReference = input.RestoreReference;
        row.ReviewAt = reviewAt;
        db.PrivacyDispositionEvidence.Add(row);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(DeletionOperationStatus.Ok, ToReceipt(row));
    }

    public async Task<DeletionOperation<PrivacyDeletionCompletion>> CompleteAsync(
        Guid requestId, Guid actorId, CompletePrivacyDeletion input, CancellationToken ct)
    {
        if (input.UserId == Guid.Empty || !IsReference(input.InventoryReference) || !IsReference(input.RestoreReference))
            return new(DeletionOperationStatus.Invalid);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var request = await deletion.LockRequestAsync(requestId, input.UserId, ct, includeClosed: true);
        if (request is null)
            return new(DeletionOperationStatus.NotFound);
        var review = await db.PrivacyDeletionCases.SingleOrDefaultAsync(x => x.RequestId == requestId, ct);
        if (review is null)
            return new(DeletionOperationStatus.Conflict);
        if (request.ClosedAt is { } closedAt)
            return review.InventoryReference == input.InventoryReference && review.RestoreReference == input.RestoreReference &&
                review.CompletionSourceCount is { } count
                ? new(DeletionOperationStatus.Ok, new(closedAt, count, []))
                : new(DeletionOperationStatus.Conflict);

        var now = AtPostgresPrecision(DateTime.UtcNow);
        var latest = await db.PrivacyDispositionEvidence
            .Where(x => x.RequestId == requestId)
            .GroupBy(x => x.SourceKey)
            .Select(group => group.OrderByDescending(x => x.RecordedAt).ThenByDescending(x => x.Id).First())
            .ToListAsync(ct);
        var verified = latest.Where(x => x.Outcome != Retained || x.ReviewAt > now)
            .Select(x => x.SourceKey).ToHashSet();
        if (review.PreferencesErasedAt is not null &&
            !await db.UserPreferences.AnyAsync(x => x.UserId == request.UserId, ct))
            verified.Add(AuthPreferences);
        if (verified.Contains(AppDeviceTokens))
        {
            var reappeared = await db.Database.SqlQuery<bool>(
                $"SELECT EXISTS (SELECT 1 FROM app.device_tokens WHERE user_id = {request.UserId}) AS \"Value\"")
                .SingleAsync(ct);
            if (reappeared)
                verified.Remove(AppDeviceTokens);
        }

        var missing = Sources.Keys.Where(key => !verified.Contains(key)).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
            return new(DeletionOperationStatus.Conflict, new(null, Sources.Count - missing.Length, missing));

        // El informe de restauración se registra por separado de la expiración
        // o disposición de cada copia. No se afirma que la app validó el artefacto.
        review.InventoryReference = input.InventoryReference;
        review.RestoreReference = input.RestoreReference;
        review.CompletionSourceCount = Sources.Count;
        review.ClosedBy = actorId;
        await db.PrivacyRequests.Where(x => x.Id == requestId && x.ClosedAt == null)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.ClosedAt, now), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(DeletionOperationStatus.Ok, new(now, Sources.Count, []));
    }

    private Task<bool> HasVerificationAsync(Guid requestId, CancellationToken ct) =>
        db.PrivacyDeletionCases.AnyAsync(x => x.RequestId == requestId, ct);

    private Task<PrivacyDispositionEvidence?> FindEvidenceAsync(
        Guid requestId, string source, string evidence, CancellationToken ct) =>
        db.PrivacyDispositionEvidence.AsNoTracking().SingleOrDefaultAsync(
            x => x.RequestId == requestId && x.SourceKey == source && x.EvidenceReference == evidence, ct);

    private static bool ValidEvidence(SourceKind kind, RecordPrivacyDisposition input)
    {
        if (!IsReference(input.EvidenceReference) || input.RemainingCount is null or < 0 ||
            !OptionalReference(input.ActionReference) || !OptionalReference(input.PolicyReference) ||
            !OptionalReference(input.ArchiveReference) || !OptionalReference(input.RestoreReference))
            return false;

        if (input.Outcome == Absent)
            return input.RemainingCount == 0 && input.ReviewAt is null;
        if (kind == SourceKind.Archive && (input.ArchiveReference is null || input.RestoreReference is null))
            return false;
        return input.Outcome switch
        {
            Retained => input.RemainingCount > 0 && input.PolicyReference is not null &&
                input.ReviewAt is not null,
            Deleted => kind != SourceKind.Backup && input.RemainingCount == 0 && input.ReviewAt is null &&
                input.ActionReference is not null && (kind != SourceKind.Protected || input.PolicyReference is not null),
            Expired => kind == SourceKind.Backup && input.RemainingCount == 0 && input.ReviewAt is null &&
                input.ActionReference is not null && input.RestoreReference is not null,
            _ => false
        };
    }

    private static bool OptionalReference(string? value) => value is null || IsReference(value);

    private static PrivacyDispositionEvidence NewEvidence(
        Guid requestId, Guid actorId, string source, string outcome, string reference, DateTime now) => new()
    {
        Id = Guid.CreateVersion7(),
        RequestId = requestId,
        RecordedBy = actorId,
        SourceKey = source,
        Outcome = outcome,
        EvidenceReference = reference,
        RecordedAt = now
    };

    private static PrivacySourceReceipt ToReceipt(PrivacyDispositionEvidence row) =>
        new(row.SourceKey, row.Outcome, row.RecordedAt, row.RowsErased);

    private enum SourceKind { Internal, Data, Protected, Provider, Archive, Backup }
}
