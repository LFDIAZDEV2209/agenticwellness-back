using CoppAddresd.Application.Features.Audit;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

/// <summary>Consulta metadatos de audit.activity_logs sin materializar los documentos JSON.</summary>
public sealed class AuditLogRepository(AppDbContext db) : IAuditLogRepository
{
    public async Task<PaginatedAuditActivityLogResult> ListAsync(ListAuditActivityLogQuery request, CancellationToken ct)
    {
        var query = db.ActivityLogs.AsNoTracking();
        if (request.Schema is { } schema) query = query.Where(x => x.SchemaName == schema);
        if (request.Table is { } table) query = query.Where(x => x.TableName == table);
        // El trigger escribe INSERT/UPDATE/DELETE y hay eventos históricos en PascalCase.
        if (request.Action is { } action) query = query.Where(x => EF.Functions.ILike(x.Action.ToString(), action));
        if (request.From is { } from) query = query.Where(x => x.OccurredAt >= from);
        if (request.To is { } to) query = query.Where(x => x.OccurredAt <= to);
        if (request.Actor is { } actor)
        {
            var normalized = actor.ToLowerInvariant();
            query = query.Where(x => x.UserEmail != null && x.UserEmail.ToLower().Contains(normalized));
        }
        if (request.Search is { } search)
            query = query.Where(x => x.RecordId.Contains(search)
                || (x.CorrelationId != null && x.CorrelationId.Contains(search)));

        var total = await query.CountAsync(ct);
        // Evita desbordamiento de int para páginas arbitrariamente grandes: esas páginas están vacías.
        var offset = ((long)request.Page - 1) * request.PageSize;
        if (offset >= total)
            return new([], total, request.Page, request.PageSize, (int)Math.Ceiling(total / (double)request.PageSize));

        var items = await query.OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id)
            .Skip((int)offset).Take(request.PageSize)
            .Select(x => new AuditActivityLogDto(x.Id, x.OccurredAt, x.Action.ToString().ToUpperInvariant(),
                x.SchemaName, x.TableName, x.RecordId, x.ActorType.ToString().ToUpperInvariant(),
                x.UserEmail, x.UserRole, x.IpAddress, x.CorrelationId))
            .ToListAsync(ct);

        return new(items, total, request.Page, request.PageSize, (int)Math.Ceiling(total / (double)request.PageSize));
    }
}
