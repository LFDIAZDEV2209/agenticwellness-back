using CoppAddresd.Api.Authorization;
using CoppAddresd.Api.Context;
using CoppAddresd.Application.Features.Medications;
using CoppAddresd.Domain.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoppAddresd.Api.Controllers;

[ApiController]
[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/v1")]
public sealed class MedicationControlController(MedicationControlService medications, ICurrentContext current) : ControllerBase
{
    [HttpGet("me/capabilities")]
    public async Task<IActionResult> Capabilities(CancellationToken ct)
    {
        var enabled = false;
        var view = false;
        var manage = false;
        var manageAccess = false;
        var revision = 0;
        if (medications.Enabled && IsApplication("app"))
        {
            var access = await medications.SelfAccessAsync(ActorId, ct);
            enabled = view = manage = access.Enabled;
            revision = access.Revision;
        }
        else if (medications.Enabled && IsApplication("erp"))
        {
            enabled = view = await current.HasPermissionAsync("Medications.View", ct);
            manage = view && await current.HasPermissionAsync("Medications.Manage", ct);
            manageAccess = view && await current.HasPermissionAsync("Medications.ManageAccess", ct);
        }
        Response.Headers.CacheControl = "no-store";
        return Ok(new
        {
            policyVersion = $"medications-v1:{(medications.Enabled ? revision.ToString() : "off")}",
            modules = new { medications = new { enabled, canView = view, canManage = manage, canManageAccess = manageAccess } },
        });
    }

    [HttpGet("me/medications")]
    public async Task<ActionResult<MedicationWorkspaceDto>> SelfWorkspace(DateOnly? date, string? timeZone, CancellationToken ct)
    {
        var patient = await SelfAsync(ct);
        return Ok(await medications.WorkspaceAsync(patient, date, timeZone, true, ct));
    }

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

    [HttpPut("me/medications/treatments/{id:guid}/status")]
    public async Task<ActionResult<MedicationTreatmentDto>> SelfStatus(Guid id, ChangeMedicationStatusRequest request, CancellationToken ct)
    {
        var patient = await SelfAsync(ct);
        return Ok(await medications.ChangeStatusAsync(patient, id, request, true, ct));
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
        return Ok(await medications.WorkspaceAsync(patientId, date, timeZone, false, ct));
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
        if (!IsApplication("app")) throw new ForbiddenException("Se requiere acceso a la aplicación de pacientes.");
        return medications.EnsureSelfAsync(ActorId, ct);
    }
    private Task StaffAsync(Guid patientId, string permission, CancellationToken ct)
    {
        EnsureErp();
        return medications.EnsureStaffAsync(patientId, permission, current.ActiveClinicId, current.ActiveOrganizationId, ct);
    }
}
