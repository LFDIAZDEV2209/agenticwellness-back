using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CoppAddresd.Api.Context;
using CoppAddresd.Api.Controllers;
using CoppAddresd.Application.Common;
using CoppAddresd.Application.DTOs.LabExam;
using CoppAddresd.Application.Features.Documents;
using CoppAddresd.Application.Features.LabExam;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Persistence;
using CoppAddresd.Infrastructure.Repositories;
using CoppAddresd.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    [NutritionPostgresFact]
    public async Task VitaDocumentIndex_HostedServiceDispatchesCommittedUploads()
    {
        await using var db = Context();
        var patient = await PatientAsync(db);
        var type = await IndexDocumentTypeAsync(db);
        var storage = Substitute.For<IObjectStorageService>();
        storage.GetObjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
            new MemoryStream(Encoding.UTF8.GetBytes("Texto sintético para worker")));
        using var transport = new DocumentIndexTransport();
        await using var host = await VitaAHostAsync(documentIndexingEnabled: true, configureServices: services =>
        {
            services.AddSingleton(storage);
            services.Configure<AiServiceSettings>(x => x.BaseUrl = "http://ai.test");
            services.AddHttpClient<VitaDocumentIndexClient>().ConfigurePrimaryHttpMessageHandler(() => transport);
        });

        var document = new Document
        {
            PatientId = patient.Id, DocumentTypeId = type.Id, Title = "QA worker",
            StorageKey = $"documents/{patient.Id:D}/{Guid.NewGuid():N}.txt",
        };
        await new DocumentRepository(db).AddAsync(document);
        var indexed = await transport.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(document.Id, indexed.Body.GetProperty("documentId").GetGuid());
        Assert.Equal(patient.Id, indexed.Body.GetProperty("patientId").GetGuid());
        Assert.Equal("Texto sintético para worker", indexed.Body.GetProperty("content").GetString());
    }

    [NutritionPostgresFact]
    public async Task VitaDocumentIndex_HttpUploadRetriesAndArchivingOrDeletingRemovesTheFamily()
    {
        await using var db = Context();
        var patient = await PatientAsync(db);
        var other = await PatientAsync(db);
        var type = await IndexDocumentTypeAsync(db);
        var actor = Substitute.For<ICurrentContext>();
        actor.UserId.Returns(patient.UserId);
        actor.HasPermissionAsync(Arg.Is<string>(x => x.StartsWith("Documents.", StringComparison.Ordinal)), Arg.Any<CancellationToken>())
            .Returns(true);
        var storage = Substitute.For<IObjectStorageService>();
        storage.GetPreSignedUploadUrlAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<TimeSpan>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>()).Returns("http://storage.test/upload");
        storage.GetObjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
            new MemoryStream(Encoding.UTF8.GetBytes("Informe sintético de indexación")));

        await using var host = await VitaAHostAsync(configureServices: services =>
        {
            services.AddSingleton(actor);
            services.AddSingleton(storage);
            services.AddAuthentication("document-index-test")
                .AddScheme<AuthenticationSchemeOptions, DocumentIndexAuthentication>("document-index-test", _ => { });
        });
        using var browser = VitaAClient(host);
        browser.DefaultRequestHeaders.Add("X-Test-Document-Actor", patient.UserId!.Value.ToString());
        using var transport = new DocumentIndexTransport { FailNextPost = true };
        using var aiHttp = new HttpClient(transport, disposeHandler: false) { BaseAddress = new Uri("http://ai.test") };
        var consent = Substitute.For<ILegalConsentService>();

        var rootKey = await UploadIntentAsync();
        using var foreign = await browser.PostAsJsonAsync("/api/v1/documents", Request($"documents/{other.Id:D}/{Guid.NewGuid():N}.txt"));
        Assert.Equal(HttpStatusCode.Conflict, foreign.StatusCode);
        var root = await RegisterAsync(rootKey);
        Assert.Empty(transport.Requests);
        await storage.DidNotReceiveWithAnyArgs().GetObjectAsync(default!, default);

        Assert.True(await RunIndexAsync(aiHttp, storage, consent));
        Assert.Equal(root.Id, Assert.Single(transport.Requests).Body.GetProperty("documentId").GetGuid());
        Assert.True(await db.Documents.AnyAsync(x => x.Id == root.Id && x.Status == "Ready"));
        Assert.False(await RunIndexAsync(aiHttp, storage, consent));
        await MakeIndexDueAsync(db, root.Id);
        Assert.True(await RunIndexAsync(aiHttp, storage, consent));
        var sent = transport.Requests[^1];
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("/internal/vita/documents/index", sent.Endpoint);
        Assert.Equal("synthetic-index-key", sent.InternalKey);
        Assert.Equal(patient.Id, sent.Body.GetProperty("patientId").GetGuid());
        Assert.Equal(root.Id, sent.Body.GetProperty("documentId").GetGuid());
        Assert.Equal("Informe sintético de indexación", sent.Body.GetProperty("content").GetString());
        Assert.Equal("text/plain", sent.Body.GetProperty("contentType").GetString());
        Assert.Equal("active", sent.Body.GetProperty("status").GetString());
        Assert.Equal(DateTimeKind.Utc, sent.Body.GetProperty("documentDate").GetDateTime().Kind);
        Assert.Equal(transport.Requests[0].Body.GetRawText(), sent.Body.GetRawText());

        await ChangeAsync(root.Id, new("Informe corregido", null, null, null));
        Assert.True(await RunIndexAsync(aiHttp, storage, consent));
        Assert.Equal("Informe corregido", transport.Requests[^1].Body.GetProperty("title").GetString());
        var child = await RegisterAsync(await UploadIntentAsync(root.Id), root.Id);
        Assert.True(await RunIndexAsync(aiHttp, storage, consent));

        await ChangeAsync(root.Id, new(null, null, null, "Archived"));
        await ProcessFamilyAsync(HttpMethod.Delete);
        await ChangeAsync(root.Id, new(null, null, null, "Ready"));
        await ProcessFamilyAsync(HttpMethod.Post);
        using var deletion = await browser.DeleteAsync($"/api/v1/documents/{root.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deletion.StatusCode);
        await ProcessFamilyAsync(HttpMethod.Delete);
        Assert.True(await db.Documents.Where(d => d.Id == root.Id || d.Id == child.Id).AllAsync(d => d.DeletedAt != null));
        await storage.DidNotReceive().GetObjectAsync(Arg.Is<string>(key => key.Contains(other.Id.ToString())), Arg.Any<CancellationToken>());

        async Task<string> UploadIntentAsync(Guid? rootId = null)
        {
            using var result = await browser.PostAsJsonAsync("/api/v1/documents/upload-intent",
                new CreateDocumentUploadIntentRequest(patient.Id, type.Id, "informe.txt", rootId, "text/plain"));
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            return (await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("storageKey").GetString()!;
        }

        CreateDocumentRequest Request(string key, Guid? rootId = null) =>
            new(rootId is null ? patient.Id : null, null, type.Id, "Informe QA", null, key, "text/plain", 40, rootId);

        async Task<DocumentDto> RegisterAsync(string key, Guid? rootId = null)
        {
            using var response = await browser.PostAsJsonAsync("/api/v1/documents", Request(key, rootId));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<DocumentDto>())!;
        }

        async Task ChangeAsync(Guid documentId, UpdateDocumentRequest change)
        {
            using var response = await browser.PutAsJsonAsync($"/api/v1/documents/{documentId}", change);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        async Task ProcessFamilyAsync(HttpMethod method)
        {
            Assert.True(await RunIndexAsync(aiHttp, storage, consent));
            Assert.True(await RunIndexAsync(aiHttp, storage, consent));
            Assert.False(await RunIndexAsync(aiHttp, storage, consent));
            var requests = transport.Requests.TakeLast(2).ToArray();
            Assert.All(requests, request => Assert.Equal(method, request.Method));
            if (method == HttpMethod.Delete)
            {
                Assert.Single(requests, x => x.Endpoint == $"/internal/vita/documents/{root.Id:D}?patientId={patient.Id:D}");
                Assert.Single(requests, x => x.Endpoint == $"/internal/vita/documents/{child.Id:D}?patientId={patient.Id:D}");
            }
        }
    }

    [NutritionPostgresFact]
    public async Task VitaDocumentIndex_LabArchiveExtractsTheOriginalAndWaitsForConsent()
    {
        await using var db = Context();
        var patient = await PatientAsync(db);
        if (!await db.ClinicalDocumentTypes.AnyAsync(x => x.Code == "OTHER"))
            await IndexDocumentTypeAsync(db, "OTHER");
        var pdf = new PdfDocumentBuilder();
        var font = pdf.AddStandard14Font(Standard14Font.Helvetica);
        pdf.AddPage(PageSize.A4).AddText("Informe sintetico PDF", 12, new PdfPoint(40, 760), font);
        var bytes = pdf.Build();
        using var original = new MemoryStream(bytes);
        var storage = Substitute.For<IObjectStorageService>();
        var objects = new Dictionary<string, byte[]>();
        storage.PutDocumentArchiveObjectAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                using var destination = new MemoryStream();
                await call.Arg<Stream>().CopyToAsync(destination);
                var key = call.ArgAt<string>(0);
                objects[key] = destination.ToArray();
                return key;
            });
        storage.GetDocumentArchiveObjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new MemoryStream(objects[call.Arg<string>()]));
        var compression = Substitute.For<IFileCompressionService>();
        compression.CompressAsync(Arg.Any<Stream>(), "application/pdf", Arg.Any<CancellationToken>())
            .Returns(_ => ((Stream)new MemoryStream(bytes), "application/pdf"));
        var extractor = Substitute.For<IAiServiceClient>();
        extractor.ExtractLabMetricsAsync(patient.Id, Arg.Any<Guid>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new LabExamAiResponse("Resultado sintético", []));

        var upload = new UploadLabExamCommandHandler(compression, storage, extractor, new ClinicalMeasurementRepository(db),
            new PatientRepository(db), new DocumentRepository(db), NullLogger<UploadLabExamCommandHandler>.Instance);
        var recorded = await upload.Handle(new(original, "informe.pdf", "application/pdf", bytes.Length,
            patient.UserId!.Value, null, "es"), default);
        var docId = recorded.DocumentId!.Value;
        Assert.Equal(2, objects.Count);

        using var transport = new DocumentIndexTransport();
        using var aiHttp = new HttpClient(transport, disposeHandler: false) { BaseAddress = new Uri("http://ai.test") };
        var consent = Substitute.For<ILegalConsentService>();
        var allowed = false;
        consent.EnsurePatientAsync(patient.Id, "chat", Arg.Any<CancellationToken>()).Returns(_ => allowed
            ? Task.CompletedTask : Task.FromException(new AiConsentException("CONSENT_REQUIRED", "chat", 403)));
        Assert.True(await RunIndexAsync(aiHttp, storage, consent));
        Assert.Empty(transport.Requests);
        await storage.DidNotReceiveWithAnyArgs().GetDocumentArchiveObjectAsync(default!, default);
        allowed = true;
        await MakeIndexDueAsync(db, docId);
        Assert.True(await RunIndexAsync(aiHttp, storage, consent));
        var index = Assert.Single(transport.Requests);
        Assert.Equal(docId, index.Body.GetProperty("documentId").GetGuid());
        Assert.Equal("Informe sintetico PDF", index.Body.GetProperty("content").GetString());
        await storage.Received(1).GetDocumentArchiveObjectAsync(recorded.StorageKey!, Arg.Any<CancellationToken>());
        await storage.DidNotReceive().GetDocumentArchiveObjectAsync(recorded.ExtractionStorageKey!, Arg.Any<CancellationToken>());
    }

    [NutritionPostgresFact]
    public async Task VitaDocumentIndex_CommitIsAtomicAndAnOlderLeaseCannotAcknowledgeArchiving()
    {
        await using var writer = Context();
        await using var worker = Context();
        var patient = await PatientAsync(writer);
        var type = await IndexDocumentTypeAsync(writer);
        var document = new Document
        {
            PatientId = patient.Id, DocumentTypeId = type.Id, Title = "QA transacción",
            StorageKey = $"documents/{patient.Id:D}/{Guid.NewGuid():N}.txt",
        };
        var outbox = new VitaDocumentIndexOutbox(worker);
        await using (var tx = await writer.Database.BeginTransactionAsync())
        {
            await new DocumentRepository(writer).AddAsync(document);
            Assert.Null(await outbox.ClaimAsync(default));
            await tx.CommitAsync();
        }

        var lease = Assert.IsType<VitaDocumentIndexJob>(await outbox.ClaimAsync(default));
        document.Status = "Archived";
        await writer.SaveChangesAsync();
        Assert.Null(await new VitaDocumentIndexOutbox(writer).ClaimAsync(default));
        Assert.False(await outbox.IsCurrentAsync(lease, default));
        await outbox.CompleteAsync(lease, default);

        using var transport = new DocumentIndexTransport();
        using var aiHttp = new HttpClient(transport, disposeHandler: false) { BaseAddress = new Uri("http://ai.test") };
        var storage = Substitute.For<IObjectStorageService>();
        Assert.True(await RunIndexAsync(aiHttp, storage, Substitute.For<ILegalConsentService>()));
        Assert.Equal(HttpMethod.Delete, Assert.Single(transport.Requests).Method);
        await storage.DidNotReceiveWithAnyArgs().GetObjectAsync(default!, default);
        Assert.Null(await outbox.ClaimAsync(default));
    }

    private async Task<bool> RunIndexAsync(HttpClient http, IObjectStorageService storage, ILegalConsentService consent)
    {
        await using var db = Context();
        var ai = new VitaDocumentIndexClient(http, Options.Create(new AiServiceSettings { InternalApiKey = "synthetic-index-key" }));
        return await new VitaDocumentIndexProcessor(db, new(db), new(storage), ai, consent,
            NullLogger<VitaDocumentIndexProcessor>.Instance).ProcessNextAsync(default);
    }

    private static Task MakeIndexDueAsync(AppDbContext db, Guid docId) => db.Database.ExecuteSqlInterpolatedAsync($"""
        UPDATE app.vita_document_index_outbox SET next_attempt_at = now() WHERE document_id = {docId}
        """);

    private static async Task<ClinicalDocumentType> IndexDocumentTypeAsync(AppDbContext db, string code = "VITAQA")
    {
        var documentType = new ClinicalDocumentType
        {
            Code = code, Name = "Documento QA", IsActive = true, AllowedExtensions = ["txt", "pdf"],
            Category = new DocumentCategory { Code = "VITAQA", Name = "Categoría QA" },
        };
        db.ClinicalDocumentTypes.Add(documentType);
        await db.SaveChangesAsync();
        return documentType;
    }

    private sealed record DocumentIndexRequest(HttpMethod Method, string Endpoint, string InternalKey, JsonElement Body);

    private sealed class DocumentIndexTransport : HttpMessageHandler
    {
        public List<DocumentIndexRequest> Requests { get; } = [];
        public bool FailNextPost { get; set; }
        public TaskCompletionSource<DocumentIndexRequest> FirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? default : await request.Content.ReadFromJsonAsync<JsonElement>(ct);
            var captured = new DocumentIndexRequest(request.Method, request.RequestUri!.PathAndQuery,
                request.Headers.GetValues("X-Internal-Key").Single(), body);
            Requests.Add(captured);
            FirstRequest.TrySetResult(captured);
            if (request.Method == HttpMethod.Delete) return new(HttpStatusCode.NoContent);
            if (!FailNextPost) return new(HttpStatusCode.OK);
            FailNextPost = false;
            return new(HttpStatusCode.ServiceUnavailable);
        }
    }

    private sealed class DocumentIndexAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Guid.TryParse(Request.Headers["X-Test-Document-Actor"], out var actor))
                return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actor.ToString())], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
