using System.Security.Cryptography;
using CoppAddresd.Application.Common.Storage;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Entities;
using MediatR;

namespace CoppAddresd.Application.Features.Agents.Vita;

public sealed record ResolveVitaAttachmentPatientQuery(Guid UserId) : IRequest<Guid>;

public sealed class ResolveVitaAttachmentPatientHandler(VitaPatientAccess access)
    : IRequestHandler<ResolveVitaAttachmentPatientQuery, Guid>
{
    public async Task<Guid> Handle(ResolveVitaAttachmentPatientQuery request, CancellationToken ct) =>
        (await access.RequireActiveAsync(request.UserId, ct)).Id;
}

public sealed record UploadVitaAttachmentCommand(Guid UserId, Stream File, long Size, string FileName,
    string ContentType, string? ThreadId, string? Language) : IRequest<VitaAttachmentDto>
{
    public const int MaxFileBytes = 10 * 1024 * 1024;
}

public sealed class UploadVitaAttachmentHandler(VitaPatientAccess access, IVitaAttachmentClient ai)
    : IRequestHandler<UploadVitaAttachmentCommand, VitaAttachmentDto>
{
    public async Task<VitaAttachmentDto> Handle(UploadVitaAttachmentCommand request, CancellationToken ct)
    {
        var patient = await access.RequireAsync(request.UserId, "chat", ct);
        if (request.Size > UploadVitaAttachmentCommand.MaxFileBytes)
            throw new VitaAttachmentException(413, "attachment_too_large");
        if (request.Size == 0)
            throw new VitaAttachmentException(422, "attachment_empty");
        if (request.ThreadId?.Length > 128 || request.Language is not (null or "es" or "en"))
            throw new VitaAttachmentException(422, "invalid_attachment_request");

        var metadata = await ai.UploadAsync(request.UserId, patient.Id, request.File, request.FileName,
            request.ContentType, request.ThreadId, request.Language, ct);
        return new(metadata.AttachmentId, metadata.Kind, metadata.ContentType, metadata.SizeBytes,
            metadata.PageCount, metadata.ExpiresAt);
    }
}

public sealed record ArchiveVitaAttachmentCommand(Guid UserId, Guid AttachmentId,
    ArchiveVitaAttachmentRequest Request) : IRequest<VitaAttachmentArchiveResult>;

public sealed class ArchiveVitaAttachmentHandler(VitaPatientAccess access, IVitaAttachmentClient ai,
    IVitaAttachmentArchives archives, IDocumentRepository documents, IObjectStorageService storage, TimeProvider clock)
    : IRequestHandler<ArchiveVitaAttachmentCommand, VitaAttachmentArchiveResult>
{
    public async Task<VitaAttachmentArchiveResult> Handle(ArchiveVitaAttachmentCommand command, CancellationToken ct)
    {
        var (userId, attachmentId, request) = command;
        if (!request.Confirmed)
            throw new VitaAttachmentException(400, "attachment_confirmation_required");

        var patient = await access.RequireAsync(userId, "chat", ct);
        var existing = await archives.FindAsync(patient.Id, attachmentId, ct);
        if (existing is not null) return Existing(existing);

        var title = request.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200 || request.DocumentTypeId == Guid.Empty)
            throw new VitaAttachmentException(400, "invalid_attachment_archive_request");

        var metadata = await ai.GetAsync(patient.Id, attachmentId, ct);
        var extension = PatientDocumentStorageKeys.VitaAttachmentExtension(metadata.ContentType);
        if (metadata.AttachmentId != attachmentId || extension is null ||
            metadata.SizeBytes is <= 0 or > UploadVitaAttachmentCommand.MaxFileBytes ||
            metadata.Sha256 is not { Length: 64 } || !metadata.Sha256.All(char.IsAsciiHexDigit))
            throw VitaAttachmentException.InvalidResponse();

        var type = await documents.GetTypeByIdAsync(request.DocumentTypeId, ct);
        if (type is not { IsActive: true })
            throw new VitaAttachmentException(422, "attachment_document_type_invalid");
        if (type.AllowedExtensions.Count != 0 &&
            !type.AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) &&
            !(extension == "jpg" && type.AllowedExtensions.Contains("jpeg", StringComparer.OrdinalIgnoreCase)))
            throw new VitaAttachmentException(422, "attachment_document_type_mismatch");

        var bytes = await ai.DownloadAsync(patient.Id, attachmentId, (int)metadata.SizeBytes, ct);
        if (bytes.LongLength != metadata.SizeBytes ||
            !Convert.ToHexString(SHA256.HashData(bytes)).Equals(metadata.Sha256, StringComparison.OrdinalIgnoreCase))
            throw VitaAttachmentException.IntegrityMismatch();
        ct.ThrowIfCancellationRequested();

        var document = new Document
        {
            Id = attachmentId,
            PatientId = patient.Id,
            ClinicId = patient.ClinicId,
            DocumentTypeId = type.Id,
            Title = title,
            StorageKey = PatientDocumentStorageKeys.VitaAttachmentKey(patient.Id, attachmentId, extension),
            ContentType = metadata.ContentType,
            FileSizeBytes = metadata.SizeBytes,
            UploadedBy = userId,
            CreatedBy = userId,
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        };

        // Los reintentos concurrentes escriben el mismo binario inmutable.
        // El trigger del outbox se confirma junto con la única inserción.
        using var content = new MemoryStream(bytes, writable: false);
        await storage.PutDocumentArchiveObjectAsync(document.StorageKey, content, document.ContentType, ct);
        if (await archives.TryInsertAsync(document, ct)) return new(ToDto(document), true);
        existing = await archives.FindAsync(patient.Id, attachmentId, ct)
            ?? throw new VitaAttachmentException(409, "attachment_archive_conflict");
        return Existing(existing);
    }

    private static VitaAttachmentArchiveResult Existing(Document document) =>
        document.DeletedAt is not null
            ? throw new VitaAttachmentException(404, "vita_attachment_not_found")
            : new(ToDto(document), false);

    private static ArchivedVitaAttachmentDto ToDto(Document document) =>
        new(document.Id, document.Id, document.Title, document.DocumentTypeId,
            document.ContentType!, document.FileSizeBytes!.Value);
}

public sealed record DeleteVitaAttachmentCommand(Guid UserId, Guid AttachmentId) : IRequest;

public sealed class DeleteVitaAttachmentHandler(VitaPatientAccess access, IVitaAttachmentClient ai)
    : IRequestHandler<DeleteVitaAttachmentCommand>
{
    public async Task Handle(DeleteVitaAttachmentCommand request, CancellationToken ct)
    {
        var patient = await access.RequireActiveAsync(request.UserId, ct);
        await ai.DeleteAsync(patient.Id, request.AttachmentId, ct);
    }
}
