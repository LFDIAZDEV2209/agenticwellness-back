using System.Security.Cryptography;
using System.Text.Json;
using CoppAddresd.Application.Features.Medications;
using Microsoft.AspNetCore.Mvc;

namespace CoppAddresd.Api.Controllers;

public sealed partial class MedicationControlController
{
    private const int MaxPhotoBytes = 5 * 1024 * 1024;
    private const int MaxPhotoRequestBytes = MaxPhotoBytes + 64 * 1024;
    private static readonly JsonSerializerOptions PhotoJsonOptions = new(JsonSerializerDefaults.Web);

    // La variante JSON sigue siendo la operación documentada por OpenAPI.
    [HttpPost("me/medications/treatments")]
    [Consumes("multipart/form-data")]
    [ApiExplorerSettings(IgnoreApi = true)]
    [RequestSizeLimit(MaxPhotoRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxPhotoRequestBytes, ValueLengthLimit = 16384, ValueCountLimit = 4)]
    public Task<ActionResult<MedicationTreatmentDto>> SelfCreateWithPhoto(CancellationToken ct)
        => SubmitPhotoAsync<CreateMedicationTreatmentRequest, MedicationTreatmentDto>(async (patient, request, photo) =>
        {
            var result = await medications.CreateAsync(patient, ActorId, "patient_reported", request, ct, photo);
            return StatusCode(result.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK, result.Treatment);
        }, ct);

    [HttpPost("me/medications/doses/{doseId:guid}/events")]
    [Consumes("multipart/form-data")]
    [ApiExplorerSettings(IgnoreApi = true)]
    [RequestSizeLimit(MaxPhotoRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxPhotoRequestBytes, ValueLengthLimit = 16384, ValueCountLimit = 4)]
    public Task<ActionResult<MedicationDoseDto>> SelfRecordWithPhoto(Guid doseId, CancellationToken ct)
        => SubmitPhotoAsync<RecordMedicationDoseRequest, MedicationDoseDto>(async (patient, request, photo) =>
            Ok(await medications.RecordAsync(patient, ActorId, "patient_reported", doseId, request, ct, photo)), ct);

    [HttpGet("me/medications/treatments/{id:guid}/photo")]
    public Task<IActionResult> SelfTreatmentPhoto(Guid id, CancellationToken ct)
        => SelfPhotoAsync(id, MedicationPhotoTarget.Treatment, ct);

    [HttpGet("me/medications/events/{id:guid}/photo")]
    public Task<IActionResult> SelfEventPhoto(Guid id, CancellationToken ct)
        => SelfPhotoAsync(id, MedicationPhotoTarget.IntakeEvent, ct);

    private async Task<IActionResult> SelfPhotoAsync(Guid id, MedicationPhotoTarget target, CancellationToken ct)
    {
        var patientId = await SelfAsync(ct);
        var photo = await medications.ReadPhotoAsync(patientId, id, target, ct);
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(photo.Content, photo.ContentType);
    }

    private async Task<ActionResult<TResponse>> SubmitPhotoAsync<TRequest, TResponse>(
        Func<Guid, TRequest, MedicationPhotoUpload, Task<ActionResult<TResponse>>> submit, CancellationToken ct)
        where TRequest : class
    {
        var patient = await SelfAsync(ct);
        if (Request.ContentLength > MaxPhotoRequestBytes) return PhotoTooLarge();

        IFormCollection form;
        try { form = await Request.ReadFormAsync(ct); }
        catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status413PayloadTooLarge)
        { return PhotoTooLarge(); }
        catch (InvalidDataException) { return InvalidPhoto(); }

        if (form.Files.Count != 1 || form.Files.GetFile("photo") is not { Length: >= 8 } file)
            return InvalidPhoto();
        if (file.Length > MaxPhotoBytes) return PhotoTooLarge();
        if (form["request"].Count != 1 || form["request"].FirstOrDefault() is not { } requestJson)
            return InvalidPhotoRequest();

        TRequest? request;
        try { request = JsonSerializer.Deserialize<TRequest>(requestJson, PhotoJsonOptions); }
        catch (JsonException) { return InvalidPhotoRequest(); }
        if (request is null) return InvalidPhotoRequest();

        await using var image = file.OpenReadStream();
        var header = new byte[8];
        await image.ReadExactlyAsync(header, ct);
        var contentType = file.ContentType.Split(';')[0].Trim().ToLowerInvariant();
        var validImage = contentType switch
        {
            "image/jpeg" => header.AsSpan().StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }),
            "image/png" => header.AsSpan().SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            _ => false,
        };
        if (!validImage) return InvalidPhoto();

        image.Position = 0;
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(image, ct));
        image.Position = 0;
        return await submit(patient, request, new(image, contentType, digest));
    }

    private BadRequestObjectResult InvalidPhoto() => BadRequest(new { code = "MEDICATION_PHOTO_INVALID" });
    private BadRequestObjectResult InvalidPhotoRequest() => BadRequest(new { code = "MEDICATION_REQUEST_INVALID" });
    private ObjectResult PhotoTooLarge() => StatusCode(StatusCodes.Status413PayloadTooLarge,
        new { code = "MEDICATION_PHOTO_TOO_LARGE" });
}
