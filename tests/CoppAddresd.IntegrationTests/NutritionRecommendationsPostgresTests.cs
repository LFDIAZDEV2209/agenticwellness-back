using System.Data.Common;
using System.Text.Json;
using CoppAddresd.Api.Security;
using CoppAddresd.Application.Features.Nutrition.Queries;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Persistence;
using CoppAddresd.Infrastructure.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    private sealed class RecommendationSql : DbCommandInterceptor
    {
        public List<string> Queries { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Queries.Add(command.CommandText); return ValueTask.FromResult(result); }
    }

    [NutritionPostgresFact]
    public async Task RecomendacionesMovil_UltimaPublicacionNoVigenciaNiNumero_DTOExacto_SoloImagenesVisibles_YPropias()
    {
        await using var db = Context(); var patient = await PatientAsync(db); var other = await PatientAsync(db); var empty = await PatientAsync(db);
        var one = await CycleAdherencePlanAsync(db, patient.Id, new(2026, 9, 1), new(2026, 9, 1, 15, 0, 0, DateTimeKind.Utc));
        var two = await CycleAdherencePlanAsync(db, patient.Id, new(2026, 10, 1), new(2026, 10, 1, 15, 0, 0, DateTimeKind.Utc));
        var three = await CycleAdherencePlanAsync(db, patient.Id, new(2026, 11, 1), new(2026, 11, 1, 15, 0, 0, DateTimeKind.Utc));
        var foreign = await CycleAdherencePlanAsync(db, other.Id, new(2026, 11, 1), new(2026, 11, 1, 15, 0, 0, DateTimeKind.Utc));
        NutritionCycleReview Review(Guid cycle, DateTime? published, string es, string en) => new()
        {
            CycleId = cycle, Revision = 1, ReviewerId = Guid.NewGuid(), ReviewedAt = new(2026, 12, 1, 15, 0, 0, DateTimeKind.Utc), PublishedAt = published,
            ProfessionalName = "Daniela Ruiz · Nutricionista", ProfessionalNotes = "SECRET_NOTES", AiDraftJson = "{\"draft\":\"RAW_AI\"}",
            SummaryJson = "{\"internal\":\"PRIVATE_SUMMARY\"}", PatientMessageJson = JsonSerializer.Serialize(new { es, en })
        };
        var first = Review(one.CycleId, new(2026, 12, 6, 15, 0, 0, DateTimeKind.Utc), "Cierre uno ES", "Closure one EN");
        var second = Review(two.CycleId, new(2026, 12, 5, 15, 0, 0, DateTimeKind.Utc), "Cierre dos ES", "Closure two EN");
        var draft = Review(three.CycleId, null, "DRAFT_MESSAGE", "DRAFT_MESSAGE");
        var alien = Review(foreign.CycleId, new(2026, 12, 9, 15, 0, 0, DateTimeKind.Utc), "FOREIGN_MESSAGE", "FOREIGN_MESSAGE");
        NutritionCycleAttachment File(NutritionCycleReview review, bool visible, string kind, string caption)
        {
            var file = new NutritionCycleAttachment { ReviewId = review.Id, Kind = kind,
                ContentType = kind == "image" ? "image/jpeg" : "application/pdf", Length = 100, VisibleToPatient = visible, Caption = caption };
            file.StorageKey = $"documents/{patient.Id:D}/{file.Id:D}/original.{(kind == "image" ? "jpg" : "pdf")}";
            return file;
        }
        first.Attachments.Add(File(first, true, "image", "Imagen aprobada")); first.Attachments.Add(File(first, false, "image", "INTERNAL_ATTACHMENT"));
        first.Attachments.Add(File(first, true, "document", "VISIBLE_DOCUMENT_EXCLUDED"));
        second.Attachments.Add(File(second, true, "image", "Anterior")); draft.Attachments.Add(File(draft, true, "image", "DRAFT_ATTACHMENT"));
        db.Set<NutritionCycleReview>().AddRange(first, second, draft, alien); await db.SaveChangesAsync();
        var actor = Substitute.For<IPatientScopeActor>(); actor.UserId.Returns(patient.UserId); // Sin permisos ERP ni profesional.
        var signatures = new StorageSignatureService("clave-sintetica-recomendaciones"); var clock = new EffectiveClock(new(2026, 12, 10, 18, 0, 0, DateTimeKind.Utc));
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() }; http.HttpContext.Request.Scheme = "https"; http.HttpContext.Request.Host = new("example.test");
        var recorder = new RecommendationSql();
        await using var readingDb = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).AddInterceptors(recorder).Options);
        var handler = new MyNutritionRecommendationsHandler(new PatientScopeGuard(actor, new PatientScopeStore(readingDb)),
            new NutritionCycleReadStore(readingDb), new NutritionRecommendationStore(readingDb), new NutritionAttachmentUrlSigner(signatures, http), clock);
        var audits = await db.ActivityLogs.CountAsync();
        var result = await handler.Handle(new(patient.UserId!.Value), default);
        Assert.Equal(1, result.Current!.CycleNumber); Assert.Equal("Cierre uno ES", result.Current.PatientMessage); // Ciclo terminado y número menor.
        Assert.Equal("Daniela Ruiz", result.Current.ProfessionalName); Assert.All(result.Previous, r => Assert.Equal("Daniela Ruiz", r.ProfessionalName));
        Assert.Equal(2, Assert.Single(result.Previous).CycleNumber); Assert.Equal("Imagen aprobada", Assert.Single(result.Current.Attachments).Caption);
        Assert.Equal(3, recorder.Queries.Count);
        var sql = string.Join('\n', recorder.Queries);
        foreach (var column in new[] { "professional_notes", "ai_draft_json", "summary_json", "storage_key" }) Assert.DoesNotContain(column, sql);
        var json = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(new[] { "current", "previous" }, json.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(new[] { "cycleNumber", "startDate", "endDate", "publishedAt", "professionalName", "patientMessage", "attachments" },
            json.GetProperty("current").EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(new[] { "url", "caption" }, json.GetProperty("current").GetProperty("attachments")[0].EnumerateObject().Select(p => p.Name).ToArray());
        foreach (var secret in new[] { "SECRET_NOTES", "RAW_AI", "PRIVATE_SUMMARY", "INTERNAL_ATTACHMENT", "VISIBLE_DOCUMENT_EXCLUDED", "FOREIGN_MESSAGE", "DRAFT_MESSAGE", "DRAFT_ATTACHMENT", "reviewId", "aiDraft", "summary", "program" })
            Assert.DoesNotContain(secret, json.GetRawText());
        var url = new Uri(result.Current.Attachments[0].Url); var query = QueryHelpers.ParseQuery(url.Query);
        var attachmentId = first.Attachments.Single(a => a.VisibleToPatient && a.Kind == "image").Id;
        Assert.True(signatures.ValidateNutritionAttachment(attachmentId, query["sig"], query["exp"], clock.GetUtcNow()));
        Assert.False(signatures.ValidateNutritionAttachment(attachmentId, query["sig"], query["exp"], clock.GetUtcNow().AddMinutes(6)));
        var english = (await handler.Handle(new(patient.UserId.Value, "en"), default)).Current!;
        Assert.Equal("Closure one EN", english.PatientMessage); Assert.Equal("Daniela Ruiz", english.ProfessionalName);
        Assert.Equal("Daniela Ruiz · Nutricionista", (await db.Set<NutritionCycleReview>().AsNoTracking().SingleAsync(r => r.Id == first.Id)).ProfessionalName);
        actor.IsAdmin.Returns(true);
        await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(new(other.UserId!.Value), default));
        actor.UserId.Returns(other.UserId); Assert.Equal("FOREIGN_MESSAGE", (await handler.Handle(new(other.UserId!.Value), default)).Current!.PatientMessage);
        actor.UserId.Returns(empty.UserId); var noRecommendation = await handler.Handle(new(empty.UserId!.Value), default);
        Assert.Null(noRecommendation.Current); Assert.Empty(noRecommendation.Previous);
        Assert.Equal(audits, await db.ActivityLogs.CountAsync());
        empty.DeletedAt = clock.GetUtcNow().UtcDateTime; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(new(empty.UserId.Value), default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.Handle(new(empty.UserId.Value), cancelled.Token));
        await actor.DidNotReceive().HasPermissionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
