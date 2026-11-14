using CoppAddresd.Api.Authorization;
using CoppAddresd.Api.Constants;
using CoppAddresd.Application.Features.Nutrition.Queries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoppAddresd.Api.Controllers;

/// <summary>Avisos de cierre del profesional autenticado en la bandeja de Nutrición del ERP.</summary>
[ApiController, Authorize(Policy = NutritionPolicies.Staff), Route("api/v1/nutrition/cycle-closing-notices")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class NutritionClosingNoticesController(IMediator mediator) : ControllerBase
{
    [HttpGet, RequireContextPermission(PermissionCodes.NutritionView)]
    public async Task<ActionResult<CycleClosingNoticePage>> List(CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        => Ok(await mediator.Send(new GetCycleClosingNotices(page, pageSize), ct));
}
