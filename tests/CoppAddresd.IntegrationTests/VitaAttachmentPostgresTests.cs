using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using CoppAddresd.Api.Context;
using CoppAddresd.Api.Security;
using CoppAddresd.Application.Common;
using CoppAddresd.Application.Features.Agents.Vita;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Infrastructure.Repositories;
using CoppAddresd.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    private const string VitaAttachmentRoute = "/api/v1/vita/attachments";

    [NutritionPostgresFact]
    public async Task VitaAttachments_UploadChatConcurrentArchiveAndDownloadArePatientScoped()
    {
        await using var db = Context();
        var owner = await PatientAsync(db);
        var other = await PatientAsync(db);
        var type = await IndexDocumentTypeAsync(db);
        var pdf = new PdfDocumentBuilder();
        var font = pdf.AddStandard14Font(Standard14Font.Helvetica);
        pdf.AddPage(400, 400).AddText("Contenido sintetico Vita", 12, new PdfPoint(20, 300), font);
        using var ai = new AttachmentTransport(owner.UserId!.Value, owner.Id, pdf.Build());
        var storage = Substitute.For<IObjectStorageService>();
        storage.IsCloudStorage.Returns(true);
        var storageKey = $"documents/{owner.Id:D}/vita-attachments/{ai.Id:D}.pdf";
        storage.GetDocumentArchiveReadUrlAsync(storageKey, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns("https://storage.test/signed");
        storage.GetDocumentArchiveObjectAsync(storageKey, Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream(ai.Bytes));
        var bothWriting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writers = 0;
        storage.PutDocumentArchiveObjectAsync(storageKey, Arg.Any<Stream>(), "application/pdf", Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                using var bytes = new MemoryStream();
                await call.Arg<Stream>().CopyToAsync(bytes, call.Arg<CancellationToken>());
                Assert.Equal(ai.Bytes, bytes.ToArray());
                if (Interlocked.Increment(ref writers) == 2) bothWriting.TrySetResult();
                await bothWriting.Task.WaitAsync(TimeSpan.FromSeconds(10), call.Arg<CancellationToken>());
                return storageKey;
            });

        await using var host = await AttachmentHostAsync(ai, storage);
        using var browser = AttachmentBrowser(host, owner.UserId.Value);
        using var foreignBrowser = AttachmentBrowser(host, other.UserId!.Value);
        using var erp = AttachmentBrowser(host, owner.UserId.Value, "erp");
        using var anonymous = VitaAClient(host, attachKey: false);
        using var anonymousUpload = await anonymous.PostAsync(VitaAttachmentRoute, AttachmentForm(other.Id));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousUpload.StatusCode);
        using var erpUpload = await erp.PostAsync(VitaAttachmentRoute, AttachmentForm(other.Id));
        await AssertAttachmentErrorAsync(erpUpload, HttpStatusCode.Forbidden, "vita_identity_required");
        Assert.Equal(0, ai.Uploads);
        using var upload = await browser.PostAsync(VitaAttachmentRoute, AttachmentForm(other.Id));
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var uploaded = await upload.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ai.Id, uploaded.GetProperty("attachmentId").GetGuid());
        Assert.Equal("pdf", uploaded.GetProperty("kind").GetString());
        Assert.Equal(ai.Bytes.Length, uploaded.GetProperty("sizeBytes").GetInt32());
        Assert.Equal(6, uploaded.EnumerateObject().Count());
        Assert.Empty(await db.Documents.ToListAsync());

        var chatBody = new { message = "Consulta sintética", supportsVita = true, attachmentIds = new[] { ai.Id }, patientId = other.Id };
        using var chat = await browser.PostAsJsonAsync("/api/v1/chat", chatBody);
        Assert.Equal(HttpStatusCode.OK, chat.StatusCode);
        var chatResult = await chat.Content.ReadFromJsonAsync<JsonElement>();
        var summary = Assert.Single(chatResult.GetProperty("attachments").EnumerateArray());
        Assert.Equal(ai.Id, summary.GetProperty("attachmentId").GetGuid());
        Assert.False(summary.GetProperty("readable").GetBoolean());
        using var foreignChat = await foreignBrowser.PostAsJsonAsync("/api/v1/chat", chatBody);
        await AssertAttachmentErrorAsync(foreignChat, HttpStatusCode.NotFound, "vita_attachment_not_found");
        using var erpChat = await erp.PostAsJsonAsync("/api/v1/chat", chatBody);
        await AssertAttachmentErrorAsync(erpChat, HttpStatusCode.Forbidden, "vita_identity_required");
        using var classic = await browser.PostAsJsonAsync("/api/v1/chat", new { chatBody.message, chatBody.attachmentIds });
        await AssertAttachmentErrorAsync(classic, HttpStatusCode.Forbidden, "vita_identity_required");
        using var four = await browser.PostAsJsonAsync("/api/v1/chat", new
        {
            chatBody.message, chatBody.supportsVita, attachmentIds = Enumerable.Repeat(ai.Id, 4)
        });
        await AssertAttachmentErrorAsync(four, HttpStatusCode.UnprocessableEntity, "attachment_limit_exceeded");
        using var five = await browser.PostAsJsonAsync("/api/v1/chat", new
        {
            chatBody.message, chatBody.supportsVita, attachmentIds = Enumerable.Repeat(ai.Id, 5)
        });
        await AssertAttachmentErrorAsync(five, HttpStatusCode.BadRequest, "attachment_limit_exceeded");

        var archivePath = $"{VitaAttachmentRoute}/{ai.Id:D}/archive";
        var request = new { confirmed = true, title = "  Archivo QA  ", documentTypeId = type.Id, patientId = other.Id };
        using var denied = await browser.PostAsJsonAsync(archivePath, new { confirmed = false, request.title, request.documentTypeId });
        await AssertAttachmentErrorAsync(denied, HttpStatusCode.BadRequest, "attachment_confirmation_required");
        using var foreign = await foreignBrowser.PostAsJsonAsync(archivePath, request);
        await AssertAttachmentErrorAsync(foreign, HttpStatusCode.NotFound, "vita_attachment_not_found");
        using var missing = await browser.PostAsJsonAsync($"{VitaAttachmentRoute}/{Guid.NewGuid():D}/archive", request);
        await AssertAttachmentErrorAsync(missing, HttpStatusCode.NotFound, "vita_attachment_not_found");
        using var foreignDeletion = await foreignBrowser.DeleteAsync($"{VitaAttachmentRoute}/{ai.Id:D}");
        Assert.Equal(HttpStatusCode.NoContent, foreignDeletion.StatusCode);
        Assert.Equal(0, ai.Downloads);
        Assert.Equal(0, writers);

        // La barrera de almacenamiento garantiza dos escrituras antes del INSERT.
        var concurrent = await Task.WhenAll(browser.PostAsJsonAsync(archivePath, request),
            browser.PostAsJsonAsync(archivePath, request));
        using var first = concurrent[0];
        using var second = concurrent[1];
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Created },
            concurrent.Select(response => response.StatusCode).Order());
        using var retry = await browser.PostAsJsonAsync(archivePath, request);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var archived = await retry.Content.ReadFromJsonAsync<ArchivedVitaAttachmentDto>();
        Assert.Equal(new ArchivedVitaAttachmentDto(ai.Id, ai.Id, "Archivo QA", type.Id, "application/pdf", ai.Bytes.Length), archived);
        Assert.Equal(2, writers);
        Assert.Equal(2, ai.Downloads);
        var document = Assert.Single(await db.Documents.AsNoTracking().ToListAsync());
        Assert.Equal(storageKey, document.StorageKey);
        Assert.Equal(owner.Id, document.PatientId);
        Assert.Equal(owner.UserId, document.UploadedBy);
        Assert.Equal("Ready", document.Status);
        Assert.Empty(await db.ClinicalMeasurements.ToListAsync());

        using var index = new DocumentIndexTransport();
        using var indexHttp = new HttpClient(index, disposeHandler: false) { BaseAddress = new Uri("http://ai.test") };
        var consent = Substitute.For<ILegalConsentService>();
        Assert.True(await RunIndexAsync(indexHttp, storage, consent));
        var indexed = Assert.Single(index.Requests).Body;
        Assert.Equal("Contenido sintetico Vita", indexed.GetProperty("content").GetString());
        Assert.Equal(ai.Id, indexed.GetProperty("documentId").GetGuid());
        Assert.False(await RunIndexAsync(indexHttp, storage, consent));

        using var download = await browser.GetAsync($"/api/v1/me/documents/{ai.Id:D}/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("https://storage.test/signed", (await download.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString());
        using var foreignDownload = await foreignBrowser.GetAsync($"/api/v1/me/documents/{ai.Id:D}/download");
        Assert.Equal(HttpStatusCode.NotFound, foreignDownload.StatusCode);
        using var foreignAfter = await foreignBrowser.PostAsJsonAsync(archivePath, request);
        await AssertAttachmentErrorAsync(foreignAfter, HttpStatusCode.NotFound, "vita_attachment_not_found");

        for (var i = 0; i < 2; i++)
        {
            using var deleted = await browser.DeleteAsync($"{VitaAttachmentRoute}/{ai.Id:D}");
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }
        using var afterExpiration = await browser.PostAsJsonAsync(archivePath, request);
        Assert.Equal(HttpStatusCode.OK, afterExpiration.StatusCode);
        Assert.Equal(2, ai.Downloads);
    }

    [NutritionPostgresFact]
    public async Task VitaAttachments_HttpErrorsAndIntegrityFailuresNeverWrite()
    {
        await using var db = Context();
        var owner = await PatientAsync(db);
        var type = await IndexDocumentTypeAsync(db);
        using var ai = new AttachmentTransport(owner.UserId!.Value, owner.Id, [1, 2, 3]);
        var storage = Substitute.For<IObjectStorageService>();
        await using var host = await AttachmentHostAsync(ai, storage);
        using var browser = AttachmentBrowser(host, owner.UserId.Value);
        ai.UploadError = "attachment_mime_mismatch";
        using var invalid = await browser.PostAsync(VitaAttachmentRoute, AttachmentForm(Guid.Empty));
        await AssertAttachmentErrorAsync(invalid, HttpStatusCode.UnprocessableEntity, ai.UploadError);
        ai.UploadError = "network";
        using var offline = await browser.PostAsync(VitaAttachmentRoute, AttachmentForm(Guid.Empty));
        await AssertAttachmentErrorAsync(offline, HttpStatusCode.ServiceUnavailable, "attachment_service_unavailable");

        var path = $"{VitaAttachmentRoute}/{ai.Id:D}/archive";
        foreach (var body in new byte[][] { [1, 2], [1, 2, 4], [1, 2, 3, 4] })
        {
            ai.ContentOverride = body;
            using var result = await browser.PostAsJsonAsync(path, new ArchiveVitaAttachmentRequest(true, "Sintético", type.Id));
            await AssertAttachmentErrorAsync(result, HttpStatusCode.BadGateway, "attachment_integrity_mismatch");
        }
        Assert.Empty(await db.Documents.ToListAsync());
        Assert.Null(await new VitaDocumentIndexOutbox(db).ClaimAsync(default));
        await storage.DidNotReceiveWithAnyArgs().PutDocumentArchiveObjectAsync(default!, default!);
    }

    private Task<WebApplication> AttachmentHostAsync(AttachmentTransport ai, IObjectStorageService storage) =>
        VitaAHostAsync(configureServices: services =>
        {
            services.AddHttpContextAccessor();
            services.AddScoped<ICurrentContext, CurrentContext>();
            services.AddSingleton(Substitute.For<IScopedAuthorizationClient>());
            services.AddSingleton(storage);
            services.AddSingleton(Substitute.For<IAgentCatalogRepository>());
            services.AddSingleton(Substitute.For<IAgentRuntimeSyncService>());
            services.AddSingleton(Substitute.For<IProgramControlRepository>());
            services.Configure<AiServiceSettings>(options => options.BaseUrl = "http://ai.test");
            services.AddHttpClient<IVitaAttachmentClient, VitaAttachmentClient>().ConfigurePrimaryHttpMessageHandler(() => ai);
            services.AddHttpClient<IAiServiceClient, AiServiceClient>().ConfigurePrimaryHttpMessageHandler(() => ai);
            services.AddAuthentication("vita-attachments-test")
                .AddScheme<AuthenticationSchemeOptions, AttachmentAuthentication>("vita-attachments-test", _ => { });
        });

    private static HttpClient AttachmentBrowser(WebApplication host, Guid user, string audience = "app")
    {
        var client = VitaAClient(host, attachKey: false);
        client.DefaultRequestHeaders.Add("X-Test-Vita-Actor", user.ToString("D"));
        client.DefaultRequestHeaders.Add("X-Test-Vita-Audience", audience);
        return client;
    }

    private static MultipartFormDataContent AttachmentForm(Guid foreignPatient) => new()
    {
        { new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new MediaTypeHeaderValue("application/pdf") } }, "file", "synthetic-private-title.pdf" },
        { new StringContent("thread-qa"), "threadId" },
        { new StringContent("en"), "language" },
        { new StringContent(foreignPatient.ToString("D")), "patientId" },
        { new StringContent(Guid.NewGuid().ToString("D")), "userId" }
    };

    private static async Task AssertAttachmentErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, Assert.Single(body.EnumerateObject()).Value.GetString());
    }

    private sealed class AttachmentAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Guid.TryParse(Request.Headers["X-Test-Vita-Actor"], out var actor))
                return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new(ClaimTypes.NameIdentifier, actor.ToString("D")),
                new("aud", Request.Headers["X-Test-Vita-Audience"].ToString())], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private sealed class AttachmentTransport(Guid userId, Guid patientId, byte[] bytes) : HttpMessageHandler
    {
        private const string Endpoint = "/internal/vita/attachments";
        private int uploads;
        private int downloads;
        private bool deleted;
        public int Uploads => uploads;
        public int Downloads => downloads;
        public Guid Id { get; } = Guid.NewGuid();
        public byte[] Bytes { get; } = bytes;
        public byte[]? ContentOverride { get; set; }
        public string? UploadError { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(VitaAInternalKey, Assert.Single(request.Headers.GetValues("X-Internal-Key")));
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v1/chat")
            {
                var payload = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
                var ids = payload.GetProperty("attachment_ids").EnumerateArray().Select(value => value.GetGuid()).ToArray();
                if (ids.Length == 4)
                {
                    Assert.All(ids, id => Assert.Equal(Id, id));
                    return new(HttpStatusCode.UnprocessableEntity)
                    {
                        Content = JsonContent.Create(new { detail = new[] { new
                        {
                            type = "too_long", loc = new[] { "body", "attachment_ids" }, input = ids
                        } } })
                    };
                }
                Assert.Equal(Id, Assert.Single(ids));
                if (payload.GetProperty("patient_id").GetGuid() != patientId)
                    return Error(HttpStatusCode.NotFound, "vita_attachment_not_found");
                Assert.Equal(userId, payload.GetProperty("user_id").GetGuid());
                return new(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { answer = "QA", thread_id = "thread-qa",
                        attachments = new[] { new { attachment_id = Id, kind = "pdf", readable = false } } })
                };
            }
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal(Endpoint, path);
                Interlocked.Increment(ref uploads);
                var form = Assert.IsType<MultipartFormDataContent>(request.Content).ToDictionary(
                    part => part.Headers.ContentDisposition!.Name!.Trim('"'));
                Assert.Equal(5, form.Count);
                Assert.Equal(userId.ToString("D"), await form["userId"].ReadAsStringAsync(ct));
                Assert.Equal(patientId.ToString("D"), await form["patientId"].ReadAsStringAsync(ct));
                Assert.Equal("thread-qa", await form["threadId"].ReadAsStringAsync(ct));
                Assert.Equal("en", await form["language"].ReadAsStringAsync(ct));
                Assert.Equal("attachment.pdf", form["file"].Headers.ContentDisposition!.FileName!.Trim('"'));
                Assert.Equal("application/pdf", form["file"].Headers.ContentType!.MediaType);
                Assert.Equal(new byte[] { 1, 2, 3 }, await form["file"].ReadAsByteArrayAsync(ct));
                if (UploadError == "network") throw new HttpRequestException("detalle interno sintético");
                return UploadError is null ? Metadata(HttpStatusCode.Created) : Error(HttpStatusCode.UnprocessableEntity, UploadError);
            }

            var owns = request.RequestUri.Query == $"?patientId={patientId:D}"
                && (path == $"{Endpoint}/{Id:D}" || path == $"{Endpoint}/{Id:D}/content");
            if (request.Method == HttpMethod.Delete)
            {
                if (owns) deleted = true;
                return new(HttpStatusCode.NoContent);
            }
            if (!owns || deleted) return Error(HttpStatusCode.NotFound, "vita_attachment_not_found");
            if (path.EndsWith("/content", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref downloads);
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(ContentOverride ?? Bytes) };
            }
            return Metadata(HttpStatusCode.OK);
        }

        private HttpResponseMessage Metadata(HttpStatusCode status) => new(status)
        {
            Content = JsonContent.Create(new VitaAttachmentMetadata(Id, "pdf", "application/pdf", Bytes.Length, 1,
                Convert.ToHexString(SHA256.HashData(Bytes)), DateTimeOffset.UtcNow.AddHours(24)))
        };

        private static HttpResponseMessage Error(HttpStatusCode status, string code) => new(status)
        {
            Content = JsonContent.Create(new { detail = code, message = "Texto interno sintético" })
        };
    }
}
