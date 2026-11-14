using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Exceptions;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CoppAddresd.Application.Features.Nutrition.Commands;

public sealed record UploadNutritionCycleAttachment(Guid CycleId, int Revision, Stream Content, string FileName, string ContentType,
    long Length, string? Caption = null, bool VisibleToPatient = false) : IRequest<CycleAttachmentMutationDto>;
public sealed record DeleteNutritionCycleAttachment(Guid CycleId, Guid AttachmentId, int Revision) : IRequest<int>;
public sealed record CycleAttachmentMutationDto(int Revision, CycleAttachmentDto Attachment);

public sealed class CycleAttachmentHandler(IPatientScopeGuard scope, IPatientScopeActor actor, INutritionCycleReviewStore store,
    IObjectStorageService storage, INutritionAttachmentUrlSigner urls, TimeProvider clock, ILogger<CycleAttachmentHandler> logger)
    : IRequestHandler<UploadNutritionCycleAttachment, CycleAttachmentMutationDto>, IRequestHandler<DeleteNutritionCycleAttachment, int>
{
    public const int MaximumBytes = 10 * 1024 * 1024;
    public async Task<CycleAttachmentMutationDto> Handle(UploadNutritionCycleAttachment request, CancellationToken ct)
    {
        var patient = await scope.EnsureResourceAsync(PatientResource.Cycle, request.CycleId, "Nutrition.Approve", ct);
        await scope.EnsureAsync(patient, "Nutrition.PhotosView", ct);
        var author = actor.UserId ?? throw new ForbiddenException("Falta identidad profesional.");
        if (request.Length is <= 0 or > MaximumBytes || request.Caption?.Length > 2000 || string.IsNullOrWhiteSpace(request.FileName))
            throw new ValidationException("Adjunto inválido: máximo 10 MB y pie de 2000 caracteres.");
        CycleReviewHandler.EnsureEditable(await store.LoadAsync(request.CycleId, ct)
            ?? throw new UnprocessableEntityException("Guarda la revisión antes de adjuntar archivos."), request.Revision);
        await using var content = new MemoryStream(); var buffer = new byte[65536]; int read;
        while ((read = await request.Content.ReadAsync(buffer, ct)) > 0)
        {
            if (content.Length + read > MaximumBytes) throw new ValidationException("El archivo supera 10 MB.");
            await content.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        if (content.Length != request.Length) throw new ValidationException("Longitud del archivo inconsistente.");
        var type = request.ContentType.Trim().ToLowerInvariant();
        var extension = ValidateFile(request.FileName, type, content.ToArray());
        var attachment = new NutritionCycleAttachment { Id = Guid.NewGuid(), Caption = request.Caption?.Trim(), ContentType = type,
            Length = content.Length, Kind = type == "application/pdf" ? "document" : "image", VisibleToPatient = request.VisibleToPatient };
        // Reutiliza el espacio privado de originales; el proxy genérico no resuelve estos IDs como documentos ERP.
        attachment.StorageKey = $"documents/{patient:D}/{attachment.Id:D}/original.{extension}";
        content.Position = 0;
        int revision;
        try
        {
            await storage.PutDocumentArchiveObjectAsync(attachment.StorageKey, content, type, ct);
            // Revalidar tras I/O externo, antes de persistir (asignación o permiso pudo revocarse).
            await scope.EnsureAsync(patient, "Nutrition.Approve", ct); await scope.EnsureAsync(patient, "Nutrition.PhotosView", ct);
            revision = await store.WithPatientLockAsync(patient, async writeCt =>
            {
                var review = await store.LoadAsync(request.CycleId, writeCt) ?? throw new NotFoundException("Revisión no encontrada.");
                CycleReviewHandler.EnsureEditable(review, request.Revision);
                attachment.ReviewId = review.Id; review.Attachments.Add(attachment); review.Revision++;
                await store.AddAttachmentAsync(attachment, writeCt);
                await store.SaveAsync(review, writeCt); await store.AuditAsync(review, author, "NutritionCycleAttachmentAdded", writeCt);
                return review.Revision;
            }, ct);
        }
        catch
        {
            await CleanupAsync(attachment.StorageKey);
            throw;
        }
        // Un fallo al construir la respuesta después del commit no debe borrar un archivo ya referenciado.
        return new(revision, new(attachment.Id, attachment.Kind, attachment.Caption, attachment.VisibleToPatient,
            urls.Sign(attachment.Id, clock.GetUtcNow().AddMinutes(5))));
    }

    public async Task<int> Handle(DeleteNutritionCycleAttachment request, CancellationToken ct)
    {
        var patient = await scope.EnsureResourceAsync(PatientResource.Cycle, request.CycleId, "Nutrition.Approve", ct);
        if (await scope.EnsureResourceAsync(PatientResource.NutritionAttachment, request.AttachmentId, "Nutrition.Approve", ct) != patient)
            throw new NotFoundException("Adjunto no encontrado.");
        await scope.EnsureAsync(patient, "Nutrition.PhotosView", ct);
        var author = actor.UserId ?? throw new ForbiddenException("Falta identidad profesional.");
        var result = await store.WithPatientLockAsync(patient, async writeCt =>
        {
            var review = await store.LoadAsync(request.CycleId, writeCt) ?? throw new NotFoundException("Revisión no encontrada.");
            CycleReviewHandler.EnsureEditable(review, request.Revision);
            var file = review.Attachments.SingleOrDefault(a => a.Id == request.AttachmentId) ?? throw new NotFoundException("Adjunto no encontrado.");
            review.Attachments.Remove(file);
            await store.DeleteAttachmentAsync(file, writeCt); review.Revision++;
            await store.SaveAsync(review, writeCt); await store.AuditAsync(review, author, "NutritionCycleAttachmentRemoved", writeCt);
            return (review.Revision, file.StorageKey);
        }, ct);
        await CleanupAsync(result.StorageKey); return result.Revision;
    }

    private async Task CleanupAsync(string key)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await storage.DeleteDocumentArchiveObjectAsync(key, cleanup.Token); }
        catch { logger.LogWarning("No se pudo limpiar un adjunto privado huérfano de nutrición."); }
    }

    internal static string ValidateFile(string name, string type, byte[] bytes)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        var valid = type switch
        {
            "image/jpeg" => extension is ".jpg" or ".jpeg" && bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 }),
            "image/png" => extension == ".png" && bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            "image/webp" => extension == ".webp" && bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8),
            "application/pdf" => extension == ".pdf" && bytes.AsSpan().StartsWith("%PDF-"u8),
            _ => false
        };
        if (!valid) throw new ValidationException("Solo JPEG, PNG, WebP o PDF con extensión, MIME y firma concordantes.");
        return type == "image/jpeg" ? "jpg" : extension[1..];
    }
}
