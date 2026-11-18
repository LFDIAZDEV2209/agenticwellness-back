using CoppAddresd.Application.Features.Agents.Vita;
using CoppAddresd.Application.Common.Storage;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Exceptions;

namespace CoppAddresd.Api.Security;

public sealed class VitaDocumentLink(IObjectStorageService storage, StorageSignatureService signatures, TimeProvider clock)
    : IVitaDocumentLink
{
    public Task<string> CreateAsync(Document document, TimeSpan lifetime, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var key = document.StorageKey;
        var archive = ArchivedDocumentAccess.IsArchiveKey(key);
        var parts = key.Split('/');
        if (FoodPhotoStorageKeys.IsProtected(key) || key.Contains('\\') || key.Any(char.IsControl) || parts.Any(p => p is "" or "." or "..")
            || archive && !ArchivedDocumentAccess.IsValidArchiveReadKey(document, key))
            throw new NotFoundException("VITA_DOCUMENT_UNAVAILABLE");

        if (storage.IsCloudStorage)
            return archive ? storage.GetDocumentArchiveReadUrlAsync(key, lifetime, ct)
                : storage.GetPreSignedUrlAsync(key, lifetime, ct);
        var expires = clock.GetUtcNow().Add(lifetime);
        var signature = signatures.Sign(key, expires);
        var path = string.Join('/', parts.Select(Uri.EscapeDataString));
        return Task.FromResult($"/api/v1/storage/{path}?exp={expires.ToUnixTimeSeconds()}&sig={signature}");
    }
}
