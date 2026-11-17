using CoppAddresd.Application.Features.Audit;

namespace CoppAddresd.Application.Interfaces;

/// <summary>Lectura paginada de metadatos de auditoría, sin restricciones de módulo.</summary>
public interface IAuditLogRepository
{
    Task<PaginatedAuditActivityLogResult> ListAsync(ListAuditActivityLogQuery query, CancellationToken ct);
}
