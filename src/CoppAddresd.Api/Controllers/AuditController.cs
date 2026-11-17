using CoppAddresd.Api.Authorization;
using CoppAddresd.Api.Constants;
using CoppAddresd.Application.Features.Audit;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoppAddresd.Api.Controllers;

/// <summary>Consulta general de auditoría del ERP; solo expone metadatos.</summary>
[ApiController]
[Route("api/v1/audit")]
[Authorize]
public sealed class AuditController(IMediator mediator) : ControllerBase
{
    /// <summary>Lista actividad de todos los esquemas con paginación y filtros opcionales.</summary>
    [HttpGet("activity-log")]
    [RequirePermission(PermissionCodes.AuditView)]
    public async Task<ActionResult<PaginatedAuditActivityLogResult>> GetActivityLog(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? schema = null,
        [FromQuery] string? table = null,
        [FromQuery] string? action = null,
        [FromQuery] string? actor = null,
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery] string? search = null,
        CancellationToken ct = default)
        => Ok(await mediator.Send(new ListAuditActivityLogQuery(page, pageSize, schema, table,
            action, actor, from, to, search), ct));
}
