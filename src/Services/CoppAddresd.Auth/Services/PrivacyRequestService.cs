using CoppAddresd.Auth.Constants;
using CoppAddresd.Auth.Data;
using CoppAddresd.Auth.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Auth.Services;

public sealed record SubmitPrivacyRequest(string? Kind, string? Format, bool Confirmed = false);
public sealed record PrivacyReceipt(Guid Id, string Kind, string? Format, DateTime ReceivedAt, PrivacyDeletionReview? Review = null, DateTime? CompletedAt = null);
public sealed record PendingPrivacyRequests(PrivacyReceipt? Export, PrivacyReceipt? Deletion, bool DownloadAvailable = false, ReadyPrivacyExport? FullExport = null);

public sealed class PrivacyRequestService(AuthDbContext db)
{
    public const string Export = "export";
    public const string Deletion = "deletion";

    // Los tokens de App son válidos hasta expirar incluso si se retira el acceso.
    // Para este canal comprobamos además el acceso vigente, no solo el JWT.
    public Task<bool> HasActiveAccessAsync(Guid userId, CancellationToken ct) =>
        db.UserApplications.AnyAsync(x =>
            x.UserId == userId && x.User.IsActive && x.User.DeletionRequestedAt == null &&
            !x.IsSuspended && x.Application.IsActive && x.Application.Code == ApplicationCodes.App, ct);

    public async Task<PendingPrivacyRequests> ListPendingAsync(Guid userId, CancellationToken ct)
    {
        // El índice único parcial limita la lista a una fila por tipo.
        var receipts = await Project(db.PrivacyRequests
            .Where(x => x.UserId == userId && x.ClosedAt == null)).ToListAsync(ct);
        return new(receipts.SingleOrDefault(x => x.Kind == Export),
            receipts.SingleOrDefault(x => x.Kind == Deletion));
    }

    public Task<PrivacyReceipt?> FindAsync(Guid userId, Guid requestId, CancellationToken ct) =>
        Project(db.PrivacyRequests.Where(x => x.UserId == userId && x.Id == requestId))
            .SingleOrDefaultAsync(ct);

    public Task<int> RecordPreparationAsync(Guid userId, Guid requestId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return db.PrivacyRequests.Where(x => x.UserId == userId && x.Id == requestId &&
                x.Kind == Export && x.ClosedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExportPreparedAt, now), ct);
    }

    public async Task<(PrivacyReceipt Receipt, bool Created)?> SubmitAsync(Guid userId,
        SubmitPrivacyRequest request, CancellationToken ct)
    {
        if (request is not ({ Kind: Export, Format: "json" or "csv", Confirmed: false }
            or { Kind: Deletion, Format: null, Confirmed: true }))
            return null;

        var id = Guid.NewGuid();
        var receivedAt = DateTime.UtcNow;
        var kind = request.Kind!;
        var format = request.Format;

        // Conflicto solo con otra solicitud pendiente del mismo tipo. Leer en otra
        // sentencia asegura una nueva instantánea después de un INSERT concurrente.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO auth."PrivacyRequests" ("Id", "UserId", "Kind", "Format", "ReceivedAt")
                VALUES ({id}, {userId}, {kind}, {format}, {receivedAt})
                ON CONFLICT ("UserId", "Kind") WHERE "ClosedAt" IS NULL DO NOTHING
                """, ct);
            if (inserted == 1)
                return ((await FindAsync(userId, id, ct))!, true);

            var pending = await Project(db.PrivacyRequests.Where(
                x => x.UserId == userId && x.Kind == kind && x.ClosedAt == null)).SingleOrDefaultAsync(ct);
            if (pending is not null)
                return (pending, false);
        }

        throw new InvalidOperationException("No se pudo recuperar la solicitud pendiente.");
    }

    private static IQueryable<PrivacyReceipt> Project(IQueryable<PrivacyRequest> query) =>
        query.Select(x => new PrivacyReceipt(x.Id, x.Kind, x.Format, x.ReceivedAt,
            x.DeletionCase == null ? null : new PrivacyDeletionReview(x.DeletionCase.VerifiedAt, x.DeletionCase.DueAt),
            x.Kind == Deletion ? x.ClosedAt : null));
}
