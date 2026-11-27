using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using CoppAddresd.Api.Constants;
using CoppAddresd.Application.Features.Motor365;
using CoppAddresd.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoppAddresd.Api.Controllers;

[ApiController, Authorize]
[Route("api/v1/program/erp/patients/{patientId:guid}/motor-timeline")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class Motor365TimelineController(IPatientScopeGuard scope, IPatientScopeActor actor,
    IMotorTimelineStore timeline, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<MotorTimelinePage>> Get(Guid patientId, [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to, [FromQuery] string? kind, CancellationToken ct,
        [FromQuery, Range(1, 30)] int page = 1)
    {
        if (!IsErpSession()) return Forbid();
        await scope.EnsureAsync(patientId, PermissionCodes.ProgramProfile360View, ct);
        var filter = new MotorTimelineFilter(from, to, kind);
        if (!filter.IsValid) return BadRequest();
        return Ok(await timeline.ReadAsync(patientId, filter, page, ct));
    }

    [HttpPost("export")]
    public async Task<IActionResult> Export(Guid patientId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] string? kind, [FromQuery] string? lang, CancellationToken ct)
    {
        if (!IsErpSession() || actor.UserId is not { } actorId) return Forbid();
        await scope.EnsureAsync(patientId, PermissionCodes.ProgramProfile360View, ct);
        await scope.EnsureAsync(patientId, PermissionCodes.ProgramExport, ct);
        var filter = new MotorTimelineFilter(from, to, kind);
        if (!filter.IsValid || lang is not (null or "es" or "en")) return BadRequest();
        var events = await timeline.ReadForExportAsync(patientId, filter, ct);
        if (events.Count == 0) return NotFound();
        var file = Csv(events, lang == "en");
        await timeline.AuditExportAsync(patientId, actorId, clock.GetUtcNow(), ct);
        return File(file, "text/csv; charset=utf-8", "motor-365-events.csv");
    }

    private bool IsErpSession() => User.HasClaim("aud", "erp") && !User.HasClaim("aud", "app");

    private static byte[] Csv(IReadOnlyList<MotorTimelineEvent> events, bool english)
    {
        var table = new StringBuilder("\uFEFF");
        Row(english
            ? ["Event ID", "Recorded at (UTC)", "Day", "Type", "Plan version", "Settings version", "Engine version", "Change (kcal)", "Rate (%)", "Explanation"]
            : ["ID de evento", "Registro (UTC)", "Día", "Tipo", "Versión del plan", "Versión de parámetros", "Versión del motor", "Cambio (kcal)", "Tasa (%)", "Explicación"]);
        foreach (var item in events)
            Row([item.Id.ToString(), item.OccurredAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                Number(item.Day), item.Kind, Number(item.PlanVersion), Number(item.ParametersVersion), item.EngineVersion,
                Number(item.CalorieDelta), Number(item.RatePercent), english ? item.MessageEn : item.MessageEs], protect: true);
        return Encoding.UTF8.GetBytes(table.ToString());

        void Row(string[] cells, bool protect = false)
        {
            for (var index = 0; index < cells.Length; index++)
            {
                if (index > 0) table.Append(',');
                var cell = cells[index];
                // No convertir valores numéricos negativos en texto. Las columnas de texto
                // sí se neutralizan para hojas de cálculo antes de aplicar las comillas CSV.
                if (protect && index is 3 or 6 or 9 && cell.TrimStart() is { Length: > 0 } trimmed && trimmed[0] is '=' or '+' or '-' or '@')
                    cell = "'" + cell;
                table.Append('"').Append(cell.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
            }
            table.Append("\r\n");
        }

        static string Number(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";
    }
}
