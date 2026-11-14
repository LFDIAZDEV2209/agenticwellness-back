using CoppAddresd.Application.Common.Storage;
using CoppAddresd.Application.Features.Nutrition.Queries;
using CoppAddresd.Application.Interfaces;

namespace CoppAddresd.Api.Security;

/// <summary>Firma de fotos ya autorizadas por el handler y vinculadas al paciente en la proyección.</summary>
public sealed class NutritionPhotoUrlSigner(IObjectStorageService storage, StorageSignatureService signatures,
    IHttpContextAccessor http, TimeProvider clock) : INutritionPhotoUrlSigner
{
    public async Task<IntakePhotoDto?> SignAsync(Guid analysisId, string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!FoodPhotoStorageKeys.IsProtected(key)) return null;
        var segments = key.Split('/');
        if (segments.Length != 2 || !Guid.TryParse(Path.GetFileNameWithoutExtension(segments[1]), out var id) || id != analysisId) return null;
        var expiry = clock.GetUtcNow().AddMinutes(5);
        if (storage.IsCloudStorage)
            return new(await storage.GetFoodPhotoReadUrlAsync(key, TimeSpan.FromMinutes(5), ct), expiry);
        var request = http.HttpContext?.Request ?? throw new InvalidOperationException("Falta contexto HTTP para firmar la foto.");
        var url = $"{request.Scheme}://{request.Host}{request.PathBase}/api/v1/storage/{key}?exp={expiry.ToUnixTimeSeconds()}&sig={signatures.SignFoodPhoto(key, expiry)}";
        return new(url, expiry);
    }
}
