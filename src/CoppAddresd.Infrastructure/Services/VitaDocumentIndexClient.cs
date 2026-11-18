using System.Net;
using System.Net.Http.Json;
using CoppAddresd.Application.Common;
using Microsoft.Extensions.Options;

namespace CoppAddresd.Infrastructure.Services;

/// <summary>Transporte del contrato privado de indexación por paciente y documento.</summary>
public sealed class VitaDocumentIndexClient(HttpClient http, IOptions<AiServiceSettings> settings)
{
    private const string Documents = "/internal/vita/documents";

    public Task IndexAsync(Guid patientId, Guid documentId, string title, DateTime documentDate,
        string content, CancellationToken ct)
        => SendAsync(HttpMethod.Post, $"{Documents}/index", JsonContent.Create(new
        {
            patientId, documentId, title, documentDate, status = "active", content, contentType = "text/plain",
        }), ct);

    public Task DeleteAsync(Guid patientId, Guid documentId, CancellationToken ct)
        => SendAsync(HttpMethod.Delete, $"{Documents}/{documentId:D}?patientId={patientId:D}", null, ct);

    private async Task SendAsync(HttpMethod method, string endpoint, HttpContent? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, endpoint) { Content = body };
        if (string.IsNullOrWhiteSpace(settings.Value.InternalApiKey))
            throw new InvalidOperationException("La clave interna de indexación no está configurada.");
        request.Headers.Add("X-Internal-Key", settings.Value.InternalApiKey);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (method == HttpMethod.Delete && response.StatusCode == HttpStatusCode.NotFound) return;
        response.EnsureSuccessStatusCode();
    }
}
