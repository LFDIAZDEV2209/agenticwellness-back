using System.Globalization;
using System.Text;
using CoppAddresd.Api.Authorization;
using CoppAddresd.Api.Context;
using CoppAddresd.Application.Features.Medications;
using CoppAddresd.Domain.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoppAddresd.Api.Controllers;

[ApiController, Authorize, Route("api/v1")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class MedicationMonitoringController(MedicationControlService policy, MedicationMonitoringService monitoring, ICurrentContext current) : ControllerBase
{
    private Guid Actor => current.UserId ?? throw new ForbiddenException("Se requiere una identidad válida.");
    private bool IsApp => User.HasClaim("aud", "app");
    private string Origin => IsApp ? "patient_reported" : "staff_recorded";
    private async Task<Guid> Self(CancellationToken ct) {
        if (!IsApp) throw new ForbiddenException("Se requiere acceso a la aplicación.");
        return await policy.EnsureSelfAsync(Actor, ct);
    }
    private async Task Staff(Guid? patientId, string permission, CancellationToken ct) {
        if (!User.HasClaim("aud", "erp")) throw new ForbiddenException("Se requiere acceso al ERP.");
        policy.EnsureEnabled();
        if (patientId is { } id) await policy.EnsureStaffAsync(id, permission, current.ActiveClinicId, current.ActiveOrganizationId, ct);
    }
    private Task Audit(IEnumerable<Guid> patients, string resource, CancellationToken ct, string action = "read", string? purpose = null)
        => monitoring.Store.AuditAccessAsync(patients, Actor, Origin, action, resource, purpose, ct);

    [HttpGet("medications/patients/{patientId:guid}/tracking"), RequireContextPermission("Medications.View")]
    public async Task<IActionResult> Week(Guid patientId, DateOnly? from, string timeZone = "America/Bogota", CancellationToken ct = default) {
        await Staff(patientId, "Medications.View", ct);
        var date = monitoring.Date(from, timeZone);
        var result = await monitoring.Store.WeekAsync(patientId, date, timeZone, ct);
        await Audit([patientId], "tracking", ct); return Ok(result);
    }
    [HttpGet("me/medications/inventory")]
    public async Task<IActionResult> SelfInventory(int page = 1, int pageSize = 100, CancellationToken ct = default) {
        var patient = await Self(ct); MedicationMonitoringService.Page(page, pageSize);
        var result = await monitoring.Store.InventoryAsync(patient, null, page, pageSize, null, null, true, ct);
        await Audit([patient], "inventory", ct); return Ok(result);
    }
    [HttpGet("medications/inventory"), RequireContextPermission("Medications.View")]
    public async Task<IActionResult> Inventory(Guid? patientId, string? query = null, int page = 1, int pageSize = 20, CancellationToken ct = default) {
        await Staff(patientId, "Medications.View", ct); MedicationMonitoringService.Page(page, pageSize);
        if ((query?.Length ?? 0) > 200) return BadRequest();
        var result = await monitoring.Store.InventoryAsync(patientId, query, page, pageSize, current.ActiveClinicId, current.ActiveOrganizationId, false, ct);
        await Audit(result.Items.Select(x => x.PatientId), "inventory", ct); return Ok(result);
    }
    [HttpGet("medications/patients/{patientId:guid}/inventory"), RequireContextPermission("Medications.View")]
    public Task<IActionResult> PatientInventory(Guid patientId, int page = 1, int pageSize = 100, CancellationToken ct = default)
        => Inventory(patientId, null, page, pageSize, ct);

    [HttpGet("me/medications/inventory/{treatmentId:guid}/movements")]
    public async Task<IActionResult> SelfMovements(Guid treatmentId, CancellationToken ct) {
        var patient = await Self(ct); var rows = await monitoring.Store.MovementsAsync(patient, treatmentId, ct);
        await Audit([patient], "inventory_movements", ct); return Ok(rows);
    }
    [HttpGet("medications/patients/{patientId:guid}/inventory/{treatmentId:guid}/movements"), RequireContextPermission("Medications.View")]
    public async Task<IActionResult> Movements(Guid patientId, Guid treatmentId, CancellationToken ct) {
        await Staff(patientId, "Medications.View", ct); var rows = await monitoring.Store.MovementsAsync(patientId, treatmentId, ct);
        await Audit([patientId], "inventory_movements", ct); return Ok(rows);
    }
    [HttpPost("me/medications/inventory/{treatmentId:guid}/movements")]
    public async Task<IActionResult> SelfStock(Guid treatmentId, MedicationStockRequest request, CancellationToken ct)
        => Ok(await monitoring.StockAsync(await Self(ct), Actor, Origin, treatmentId, request, ct));
    [HttpPost("medications/patients/{patientId:guid}/inventory/{treatmentId:guid}/movements"), RequireContextPermission("Medications.Manage")]
    public async Task<IActionResult> Stock(Guid patientId, Guid treatmentId, MedicationStockRequest request, CancellationToken ct) {
        await Staff(patientId, "Medications.Manage", ct); return Ok(await monitoring.StockAsync(patientId, Actor, Origin, treatmentId, request, ct));
    }
    [HttpGet("me/medications/effects")]
    public async Task<IActionResult> SelfEffects(CancellationToken ct) {
        var patient = await Self(ct); var result = await monitoring.Store.EffectsAsync(patient, ct);
        await Audit([patient], "effects", ct); return Ok(result);
    }
    [HttpPost("me/medications/effects")]
    public async Task<IActionResult> SelfReport(MedicationEffectRequest request, CancellationToken ct)
        => Ok(await monitoring.ReportAsync(await Self(ct), Actor, Origin, request, ct));
    [HttpGet("medications/patients/{patientId:guid}/effects"), RequireContextPermission("Medications.View")]
    public async Task<IActionResult> Effects(Guid patientId, CancellationToken ct) {
        await Staff(patientId, "Medications.View", ct); var result = await monitoring.Store.EffectsAsync(patientId, ct);
        await Audit([patientId], "effects", ct); return Ok(result);
    }
    [HttpPost("medications/patients/{patientId:guid}/effects"), RequireContextPermission("Medications.Manage")]
    public async Task<IActionResult> Report(Guid patientId, MedicationEffectRequest request, CancellationToken ct) {
        await Staff(patientId, "Medications.Manage", ct); return Ok(await monitoring.ReportAsync(patientId, Actor, Origin, request, ct));
    }
    [HttpPost("medications/patients/{patientId:guid}/effects/{reportId:guid}/follow-ups"), RequireContextPermission("Medications.Manage")]
    public async Task<IActionResult> Review(Guid patientId, Guid reportId, MedicationFollowUpRequest request, CancellationToken ct) {
        await Staff(patientId, "Medications.Manage", ct); return Ok(await monitoring.ReviewAsync(patientId, Actor, reportId, request, ct));
    }
    [HttpGet("medications/alerts"), RequireContextPermission("Medications.View")]
    public async Task<IActionResult> Alerts(Guid? patientId, string? status = null, string? kind = null, int page = 1, int pageSize = 20, CancellationToken ct = default) {
        await Staff(patientId, "Medications.View", ct); MedicationMonitoringService.Page(page, pageSize);
        if (status is not (null or "" or "open" or "in_progress" or "resolved") || kind is not (null or "" or "stock_low" or "expired" or "unrecorded" or "low_adherence" or "reported_effect")) return BadRequest();
        var result = await monitoring.Store.AlertsAsync(patientId, status, kind, page, pageSize, current.ActiveClinicId, current.ActiveOrganizationId, ct);
        await Audit(result.Items.Select(x => x.PatientId), "alerts", ct); return Ok(result);
    }
    [HttpPost("medications/patients/{patientId:guid}/alerts/{alertId:guid}/follow-ups"), RequireContextPermission("Medications.Manage")]
    public async Task<IActionResult> FollowAlert(Guid patientId, Guid alertId, MedicationFollowUpRequest request, CancellationToken ct) {
        await Staff(patientId, "Medications.Manage", ct); return Ok(await monitoring.AlertAsync(patientId, Actor, alertId, request, ct));
    }
    [HttpGet("medications/audit"), RequireContextPermission("Medications.View"), RequireContextPermission("Audit.View")]
    public async Task<IActionResult> AuditLog(Guid? patientId, string? action, DateTimeOffset? from, DateTimeOffset? to, Guid? actorId,
        int page = 1, int pageSize = 30, CancellationToken ct = default) {
        await Staff(patientId, "Medications.View", ct); MedicationMonitoringService.Page(page, pageSize);
        if ((action?.Length ?? 0) > 40 || to < from) return BadRequest();
        return Ok(await monitoring.Store.AuditAsync(new(patientId, action, from, to, actorId), page, pageSize, current.ActiveClinicId, current.ActiveOrganizationId, ct));
    }
    [HttpGet("medications/patients/{patientId:guid}/export"), RequireContextPermission("Medications.Export")]
    public async Task<IActionResult> Export(Guid patientId, DateOnly? from, string timeZone = "America/Bogota", string purpose = "Seguimiento clínico", CancellationToken ct = default) {
        await Staff(patientId, "Medications.View", ct);
        if (string.IsNullOrWhiteSpace(purpose) || purpose.Trim().Length > 200) return BadRequest();
        var date = monitoring.Date(from, timeZone);
        var week = await monitoring.Store.WeekAsync(patientId, date, timeZone, ct);
        static string Csv(string? value) {
            value ??= "";
            if (value.Length > 0 && "=+-@\t\r\n".Contains(value[0])) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        var csv = new StringBuilder("Paciente,Medicamento,Cantidad,Unidad,Programada,Estado,Registrada,Plan asignado\r\n");
        foreach (var item in week.Doses) {
            var d = item.Dose;
            csv.AppendLine(string.Join(',', new[] {week.Patient.Name, d.Name, d.Dosage.ToString(CultureInfo.InvariantCulture), d.Unit,
                d.ScheduledAt.ToString("O"), d.Status, d.OccurredAt?.ToString("O"), item.IsRequired ? "Sí" : "No"}.Select(Csv)));
        }
        await Audit([patientId], "tracking", ct, "export", purpose.Trim());
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray(), "text/csv; charset=utf-8", $"medicamentos-{date:yyyyMMdd}.csv");
    }
}
