using CoppAddresd.Api.Authorization;
using CoppAddresd.Api.Constants;
using CoppAddresd.Application.Features.Media.PodcastMetadata;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoppAddresd.Api.Controllers;

/// <summary>Ayuda editorial efímera al cargar podcasts; guardar/publicar sigue requiriendo acciones humanas separadas.</summary>
[ApiController]
[Route("api/v1/media/podcast-metadata")]
[Authorize(Policy = NutritionPolicies.Staff)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PodcastMetadataController(IMediator mediator) : ControllerBase
{
    [HttpPost]
    [EndpointSummary("Sugerir descripción, categoría y etiquetas editables sin guardar ni publicar")]
    [RequireContextPermission(PermissionCodes.MediaCreate)]
    [RequireContextPermission("Program.AiManage")]
    [RequestSizeLimit(131072)]
    public async Task<ActionResult<PodcastMetadataResult>> Suggest([FromBody] PodcastMetadataInput input, CancellationToken ct)
        => Ok(await mediator.Send(new SuggestPodcastMetadataCommand(input), ct));
}
