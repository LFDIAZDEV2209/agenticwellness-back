using System.Text.Json;
using CoppAddresd.Application.Features.Nutrition.Cases;
using CoppAddresd.Application.Features.Nutrition.Commands;
using CoppAddresd.Application.Features.ProgramProgress.DTOs.Nutrition;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Repositories;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    [NutritionPostgresFact]
    public async Task CierreCiclo_PasosObligatorios_IAConfirmada_PlanSiguienteAtomico_AuditoriaYResumenCongelado()
    {
        await using var db = Context();
        await db.GetService<IMigrator>().MigrateAsync("20261114194459_AddNutritionCycleHighlights"); await db.GetService<IMigrator>().MigrateAsync();
        var seed = await EffectiveSeedAsync(db); var other = await PatientAsync(db);
        var owner = await db.PatientProfiles.Where(p => p.Id == seed.Patient).Select(p => p.UserId!.Value).SingleAsync();
        var professional = NutritionProfessional(seed.Patient); professional.Employee.UserId = owner; db.Professionals.Add(professional);
        db.PatientProfessionalAssignments.Add(new() { PatientId = seed.Patient, ProfessionalId = professional.Id }); await db.SaveChangesAsync();
        var actor = Substitute.For<IPatientScopeActor>(); actor.UserId.Returns(owner); actor.ProfessionalIdAsync(Arg.Any<CancellationToken>()).Returns(professional.Id);
        actor.HasPermissionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var scope = new PatientScopeGuard(actor, new PatientScopeStore(db));
        var early = Cases(db, time: new EffectiveClock(new(2026, 11, 2, 18, 0, 0, DateTimeKind.Utc)));
        var nutritionActor = new NutritionActor(owner, professional.Id, false, true, true, true);
        var first = await early.ExecuteAsync(new(NutritionOperation.Generate, seed.Patient, null, null, nutritionActor, CompleteNutritionInput), default);
        first = await early.ExecuteAsync(Mutation(first, NutritionOperation.Submit, nutritionActor), default);
        first = await early.ExecuteAsync(Mutation(first, NutritionOperation.Approve, nutritionActor, confirm: true), default);
        var clock = new EffectiveClock(new(2026, 12, 3, 18, 0, 0, DateTimeKind.Utc)); var cases = Cases(db, time: clock);
        var next = await cases.ExecuteAsync(new(NutritionOperation.Generate, seed.Patient, null, null, nutritionActor, CompleteNutritionInput), default);
        next = await cases.ExecuteAsync(Mutation(next, NutritionOperation.Submit, nutritionActor), default);
        var foreign = await CycleAdherencePlanAsync(db, other.Id, seed.Start, new(2026, 11, 2, 15, 0, 0, DateTimeKind.Utc));
        var body = new NutritionIntakePayload(100, null, null, null, null, null, "manual", null);
        await EffectiveRepository(db).LogNutritionAsync(seed.Patient, MealCode.des, seed.Start, body, owner, "review39", default);
        var store = new NutritionCycleReviewStore(db, EffectiveRepository(db));
        var handler = new CycleReviewHandler(scope, actor, store, new NutritionCycleReadStore(db), Substitute.For<INutritionAttachmentUrlSigner>(), cases, clock);
        var review = await handler.Handle(new SaveNutritionCycleReview(first.CycleId, 0, null, null), default);
        Assert.Equal(1, review.Revision); Assert.Equal(JsonValueKind.Null, review.Summary.GetProperty("averages").GetProperty("proteinG").ValueKind);
        PublishNutritionCycleReview Publish(int revision) => new(first.CycleId, revision, next.Id, next.Revision);
        await Assert.ThrowsAsync<UnprocessableEntityException>(() => handler.Handle(Publish(review.Revision), default));
        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(new PublishNutritionCycleReview(first.CycleId, review.Revision, foreign.Id, 1), default));
        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(new GetNutritionCycleReview(foreign.CycleId), default));
        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(new SaveNutritionCycleReview(foreign.CycleId, 0, "No", new("No", "No")), default));
        var message = new CyclePatientMessage("Mantén un registro diario.", "Keep a daily log.");
        review = await handler.Handle(new SaveNutritionCycleReview(first.CycleId, review.Revision, "Evaluación interna", message), default);
        await Assert.ThrowsAsync<UnprocessableEntityException>(() => handler.Handle(Publish(review.Revision), default));
        await db.Set<NutritionCycleReview>().Where(r => r.CycleId == first.CycleId).ExecuteUpdateAsync(s => s.SetProperty(r => r.AiDraftJson, "{\"draft\":\"Pendiente\"}"));
        review = await handler.Handle(new SaveNutritionCycleReview(first.CycleId, review.Revision, "Evaluación interna", message, true), default);
        await Assert.ThrowsAsync<UnprocessableEntityException>(() => handler.Handle(Publish(review.Revision), default));
        review = await handler.Handle(new SaveNutritionCycleReview(first.CycleId, review.Revision, "Evaluación interna revisada", message, true, true), default);
        await Assert.ThrowsAsync<BusinessRuleViolationException>(() => handler.Handle(new SaveNutritionCycleReview(first.CycleId, review.Revision - 1, "Obsoleto", message), default));
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION app.reject_cycle_review_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
              IF NEW.table_name = 'nutrition_cycle_reviews' AND NEW.metadata->>'event' = 'NutritionCycleReviewPublished' THEN
                RAISE EXCEPTION 'test rollback cierre';
              END IF; RETURN NEW; END $$;
            CREATE TRIGGER reject_cycle_review_audit BEFORE INSERT ON audit.activity_logs FOR EACH ROW EXECUTE FUNCTION app.reject_cycle_review_audit();
            """);
        await Assert.ThrowsAsync<PostgresException>(() => handler.Handle(Publish(review.Revision), default));
        Assert.False(await db.NutritionPrescriptions.AsNoTracking().Where(p => p.Id == next.Id).Select(p => p.IsPublished).SingleAsync());
        Assert.Null(await db.Set<NutritionCycleReview>().AsNoTracking().Where(r => r.CycleId == first.CycleId).Select(r => r.PublishedAt).SingleAsync());
        Assert.Empty(await db.AppNotifications.Where(n => n.Type == "nutrition_cycle_review").ToArrayAsync());
        Assert.Equal(1, await db.NutritionCycles.CountAsync(c => c.PatientId == seed.Patient && c.Number != null));
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_cycle_review_audit ON audit.activity_logs; DROP FUNCTION app.reject_cycle_review_audit();");
        var published = await handler.Handle(Publish(review.Revision), default);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, published.PublishedAt); Assert.True(published.AiDraftConfirmed);
        Assert.Equal("revisado", await db.NutritionCycles.Where(c => c.Id == first.CycleId).Select(c => c.State).SingleAsync());
        Assert.True(await db.NutritionPrescriptions.Where(p => p.Id == next.Id).Select(p => p.IsPublished).SingleAsync());
        Assert.Equal(2, await db.NutritionCycles.Where(c => c.Id == next.CycleId).Select(c => c.Number).SingleAsync());
        Assert.Equal(new DateOnly(2026, 12, 3), await db.NutritionCycles.Where(c => c.Id == next.CycleId).Select(c => c.StartDate).SingleAsync());
        Assert.Equal("2026-12-02", published.Summary.GetProperty("adherence").GetProperty("through").GetString());
        var audit = await db.ActivityLogs.SingleAsync(a => a.TableName == "nutrition_cycle_reviews" && a.Metadata!.Value.GetProperty("event").GetString() == "NutritionCycleReviewPublished");
        Assert.Equal(owner, audit.UserId); Assert.NotEqual(default, audit.OccurredAt);
        var replay = await handler.Handle(Publish(review.Revision), default); Assert.Equal(published.Revision, replay.Revision);
        Assert.Equal(1, await db.AppNotifications.CountAsync(n => n.Type == "nutrition_cycle_review"));
        Assert.Equal(1, await db.NotificationDedupeKeys.CountAsync(k => k.DedupeKey == $"nutrition-cycle:{first.CycleId:D}:review-published"));
        var frozen = (await store.LoadAsync(first.CycleId, default))!.SummaryJson;
        await EffectiveRepository(db).UpdateNutritionIntakeAsync(seed.Patient, MealCode.des, seed.Start, body with { Calories = 250 }, owner, default);
        Assert.Equal(frozen, await db.Set<NutritionCycleReview>().AsNoTracking().Where(r => r.CycleId == first.CycleId).Select(r => r.SummaryJson).SingleAsync());
        await Assert.ThrowsAsync<BusinessRuleViolationException>(() => handler.Handle(new SaveNutritionCycleReview(first.CycleId, published.Revision, "Cambio", message), default));
        actor.HasPermissionAsync("Nutrition.NotesView", Arg.Any<CancellationToken>()).Returns(false);
        actor.HasPermissionAsync("Nutrition.PhotosView", Arg.Any<CancellationToken>()).Returns(false);
        var redacted = await handler.Handle(new GetNutritionCycleReview(first.CycleId), default); Assert.Null(redacted!.ProfessionalNotes); Assert.Null(redacted.AiDraft); Assert.Empty(redacted.Attachments);
        actor.HasPermissionAsync("Nutrition.Approve", Arg.Any<CancellationToken>()).Returns(false);
        await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(Publish(published.Revision), default));
        await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync("20261114194459_AddNutritionCycleHighlights"));
    }

    [NutritionPostgresFact]
    public async Task AdjuntosCiclo_ValidacionPrivada_RevisionOptimista_LimpiezaTrasRollback_YAjenoDenegado()
    {
        await using var db = Context(); var seed = await EffectiveSeedAsync(db); var other = await PatientAsync(db);
        var owner = await db.PatientProfiles.Where(p => p.Id == seed.Patient).Select(p => p.UserId!.Value).SingleAsync();
        var professional = NutritionProfessional(seed.Patient); db.Professionals.Add(professional);
        db.PatientProfessionalAssignments.Add(new() { PatientId = seed.Patient, ProfessionalId = professional.Id }); await db.SaveChangesAsync();
        var plan = await CycleAdherencePlanAsync(db, seed.Patient, seed.Start, new(2026, 11, 2, 15, 0, 0, DateTimeKind.Utc));
        var foreign = await CycleAdherencePlanAsync(db, other.Id, seed.Start, new(2026, 11, 2, 15, 0, 0, DateTimeKind.Utc));
        var actor = Substitute.For<IPatientScopeActor>(); actor.UserId.Returns(owner); actor.ProfessionalIdAsync(Arg.Any<CancellationToken>()).Returns(professional.Id);
        actor.HasPermissionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var scope = new PatientScopeGuard(actor, new PatientScopeStore(db)); var clock = new EffectiveClock(new(2026, 11, 7, 18, 0, 0, DateTimeKind.Utc));
        var store = new NutritionCycleReviewStore(db, EffectiveRepository(db)); var urls = Substitute.For<INutritionAttachmentUrlSigner>();
        urls.Sign(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>()).Returns(call => $"https://example.test/attachment/{call.Arg<Guid>()}?signed=test");
        var reviews = new CycleReviewHandler(scope, actor, store, new NutritionCycleReadStore(db), urls, Cases(db, time: clock), clock);
        var review = await reviews.Handle(new SaveNutritionCycleReview(plan.CycleId, 0, "En preparación", null), default);
        var files = new Dictionary<string, byte[]>(); var storage = Substitute.For<IObjectStorageService>();
        storage.PutDocumentArchiveObjectAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(async call => { using var stream = new MemoryStream(); await call.Arg<Stream>().CopyToAsync(stream); var key = call.ArgAt<string>(0); files[key] = stream.ToArray(); return key; });
        storage.DeleteDocumentArchiveObjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => { files.Remove(call.Arg<string>()); return Task.CompletedTask; });
        var handler = new CycleAttachmentHandler(scope, actor, store, storage, urls, clock, NullLogger<CycleAttachmentHandler>.Instance);
        byte[] png = [137, 80, 78, 71, 13, 10, 26, 10, 0];
        UploadNutritionCycleAttachment Upload(Guid cycle, int revision) => new(cycle, revision, new MemoryStream(png), "foto.png", "image/png", png.Length, "Ejemplo", true);
        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(Upload(foreign.CycleId, 0), default)); Assert.Empty(files);
        await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(Upload(plan.CycleId, review.Revision) with { FileName = "foto.svg" }, default)); Assert.Empty(files);
        var added = await handler.Handle(Upload(plan.CycleId, review.Revision), default);
        Assert.Equal(review.Revision + 1, added.Revision); Assert.Equal(1, await db.Set<NutritionCycleAttachment>().CountAsync());
        var key = Assert.Single(files).Key; Assert.StartsWith($"documents/{seed.Patient:D}/{added.Attachment.Id:D}/original.", key);
        Assert.DoesNotContain("documents/", JsonSerializer.Serialize(added));
        await Assert.ThrowsAsync<BusinessRuleViolationException>(() => handler.Handle(Upload(plan.CycleId, review.Revision), default)); Assert.Single(files);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION app.reject_cycle_attachment_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
              IF NEW.table_name = 'nutrition_cycle_reviews' AND NEW.metadata->>'event' = 'NutritionCycleAttachmentAdded' THEN RAISE EXCEPTION 'test rollback adjunto'; END IF;
              RETURN NEW; END $$;
            CREATE TRIGGER reject_cycle_attachment_audit BEFORE INSERT ON audit.activity_logs FOR EACH ROW EXECUTE FUNCTION app.reject_cycle_attachment_audit();
            """);
        await Assert.ThrowsAsync<PostgresException>(() => handler.Handle(Upload(plan.CycleId, added.Revision), default));
        Assert.Single(files); Assert.Equal(1, await db.Set<NutritionCycleAttachment>().CountAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_cycle_attachment_audit ON audit.activity_logs; DROP FUNCTION app.reject_cycle_attachment_audit();");
        var removed = await handler.Handle(new DeleteNutritionCycleAttachment(plan.CycleId, added.Attachment.Id, added.Revision), default);
        Assert.Equal(added.Revision + 1, removed); Assert.Empty(files); Assert.Empty(await db.Set<NutritionCycleAttachment>().ToArrayAsync());
        actor.HasPermissionAsync("Nutrition.PhotosView", Arg.Any<CancellationToken>()).Returns(false);
        await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(Upload(plan.CycleId, removed), default)); Assert.Empty(files);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.Handle(Upload(plan.CycleId, removed), cancelled.Token));
    }
}
