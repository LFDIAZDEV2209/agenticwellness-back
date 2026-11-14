using CoppAddresd.Application.Features.Nutrition.Commands;

namespace CoppAddresd.Api.Security;

/// <summary>Firma solo tras autorizar; la descarga usa una capacidad distinta de las firmas genéricas.</summary>
public sealed class NutritionAttachmentUrlSigner(StorageSignatureService signatures, IHttpContextAccessor http) : INutritionAttachmentUrlSigner
{
    public string Sign(Guid attachmentId, DateTimeOffset expiresAt)
    {
        var request = http.HttpContext?.Request ?? throw new InvalidOperationException("Falta contexto HTTP para firmar el adjunto.");
        return $"{request.Scheme}://{request.Host}{request.PathBase}/api/v1/nutrition/attachments/{attachmentId:D}/content?exp={expiresAt.ToUnixTimeSeconds()}&sig={signatures.SignNutritionAttachment(attachmentId, expiresAt)}";
    }
}
