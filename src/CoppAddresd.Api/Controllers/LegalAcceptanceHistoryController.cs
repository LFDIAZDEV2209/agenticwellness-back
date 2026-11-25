using System.ComponentModel.DataAnnotations;
using System.Globalization;
using CoppAddresd.Api.Constants;
using CoppAddresd.Api.Context;
using CoppAddresd.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoppAddresd.Api.Controllers;

[ApiController]
[Route("api/v1/legal-acceptances")]
[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class LegalAcceptanceHistoryController(LegalAcceptanceHistory history, ICurrentContext current) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<LegalAcceptanceHistoryPage>> List([FromQuery] LegalAcceptanceHistoryQuery query,
        [FromQuery, Range(1, int.MaxValue)] int page = 1, [FromQuery, Range(1, 100)] int pageSize = 25,
        CancellationToken ct = default)
    {
        if (!User.HasClaim("aud", "erp") || current.UserId is null) return Forbid();
        if (query.InvalidRange) return BadRequest(new { code = "LEGAL_HISTORY_INVALID_RANGE" });
        if (!query.TryGetFilter(out var filter)) return BadRequest(new { code = "LEGAL_HISTORY_INVALID_VERSION" });
        return Ok(await history.ListAsync(filter, page, pageSize, current.ActiveClinicId,
            current.ActiveOrganizationId, PermissionCodes.LegalAcceptancesView, ct));
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] LegalAcceptanceHistoryQuery query, CancellationToken ct)
    {
        if (!User.HasClaim("aud", "erp") || current.UserId is not { } userId) return Forbid();
        if (query.InvalidRange) return BadRequest(new { code = "LEGAL_HISTORY_INVALID_RANGE" });
        if (!query.TryGetFilter(out var filter)) return BadRequest(new { code = "LEGAL_HISTORY_INVALID_VERSION" });
        var csv = await history.ExportAsync(filter, userId, current.ActiveClinicId,
            current.ActiveOrganizationId, PermissionCodes.LegalAcceptancesExport, ct);
        return csv is null
            ? UnprocessableEntity(new { code = "LEGAL_HISTORY_EXPORT_LIMIT" })
            : File(csv, "text/csv; charset=utf-8", "legal-acceptances.csv");
    }
}

public sealed class LegalAcceptanceHistoryQuery
{
    public Guid? AccountId { get; init; }
    [StringLength(100)] public string? DocumentCode { get; init; }
    [StringLength(21)] public string? Version { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? Before { get; init; }

    internal bool InvalidRange => From is { } from && Before is { } before && from >= before;

    internal bool TryGetFilter(out LegalAcceptanceFilter filter)
    {
        filter = new(AccountId, DocumentCode, null, From, Before);
        if (Version is null) return true;
        var parts = Version.Split('.');
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)) return false;
        filter = filter with { Version = (major, minor) };
        return true;
    }
}
