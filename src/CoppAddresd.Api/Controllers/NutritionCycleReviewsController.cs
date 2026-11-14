using CoppAddresd.Api.Authorization;
using CoppAddresd.Api.Constants;
using CoppAddresd.Application.Features.Nutrition.Commands;
using CoppAddresd.Application.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoppAddresd.Api.Controllers;

[ApiController]
[Authorize(Policy = NutritionPolicies.Staff)]
[Route("api/v1/nutrition/cycles")]
public sealed class NutritionCycleReviewsController(IMediator mediator) : ControllerBase
{
    public sealed record ReviewRequest(int Revision, string? ProfessionalNotes, CyclePatientMessage? PatientMessage,
        bool CriticalAlertsConfirmed = false, bool AiDraftConfirmed = false);
    public sealed record PublishRequest(int Revision, Guid NextPrescriptionId, int NextPrescriptionRevision);
    public sealed record AttachmentForm(IFormFile File, int Revision, string? Caption = null, bool VisibleToPatient = false);

    [HttpGet("{id:guid}/review")]
    [PatientScope(PatientResource.Cycle, "id", "Nutrition.View")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<CycleReviewDto?>> GetReview(Guid id, CancellationToken ct)
        => Ok(await mediator.Send(new GetNutritionCycleReview(id), ct));

    [HttpPost("{id:guid}/review")]
    [PatientScope(PatientResource.Cycle, "id", "Nutrition.Approve")]
    public async Task<ActionResult<CycleReviewDto>> Save(Guid id, [FromBody] ReviewRequest body, CancellationToken ct)
        => Ok(await mediator.Send(new SaveNutritionCycleReview(id, body.Revision, body.ProfessionalNotes,
            body.PatientMessage, body.CriticalAlertsConfirmed, body.AiDraftConfirmed), ct));

    [HttpPost("{id:guid}/publish")]
    [PatientScope(PatientResource.Cycle, "id", "Nutrition.Approve")]
    [PatientScope(PatientResource.Prescription, "body.NextPrescriptionId", "Nutrition.Approve")]
    public async Task<ActionResult<CycleReviewDto>> Publish(Guid id, [FromBody] PublishRequest body, CancellationToken ct)
        => Ok(await mediator.Send(new PublishNutritionCycleReview(id, body.Revision, body.NextPrescriptionId, body.NextPrescriptionRevision), ct));

    [HttpPost("{id:guid}/review/attachments")]
    [PatientScope(PatientResource.Cycle, "id", "Nutrition.Approve")]
    [PatientScope(PatientResource.Cycle, "id", "Nutrition.PhotosView")]
    [RequestSizeLimit(11 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 11 * 1024 * 1024)]
    public async Task<ActionResult<CycleAttachmentMutationDto>> Upload(Guid id, [FromForm] AttachmentForm body, CancellationToken ct)
    {
        await using var stream = body.File.OpenReadStream();
        return Ok(await mediator.Send(new UploadNutritionCycleAttachment(id, body.Revision, stream, body.File.FileName,
            body.File.ContentType, body.File.Length, body.Caption, body.VisibleToPatient), ct));
    }

    [HttpDelete("{id:guid}/review/attachments/{attachmentId:guid}")]
    [PatientScope(PatientResource.Cycle, "id", "Nutrition.Approve")]
    [PatientScope(PatientResource.NutritionAttachment, "attachmentId", "Nutrition.Approve")]
    public async Task<ActionResult<object>> RemoveAttachment(Guid id, Guid attachmentId, [FromQuery] int revision, CancellationToken ct)
        => Ok(new { revision = await mediator.Send(new DeleteNutritionCycleAttachment(id, attachmentId, revision), ct) });
}
