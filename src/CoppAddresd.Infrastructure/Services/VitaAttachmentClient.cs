using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CoppAddresd.Application.Common;
using CoppAddresd.Application.Features.Agents.Vita;
using Microsoft.Extensions.Options;

namespace CoppAddresd.Infrastructure.Services;

public sealed class VitaAttachmentClient(HttpClient http, IOptions<AiServiceSettings> options) : IVitaAttachmentClient
{
    private const string Endpoint = "/internal/vita/attachments";

    public Task<VitaAttachmentMetadata> UploadAsync(Guid userId, Guid patientId, Stream file, string fileName,
        string contentType, string? threadId, string? language, CancellationToken ct)
    {
        var multipart = new MultipartFormDataContent();
        var upload = new StreamContent(file);
        upload.Headers.ContentType = MediaTypeHeaderValue.TryParse(contentType, out var mediaType)
            ? mediaType : new MediaTypeHeaderValue("application/octet-stream");
        // Solo la extensión interviene en la validación del AI.
        var extension = Path.GetExtension(fileName.Replace('\\', '/'));
        multipart.Add(upload, "file", extension.Length <= 10 && extension.Skip(1).All(char.IsAsciiLetterOrDigit)
            ? $"attachment{extension}" : "attachment.invalid");
        multipart.Add(new StringContent(userId.ToString("D")), "userId");
        multipart.Add(new StringContent(patientId.ToString("D")), "patientId");
        if (threadId is not null) multipart.Add(new StringContent(threadId), "threadId");
        if (language is not null) multipart.Add(new StringContent(language), "language");

        return SendAsync(new(HttpMethod.Post, Endpoint) { Content = multipart }, ReadMetadataAsync, ct);
    }

    public Task<VitaAttachmentMetadata> GetAsync(Guid patientId, Guid attachmentId, CancellationToken ct) =>
        SendAsync(new(HttpMethod.Get, $"{Endpoint}/{attachmentId:D}?patientId={patientId:D}"), ReadMetadataAsync, ct);

    public Task<byte[]> DownloadAsync(Guid patientId, Guid attachmentId, int expectedSize, CancellationToken ct) =>
        SendAsync(new(HttpMethod.Get, $"{Endpoint}/{attachmentId:D}/content?patientId={patientId:D}"),
            async (response, token) =>
            {
                if (response.Content.Headers.ContentLength is { } size && size != expectedSize)
                    throw VitaAttachmentException.IntegrityMismatch();
                var bytes = new byte[expectedSize];
                await using var stream = await response.Content.ReadAsStreamAsync(token);
                try { await stream.ReadExactlyAsync(bytes, token); }
                catch (EndOfStreamException) { throw VitaAttachmentException.IntegrityMismatch(); }
                if (await stream.ReadAsync(new byte[1], token) != 0)
                    throw VitaAttachmentException.IntegrityMismatch();
                return bytes;
            }, ct);

    public Task DeleteAsync(Guid patientId, Guid attachmentId, CancellationToken ct) =>
        SendAsync(new(HttpMethod.Delete, $"{Endpoint}/{attachmentId:D}?patientId={patientId:D}"),
            static (_, _) => Task.FromResult(true), ct);

    private async Task<T> SendAsync<T>(HttpRequestMessage request,
        Func<HttpResponseMessage, CancellationToken, Task<T>> read, CancellationToken ct)
    {
        using (request)
        {
            ct.ThrowIfCancellationRequested();
            var settings = options.Value;
            if (string.IsNullOrWhiteSpace(settings.InternalApiKey)) throw VitaAttachmentException.Unavailable();
            request.Headers.TryAddWithoutValidation("X-Internal-Key", settings.InternalApiKey);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
            try
            {
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (!response.IsSuccessStatusCode)
                    throw await VitaAttachmentHttpErrors.ReadAsync(response, timeout.Token)
                        ?? VitaAttachmentException.InvalidResponse();
                return await read(response, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw VitaAttachmentException.Unavailable(); }
            catch (HttpRequestException) { throw VitaAttachmentException.Unavailable(); }
            catch (IOException) { throw VitaAttachmentException.Unavailable(); }
            catch (JsonException) { throw VitaAttachmentException.InvalidResponse(); }
        }
    }

    private static async Task<VitaAttachmentMetadata> ReadMetadataAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var metadata = await response.Content.ReadFromJsonAsync<VitaAttachmentMetadata>(cancellationToken: ct);
        if (metadata is null || metadata.AttachmentId == Guid.Empty || metadata.Kind is not ("pdf" or "image")
            || string.IsNullOrWhiteSpace(metadata.ContentType) || metadata.SizeBytes <= 0 || metadata.ExpiresAt == default)
            throw VitaAttachmentException.InvalidResponse();
        return metadata;
    }
}

internal static class VitaAttachmentHttpErrors
{
    public static async Task<VitaAttachmentException?> ReadAsync(HttpResponseMessage response, CancellationToken ct,
        bool forChat = false)
    {
        var status = (int)response.StatusCode;
        if (status >= 500 && status != 503 || status is 401 or 429)
            return VitaAttachmentException.Unavailable();
        if (status == 413) return new(status, "attachment_too_large");
        if (status == 415) return new(status, "attachment_type_not_allowed");
        if (status is not (403 or 404 or 422 or 503)) return null;

        string? detail = null;
        try
        {
            var error = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("detail", out var value))
            {
                if (value.ValueKind == JsonValueKind.String) detail = value.GetString();
                if (forChat && status == 422 && value.ValueKind == JsonValueKind.Array &&
                    value.EnumerateArray().Any(IsAttachmentLimitError))
                    return new(status, "attachment_limit_exceeded");
            }
        }
        catch (JsonException) { }

        return (status, detail) switch
        {
            (403, "vita_attachments_disabled" or "vita_identity_required") => new(status, detail),
            (403, _) => new(status, "attachment_forbidden"),
            (404, "vita_attachment_not_found") => new(status, detail),
            (404, _) when !forChat => new(status, "vita_attachment_not_found"),
            (422, "attachment_empty" or "attachment_mime_mismatch" or "attachment_too_many_pages" or
                "attachment_pdf_encrypted" or "attachment_unreadable" or "attachment_limit_exceeded") => new(status, detail),
            (422, _) => new(status, "invalid_attachment_request"),
            (503, "attachment_store_unavailable") => new(status, detail),
            (503, _) => VitaAttachmentException.Unavailable(),
            _ => null
        };
    }

    private static bool IsAttachmentLimitError(JsonElement error) =>
        error.ValueKind == JsonValueKind.Object && error.TryGetProperty("type", out var type)
        && type.ValueKind == JsonValueKind.String && type.GetString() == "too_long"
        && error.TryGetProperty("loc", out var location) && location.ValueKind == JsonValueKind.Array
        && location.EnumerateArray().Any(field => field.ValueKind == JsonValueKind.String && field.GetString() == "attachment_ids");
}
