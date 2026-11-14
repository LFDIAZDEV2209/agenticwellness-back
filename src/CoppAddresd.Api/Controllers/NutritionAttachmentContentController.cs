using CoppAddresd.Api.Security;
using CoppAddresd.Application.Features.Nutrition.Commands;
using CoppAddresd.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoppAddresd.Api.Controllers;

/// <summary>Capacidad de cinco minutos emitida tras el guard; no permite escrituras ni firmas genéricas.</summary>
[ApiController]
[Route("api/v1/nutrition/attachments")]
public sealed class NutritionAttachmentContentController(StorageSignatureService signatures, INutritionCycleReviewStore store,
    IObjectStorageService storage, TimeProvider clock) : ControllerBase
{
    [HttpGet("{id:guid}/content")]
    [AllowAnonymous]
    public async Task<IActionResult> Download(Guid id, [FromQuery] string? sig, [FromQuery] string? exp, CancellationToken ct)
    {
        if (!signatures.ValidateNutritionAttachment(id, sig, exp, clock.GetUtcNow())) return Unauthorized();
        var file = await store.AttachmentAsync(id, ct);
        if (file is null) return NotFound();
        CoppAddresd.Application.Common.Storage.DocumentArchiveStorageKeys.RequireArchiveKey(file.Attachment.StorageKey);
        if (!file.Attachment.StorageKey.StartsWith($"documents/{file.PatientId:D}/{id:D}/original.", StringComparison.Ordinal)) return NotFound();
        Response.Headers.CacheControl = "private, no-store"; Response.Headers.XContentTypeOptions = "nosniff";
        try
        {
            var stream = await storage.GetDocumentArchiveObjectAsync(file.Attachment.StorageKey, ct);
            return File(stream, file.Attachment.ContentType, file.Attachment.Kind == "document" ? "recomendacion.pdf" : null, enableRangeProcessing: true);
        }
        catch (FileNotFoundException) { return NotFound(); }
    }
}
