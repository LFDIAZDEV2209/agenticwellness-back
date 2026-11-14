using CoppAddresd.Api.Context;
using CoppAddresd.Api.Localization;
using CoppAddresd.Application.Features.Nutrition.Queries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoppAddresd.Api.Controllers;

/// <summary>Autoservicio móvil; el guard resuelve exclusivamente al paciente del JWT.</summary>
[ApiController, Authorize, Route("api/v1/me/nutrition/cycles/recommendations")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class MyNutritionRecommendationsController(IMediator mediator, ICurrentContext current) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<NutritionRecommendationsDto>> Get(CancellationToken ct)
    {
        if (!User.HasClaim("aud", "app")) return Forbid();
        if (current.UserId is not { } userId) return Unauthorized();
        var language = PatientContentLocalizationFilter.ResolvePatientLanguage(HttpContext);
        Response.Headers.ContentLanguage = language;
        PatientContentLocalizationFilter.AddVaryHeaders(Response);
        return Ok(await mediator.Send(new GetMyNutritionRecommendations(userId, language), ct));
    }
}
