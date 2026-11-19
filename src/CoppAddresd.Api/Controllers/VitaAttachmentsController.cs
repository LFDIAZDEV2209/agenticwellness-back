using CoppAddresd.Api.Context;
using CoppAddresd.Application.Features.Agents.Vita;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoppAddresd.Api.Controllers;

[ApiController, Authorize, Route("api/v1/vita/attachments")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class VitaAttachmentsController(ISender sender, ICurrentContext context) : ControllerBase
{
    private const int MaxRequestBytes = UploadVitaAttachmentCommand.MaxFileBytes + 64 * 1024;

    [HttpPost]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes, ValueLengthLimit = 4096, ValueCountLimit = 16)]
    public async Task<ActionResult<VitaAttachmentDto>> Upload(CancellationToken ct)
    {
        var userId = AppUserId();
        if (Request.ContentType is null ||
            !Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(Request.ContentType, out var mediaType) ||
            !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            throw new VitaAttachmentException(415, "attachment_type_not_allowed");
        if (Request.ContentLength > MaxRequestBytes)
            throw new VitaAttachmentException(413, "attachment_too_large");

        IFormCollection form;
        try { form = await Request.ReadFormAsync(ct); }
        catch (BadHttpRequestException ex) when (ex.StatusCode == 413)
        {
            throw new VitaAttachmentException(413, "attachment_too_large");
        }
        catch (InvalidDataException)
        {
            throw new VitaAttachmentException(422, "invalid_attachment_request");
        }

        if (form.Files.Count != 1 || form.Files.GetFile("file") is not { } file ||
            form["threadId"].Count > 1 || form["language"].Count > 1)
            throw new VitaAttachmentException(422, "invalid_attachment_request");

        await using var content = file.OpenReadStream();
        var result = await sender.Send(new UploadVitaAttachmentCommand(userId, content, file.Length,
            file.FileName, file.ContentType, form["threadId"].FirstOrDefault(), form["language"].FirstOrDefault()), ct);
        return StatusCode(201, result);
    }

    [HttpPost("{attachmentId:guid}/archive")]
    public async Task<ActionResult<ArchivedVitaAttachmentDto>> Archive(Guid attachmentId,
        [FromBody] ArchiveVitaAttachmentRequest request, CancellationToken ct)
    {
        var result = await sender.Send(new ArchiveVitaAttachmentCommand(AppUserId(), attachmentId, request), ct);
        return StatusCode(result.Created ? 201 : 200, result.Document);
    }

    [HttpDelete("{attachmentId:guid}")]
    public async Task<IActionResult> Delete(Guid attachmentId, CancellationToken ct)
    {
        await sender.Send(new DeleteVitaAttachmentCommand(AppUserId(), attachmentId), ct);
        return NoContent();
    }

    private Guid AppUserId()
    {
        if (!User.HasClaim("aud", "app"))
            throw new VitaAttachmentException(403, "vita_identity_required");
        if (context.UserId is not { } userId || userId == Guid.Empty)
            throw new VitaAttachmentException(401, "vita_identity_required");
        return userId;
    }
}
