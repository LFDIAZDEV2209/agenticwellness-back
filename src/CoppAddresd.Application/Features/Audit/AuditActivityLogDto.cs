namespace CoppAddresd.Application.Features.Audit;

/// <summary>Metadatos públicos de auditoría; excluye todos los payloads de las filas.</summary>
public sealed record AuditActivityLogDto(
    Guid Id,
    DateTimeOffset OccurredAt,
    string Action,
    string SchemaName,
    string TableName,
    string RecordId,
    string ActorType,
    string? UserEmail,
    string? UserRole,
    string? IpAddress,
    string? CorrelationId);

/// <summary>Página de auditoría general con total filtrado.</summary>
public sealed record PaginatedAuditActivityLogResult(
    IReadOnlyList<AuditActivityLogDto> Items,
    int Total,
    int Page,
    int PageSize,
    int TotalPages);
