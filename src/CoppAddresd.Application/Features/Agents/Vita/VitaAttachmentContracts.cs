using CoppAddresd.Domain.Entities;

namespace CoppAddresd.Application.Features.Agents.Vita;

public sealed record VitaAttachmentMetadata(
    Guid AttachmentId, string Kind, string ContentType, long SizeBytes,
    int? PageCount, string Sha256, DateTimeOffset ExpiresAt);

public sealed record VitaAttachmentDto(
    Guid AttachmentId, string Kind, string ContentType, long SizeBytes,
    int? PageCount, DateTimeOffset ExpiresAt);

public sealed record ArchiveVitaAttachmentRequest(bool Confirmed, string? Title, Guid DocumentTypeId);

public sealed record ArchivedVitaAttachmentDto(
    Guid DocumentId, Guid AttachmentId, string Title, Guid DocumentTypeId,
    string ContentType, long SizeBytes);

public sealed record VitaAttachmentArchiveResult(ArchivedVitaAttachmentDto Document, bool Created);

public sealed class VitaAttachmentException(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;

    public static VitaAttachmentException Unavailable() => new(503, "attachment_service_unavailable");
    public static VitaAttachmentException InvalidResponse() => new(502, "attachment_invalid_response");
    public static VitaAttachmentException IntegrityMismatch() => new(502, "attachment_integrity_mismatch");
}

public interface IVitaAttachmentClient
{
    Task<VitaAttachmentMetadata> UploadAsync(Guid userId, Guid patientId, Stream file, string fileName,
        string contentType, string? threadId, string? language, CancellationToken ct);
    Task<VitaAttachmentMetadata> GetAsync(Guid patientId, Guid attachmentId, CancellationToken ct);
    Task<byte[]> DownloadAsync(Guid patientId, Guid attachmentId, int expectedSize, CancellationToken ct);
    Task DeleteAsync(Guid patientId, Guid attachmentId, CancellationToken ct);
}

public interface IVitaAttachmentArchives
{
    Task<Document?> FindAsync(Guid patientId, Guid attachmentId, CancellationToken ct);
    Task<bool> TryInsertAsync(Document document, CancellationToken ct);
}
