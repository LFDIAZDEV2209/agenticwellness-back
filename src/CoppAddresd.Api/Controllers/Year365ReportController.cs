using CoppAddresd.Application.Features.Wellness.Year365;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Api.Constants;
using CoppAddresd.Api.Http;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoppAddresd.Api.Controllers;

[ApiController]
[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/v1/program/erp/patients/{patientId:guid}/year-365")]
public sealed class Year365ReportController(IMediator mediator, IPatientScopeGuard scope) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<Year365ReportResponse>> Get(
        Guid patientId, [FromQuery] string? month, CancellationToken ct)
    {
        if (!IsErpSession())
            return Forbid();

        return Ok(await mediator.Send(new GetYear365ReportQuery(patientId, month), ct));
    }

    [HttpGet("export/{format}")]
    public async Task<IActionResult> Export(
        Guid patientId, string format, [FromQuery] string month, [FromQuery] string? lang, CancellationToken ct)
    {
        if (!IsErpSession())
            return Forbid();
        await scope.EnsureAsync(patientId, PermissionCodes.ProgramExport, ct);
        if (format is not ("csv" or "pdf") || lang is not (null or "es" or "en"))
            return BadRequest();

        var response = await mediator.Send(new GetYear365ReportQuery(patientId, month), ct);
        if (response.Report is not { } report)
            return NotFound();

        var file = new Year365ReportExport(report, lang == "en");
        var name = $"year-365-{report.Month}.{format}";
        return format == "csv"
            ? File(file.ToCsv(), "text/csv; charset=utf-8", name)
            : File(file.ToPdf(), "application/pdf", name);
    }

    private bool IsErpSession() => User.HasClaim("aud", "erp") && !User.HasClaim("aud", "app");
}
