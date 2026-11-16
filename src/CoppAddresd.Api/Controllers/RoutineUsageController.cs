using CoppAddresd.Api.Authorization;
using CoppAddresd.Api.Constants;
using CoppAddresd.Application.Features.ProgramProgress.Queries.GetRoutineUsage;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoppAddresd.Api.Controllers;

/// <summary>Conteo read-only del uso efectivo de una rutina en semanas activas; nunca retorna identidades de pacientes.</summary>
[ApiController]
[Authorize]
[Route("api/v1/program/routines")]
public sealed class RoutineUsageController(IMediator mediator) : ControllerBase
{
    /// <summary>Pacientes, inscripciones y semanas activas que usan la rutina, dentro del alcance del actor.</summary>
    [HttpGet("{id:guid}/usage")]
    [RequireContextPermission(PermissionCodes.WellnessView)]
    [ProducesResponseType<RoutineUsageDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RoutineUsageDto>> GetUsage(Guid id, CancellationToken ct)
    {
        var result = await mediator.Send(new GetRoutineUsageQuery(id), ct);
        return result is null ? NotFound(new { message = "Rutina de ejercicio no encontrada" }) : Ok(result);
    }
}
