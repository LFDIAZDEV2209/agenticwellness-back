using System.Net;
using System.Net.Http.Json;
using CoppAddresd.Application.Common;
using CoppAddresd.Application.Common.Storage;
using CoppAddresd.Application.Features.Agents.Vita;
using CoppAddresd.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace CoppAddresd.UnitTests.Features.Agents;

public sealed class VitaAttachmentClientTests
{
    [Theory]
    [InlineData(403, "vita_attachments_disabled", 403, "vita_attachments_disabled")]
    [InlineData(403, "text from upstream", 403, "attachment_forbidden")]
    [InlineData(404, "vita_attachment_not_found", 404, "vita_attachment_not_found")]
    [InlineData(413, "attachment_too_large", 413, "attachment_too_large")]
    [InlineData(415, "attachment_type_not_allowed", 415, "attachment_type_not_allowed")]
    [InlineData(422, "attachment_empty", 422, "attachment_empty")]
    [InlineData(422, "attachment_mime_mismatch", 422, "attachment_mime_mismatch")]
    [InlineData(422, "attachment_pdf_encrypted", 422, "attachment_pdf_encrypted")]
    [InlineData(422, "attachment_too_many_pages", 422, "attachment_too_many_pages")]
    [InlineData(422, "attachment_unreadable", 422, "attachment_unreadable")]
    [InlineData(422, "unrecognized input", 422, "invalid_attachment_request")]
    [InlineData(503, "attachment_store_unavailable", 503, "attachment_store_unavailable")]
    [InlineData(502, "some internal failure", 503, "attachment_service_unavailable")]
    public async Task GetAsync_MapsStatusAndKnownCodeOnly(int upstreamStatus, string detail, int status, string code)
    {
        using var http = Http((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)upstreamStatus)
        {
            Content = JsonContent.Create(new { detail, message = "synthetic-private-data" })
        }));
        var client = Client(http);
        var error = await Assert.ThrowsAsync<VitaAttachmentException>(
            () => client.GetAsync(Guid.NewGuid(), Guid.NewGuid(), default));
        Assert.Equal(status, error.Status);
        Assert.Equal(code, error.Code);
        Assert.Equal(code, error.Message);
    }

    [Fact]
    public async Task GetAsync_SendsPrivateKeyAndPropagatesCallerCancellation()
    {
        using var requestCanceled = new CancellationTokenSource();
        using var http = Http((request, ct) =>
        {
            Assert.Equal("synthetic-attachments-key", Assert.Single(request.Headers.GetValues("X-Internal-Key")));
            requestCanceled.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(ct);
        });
        var client = Client(http);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetAsync(Guid.NewGuid(), Guid.NewGuid(), requestCanceled.Token));
    }

    [Theory]
    [InlineData(new byte[] { 1, 2 })]
    [InlineData(new byte[] { 1, 2, 3, 4 })]
    public async Task DownloadAsync_RejectsTruncatedOrOversizedBodyWithoutContentLength(byte[] content)
    {
        using var http = Http((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(content))
        }));
        var error = await Assert.ThrowsAsync<VitaAttachmentException>(
            () => Client(http).DownloadAsync(Guid.NewGuid(), Guid.NewGuid(), 3, default));
        Assert.Equal("attachment_integrity_mismatch", error.Code);
    }

    [Fact]
    public void VitaAttachmentKey_IsCanonicalAndProtectedFromGenericStorage()
    {
        var owner = Guid.NewGuid();
        var id = Guid.NewGuid();
        var key = PatientDocumentStorageKeys.VitaAttachmentKey(owner, id, "pdf");
        var alias = key.Replace("/vita-attachments/", "/x/../vita-attachments/");
        DocumentArchiveStorageKeys.RequireArchiveKey(key);
        Assert.True(PatientDocumentStorageKeys.TryParseVitaAttachment(key, out var patient, out var document));
        Assert.Equal(owner, patient);
        Assert.Equal(id, document);
        Assert.False(PatientDocumentStorageKeys.IsUploadKey(key, owner));
        Assert.Throws<UnauthorizedAccessException>(() => DocumentArchiveStorageKeys.RequireGenericKey(key));
        Assert.Throws<UnauthorizedAccessException>(() => DocumentArchiveStorageKeys.RequireGenericKey(alias));
        Assert.Throws<UnauthorizedAccessException>(() => DocumentArchiveStorageKeys.RequireArchiveKey(alias));
        Assert.Throws<UnauthorizedAccessException>(() => DocumentArchiveStorageKeys.RequireArchiveKey(key + ".json"));
    }

    private static HttpClient Http(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new Transport(send)) { BaseAddress = new Uri("http://ai.test") };

    private static VitaAttachmentClient Client(HttpClient http) =>
        new(http, Options.Create(new AiServiceSettings { InternalApiKey = "synthetic-attachments-key" }));

    private sealed class Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
}
