using CoppAddresd.Api.Authorization;
using CoppAddresd.Api.Context;
using CoppAddresd.Application.Features.Medications;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoppAddresd.Api.Controllers;

[ApiController]
[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/v1")]
public sealed partial class MedicationControlController(MedicationControlService medications, ICurrentContext current, MedicationMonitoringService? monitoring = null, IConfiguration? settings = null) : ControllerBase
{
    [HttpGet("me/capabilities")]
    public async Task<IActionResult> Capabilities(CancellationToken ct)
    {
        var enabled = false;
        var view = false;
        var manage = false;
        var manageAccess = false;
        var canViewAudit = false;
        var canExport = false;
        var canSnooze = false;
        var revision = 0;
        if (medications.Enabled && IsApplication("app"))
        {
            var access = await medications.SelfAccessAsync(ActorId, ct);
            enabled = view = manage = access.Enabled;
            revision = access.Revision;
            canSnooze = access.Enabled && MedicationReminderTestScope.AppEnabledForUser(settings, ActorId);
        }
        else if (medications.Enabled && IsApplication("erp"))
        {
            enabled = view = await current.HasPermissionAsync("Medications.View", ct);
            manage = view && await current.HasPermissionAsync("Medications.Manage", ct);
            manageAccess = view && await current.HasPermissionAsync("Medications.ManageAccess", ct);
            canViewAudit = view && await current.HasPermissionAsync("Audit.View", ct);
            canExport = view && await current.HasPermissionAsync("Medications.Export", ct);
        }
        Response.Headers.CacheControl = "no-store";
        return Ok(new
        {
            policyVersion = $"medications-v1:{(medications.Enabled ? revision.ToString() : "off")}",
            modules = new { medications = new { enabled, canView = view, canManage = manage, canManageAccess = manageAccess, canViewAudit, canExport, canSnooze } },
        });
    }

    [HttpGet("me/medications")]
    public async Task<ActionResult<MedicationWorkspaceDto>> SelfWorkspace(DateOnly? date, string? timeZone, CancellationToken ct)
    {
        var patient = await SelfAsync(ct);
        var result = await medications.WorkspaceAsync(patient, date, timeZone, true, ct);
        if (monitoring is not null) await monitoring.Store.AuditAccessAsync([patient], ActorId, "patient_reported", "read", "workspace", null, ct);
        return Ok(result);
    }

    [HttpGet("me/medications/version")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<Guid>> SelfVersion(CancellationToken ct)
        => Ok(await medications.VersionAsync(await SelfAsync(ct), ct));

    [HttpGet("me/medications/catalog/us")]
    public async Task<ActionResult<IReadOnlyList<RxTermsOption>>> SearchSelfUsCatalog(
        string? search, [FromServices] IRxTermsCatalog catalog, CancellationToken ct)
    {
        await SelfAsync(ct);
        return Ok(await catalog.SearchAsync(search, ct));
    }

    [HttpGet("me/medications/reminder-preferences")]
    public async Task<ActionResult<MedicationReminderPreferences>> SelfReminderPreferences(CancellationToken ct)
        => Ok(await medications.GetReminderPreferencesAsync(await SelfAsync(ct), ct));

    [HttpPut("me/medications/reminder-preferences")]
    public async Task<ActionResult<MedicationReminderPreferences>> UpdateSelfReminderPreferences(
        MedicationReminderPreferences request, CancellationToken ct)
        => Ok(await medications.SetReminderPreferencesAsync(await SelfAsync(ct), ActorId, request, ct));

    [HttpPost("me/medications/treatments")]
    public async Task<ActionResult<MedicationTreatmentDto>> SelfCreate(CreateMedicationTreatmentRequest request, CancellationToken ct)
    {
        var patient = await SelfAsync(ct);
        var result = await medications.CreateAsync(patient, ActorId, "patient_reported", request, ct);
        return StatusCode(result.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK, result.Treatment);
    }

    [HttpPost("me/medications/doses/{doseId:guid}/events")]
    public async Task<ActionResult<MedicationDoseDto>> SelfRecord(Guid doseId, RecordMedicationDoseRequest request, CancellationToken ct)
    {
        var patient = await SelfAsync(ct);
        return Ok(await medications.RecordAsync(patient, ActorId, "patient_reported", doseId, request, ct));
    }

    [HttpPost("me/medications/doses/{doseId:guid}/snooze")]
    public async Task<IActionResult> Snooze(Guid doseId, SnoozeMedicationDoseRequest request,
        [FromServices] IMedicationReminderScheduler reminders, [FromServices] IConfiguration configuration,
        CancellationToken ct)
    {
        var patient = await SelfAsync(ct);
        if (!MedicationReminderTestScope.AppEnabledForUser(configuration, ActorId)
            || (MedicationReminderTestScope.IsDemoOnly(configuration) && patient != MedicationReminderTestScope.PatientId))
            return NotFound();
        if (request.Minutes is not (15 or 30) || request.ScheduledAt == default) return BadRequest();
        return await reminders.SnoozeAsync(patient, doseId, request.ScheduledAt.UtcDateTime, request.Minutes, ct)
            ? NoContent() : Conflict();
    }

    [HttpPut("me/medications/treatments/{id:guid}/status")]
    public async Task<ActionResult<MedicationTreatmentDto>> SelfStatus(Guid id, ChangeMedicationStatusRequest request, CancellationToken ct)
    {
        var patient = await SelfAsync(ct);
        return Ok(await medications.ChangeStatusAsync(patient, id, request, true, ct));
    }

    [HttpGet("medications/catalog/us")]
    [RequireContextPermission("Medications.Manage")]
    public async Task<ActionResult<IReadOnlyList<RxTermsOption>>> SearchUsCatalog(
        string? search, [FromServices] IRxTermsCatalog catalog, CancellationToken ct)
    {
        EnsureErp();
        return Ok(await catalog.SearchAsync(search, ct));
    }

    [HttpGet("medications/overview")]
    [RequireContextPermission("Medications.View")]
    public async Task<ActionResult<MedicationOverviewDto>> Overview(string? timeZone, CancellationToken ct)
    {
        EnsureErp();
        return Ok(await medications.OverviewAsync(timeZone, current.ActiveClinicId, current.ActiveOrganizationId, ct));
    }

    [HttpGet("medications/patients")]
    [RequireContextPermission("Medications.View")]
    public async Task<ActionResult<MedicationPatientPage>> Patients(string? query = null, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        EnsureErp();
        return Ok(await medications.ListPatientsAsync(query, page, pageSize, current.ActiveClinicId, current.ActiveOrganizationId, ct));
    }

    [HttpGet("medications/patients/{patientId:guid}")]
    [RequireContextPermission("Medications.View")]
    public async Task<ActionResult<MedicationWorkspaceDto>> Patient(Guid patientId, DateOnly? date, string? timeZone, CancellationToken ct)
    {
        await StaffAsync(patientId, "Medications.View", ct);
        var result = await medications.WorkspaceAsync(patientId, date, timeZone, false, ct);
        if (monitoring is not null) await monitoring.Store.AuditAccessAsync([patientId], ActorId, "staff_recorded", "read", "workspace", null, ct);
        return Ok(result);
    }

    [HttpGet("medications/patients/{patientId:guid}/version")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [RequireContextPermission("Medications.View")]
    public async Task<ActionResult<Guid>> PatientVersion(Guid patientId, CancellationToken ct)
    {
        await StaffAsync(patientId, "Medications.View", ct);
        return Ok(await medications.VersionAsync(patientId, ct));
    }

    [HttpGet("medications/patients/{patientId:guid}/adherence")]
    [RequireContextPermission("Medications.View")]
    public async Task<ActionResult<MedicationAdherenceSummaryDto>> Adherence(Guid patientId, string? timeZone, CancellationToken ct)
    {
        await StaffAsync(patientId, "Medications.View", ct);
        return Ok(await medications.AdherenceAsync(patientId, timeZone, ct));
    }

    [HttpPut("medications/patients/{patientId:guid}/treatments/{treatmentId:guid}")]
    [RequireContextPermission("Medications.Manage")]
    public async Task<ActionResult<MedicationTreatmentDto>> Edit(Guid patientId, Guid treatmentId,
        UpdateMedicationRegimenRequest request, CancellationToken ct)
    {
        await StaffAsync(patientId, "Medications.Manage", ct);
        return Ok(await medications.UpdateRegimenAsync(patientId, treatmentId, ActorId, request, ct));
    }

    [HttpPut("medications/patients/{patientId:guid}/access")]
    [RequireContextPermission("Medications.ManageAccess")]
    public async Task<ActionResult<MedicationAccessDto>> Access(Guid patientId, ChangeMedicationAccessRequest request, CancellationToken ct)
    {
        await StaffAsync(patientId, "Medications.ManageAccess", ct);
        return Ok(await medications.SetAccessAsync(patientId, ActorId, request, ct));
    }

    [HttpPost("medications/patients/{patientId:guid}/treatments")]
    [RequireContextPermission("Medications.Manage")]
    public async Task<ActionResult<MedicationTreatmentDto>> Create(Guid patientId, CreateMedicationTreatmentRequest request, CancellationToken ct)
    {
        await StaffAsync(patientId, "Medications.Manage", ct);
        var result = await medications.CreateAsync(patientId, ActorId, "staff_recorded", request, ct);
        return StatusCode(result.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK, result.Treatment);
    }

    [HttpPost("medications/patients/{patientId:guid}/doses/{doseId:guid}/events")]
    [RequireContextPermission("Medications.Manage")]
    public async Task<ActionResult<MedicationDoseDto>> Record(Guid patientId, Guid doseId, RecordMedicationDoseRequest request, CancellationToken ct)
    {
        await StaffAsync(patientId, "Medications.Manage", ct);
        return Ok(await medications.RecordAsync(patientId, ActorId, "staff_recorded", doseId, request, ct));
    }

    [HttpPut("medications/patients/{patientId:guid}/treatments/{id:guid}/status")]
    [RequireContextPermission("Medications.Manage")]
    public async Task<ActionResult<MedicationTreatmentDto>> Status(Guid patientId, Guid id, ChangeMedicationStatusRequest request, CancellationToken ct)
    {
        await StaffAsync(patientId, "Medications.Manage", ct);
        return Ok(await medications.ChangeStatusAsync(patientId, id, request, false, ct));
    }

    private bool IsApplication(string application) => User.HasClaim("aud", application);
    private Guid ActorId => current.UserId ?? throw new ForbiddenException("Se requiere una identidad válida.");
    private void EnsureErp()
    {
        if (!IsApplication("erp")) throw new ForbiddenException("Se requiere acceso al ERP.");
        medications.EnsureEnabled();
    }
    private Task<Guid> SelfAsync(CancellationToken ct)
    {
        if (!IsApplication("app")) throw new ForbiddenException("Se requiere acceso a la aplicación para personas usuarias.");
        return medications.EnsureSelfAsync(ActorId, ct);
    }
    private Task StaffAsync(Guid patientId, string permission, CancellationToken ct)
    {
        EnsureErp();
        return medications.EnsureStaffAsync(patientId, permission, current.ActiveClinicId, current.ActiveOrganizationId, ct);
    }
}
