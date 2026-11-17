using CoppAddresd.Application.Interfaces;
using MediatR;

namespace CoppAddresd.Application.Features.Audit;

/// <summary>Consulta global de solo lectura protegida por Audit.View en la API.</summary>
public sealed record ListAuditActivityLogQuery(
    int Page = 1,
    int PageSize = 20,
    string? Schema = null,
    string? Table = null,
    string? Action = null,
    string? Actor = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Search = null) : IRequest<PaginatedAuditActivityLogResult>;

/// <summary>Normaliza los filtros y delega la lectura sin cargar payloads de auditoría.</summary>
public sealed class ListAuditActivityLogQueryHandler(IAuditLogRepository repository)
    : IRequestHandler<ListAuditActivityLogQuery, PaginatedAuditActivityLogResult>
{
    public Task<PaginatedAuditActivityLogResult> Handle(ListAuditActivityLogQuery request, CancellationToken ct)
        => repository.ListAsync(request with
        {
            Schema = Clean(request.Schema),
            Table = Clean(request.Table),
            Action = Clean(request.Action)?.ToUpperInvariant(),
            Actor = Clean(request.Actor),
            From = request.From?.ToUniversalTime(),
            To = request.To?.ToUniversalTime(),
            Search = Clean(request.Search),
        }, ct);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
