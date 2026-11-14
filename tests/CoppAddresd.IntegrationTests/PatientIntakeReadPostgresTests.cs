using System.Text.Json;
using CoppAddresd.Api.Security;
using CoppAddresd.Application.Features.Nutrition.Queries;
using CoppAddresd.Application.Features.ProgramProgress.DTOs.Nutrition;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services;
using CoppAddresd.Domain.Entities.FoodAi;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Repositories;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    private sealed class IntakePhotoCounter(INutritionPhotoUrlSigner inner) : INutritionPhotoUrlSigner
    {
        public int Calls;
        public Task<IntakePhotoDto?> SignAsync(Guid id, string key, CancellationToken ct) { Calls++; return inner.SignAsync(id, key, ct); }
    }

    [NutritionPostgresFact]
    public async Task BandejaIntake_MenuPorFecha_FotoFirmada_OrigenCorreccion_CoordinacionSinFotos_YAjenoDenegado()
    {
        await using var db = Context(); var seed = await EffectiveSeedAsync(db); var foreign = await PatientAsync(db);
        var patient = await db.PatientProfiles.AsNoTracking().SingleAsync(p => p.Id == seed.Patient);
        var professional = NutritionProfessional(seed.Patient); db.Professionals.Add(professional);
        db.PatientProfessionalAssignments.Add(new() { PatientId = seed.Patient, ProfessionalId = professional.Id }); await db.SaveChangesAsync();
        var first = await CycleAdherencePlanAsync(db, seed.Patient, new(2026, 11, 5), new(2026, 11, 5, 15, 0, 0, DateTimeKind.Utc), active: false);
        var second = await CycleAdherencePlanAsync(db, seed.Patient, new(2026, 11, 5), new(2026, 11, 8, 2, 0, 0, DateTimeKind.Utc), first.CycleId, 2, 4);
        var photo = IntakeAnalysis(patient.UserId); photo.ImageKey = $"foodai/{photo.AnalysisId:N}.jpg";
        photo.SummaryCalories = 100; photo.SummaryProtein = 5; photo.SummaryCarbohydrates = 10; photo.SummaryFat = 4; photo.SummaryFiber = 2;
        photo.Items.Add(new FoodAnalysisItem { Id = Guid.NewGuid(), Name = "Avena", DetectionConfidence = .9, BboxWidth = 1, BboxHeight = 1,
            EstimatedGrams = 120, NutritionStatus = "available", Calories = 100, Protein = 5, Carbohydrates = 10, Fat = 4, Fiber = 2 });
        db.FoodAnalyses.Add(photo); db.HabitTemplates.Add(new() { Id = Guid.NewGuid(), Code = "cen", Name = "Cena" }); await db.SaveChangesAsync();
        var body = new NutritionIntakePayload(100m, 5m, 10m, 4m, 2m, null, "ai_photo", photo.AnalysisId);
        await EffectiveRepository(db).LogNutritionAsync(seed.Patient, MealCode.des, new(2026, 11, 6), body, patient.UserId, "intake37-first", default);
        await EffectiveRepository(db).LogNutritionAsync(seed.Patient, MealCode.cen, new(2026, 11, 7), body with { Calories = 125, Source = "manual" }, patient.UserId, "intake37-second", default);
        await AddCycleIntakesAsync(db, foreign.Id, new(2026, 11, 6), MealCode.des);
        var actor = Substitute.For<IPatientScopeActor>(); actor.UserId.Returns(patient.UserId); actor.ProfessionalIdAsync(Arg.Any<CancellationToken>()).Returns(professional.Id);
        actor.HasPermissionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var scope = new PatientScopeGuard(actor, new PatientScopeStore(db));
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() }; http.HttpContext.Request.Scheme = "https"; http.HttpContext.Request.Host = new("example.test");
        var clock = new EffectiveClock(new(2026, 11, 7, 18, 0, 0, DateTimeKind.Utc));
        var signer = new IntakePhotoCounter(new NutritionPhotoUrlSigner(Substitute.For<IObjectStorageService>(), new("clave-sintetica-test"), http, clock));
        var handler = new PatientIntakeQueryHandler(scope, actor, new PatientIntakeReadStore(db), new NutritionCycleReadStore(db), signer, clock);
        var before = await db.ActivityLogs.CountAsync();
        var result = await handler.Handle(new(seed.Patient, new(2026, 11, 6), new(2026, 11, 9)), default);
        Assert.Equal(2, result.TotalDays); Assert.Equal(new DateOnly(2026, 11, 7), result.Through);
        Assert.Equal(second.Id, result.Days[0].Menu!.PrescriptionId); Assert.Equal(first.Id, result.Days[1].Menu!.PrescriptionId);
        Assert.Equal(4, result.Days[0].Menu!.Meals.Count); Assert.Equal(3, result.Days[1].Menu!.Meals.Count);
        var corrected = Assert.Single(result.Days[0].Records); var estimated = Assert.Single(result.Days[1].Records);
        Assert.Equal("patient_confirmed", corrected.Origin); Assert.True(corrected.PatientCorrection); Assert.Equal(125m, corrected.Recorded.Calories);
        Assert.Equal("ai_estimated", estimated.Origin); Assert.Equal(100m, estimated.Analysis!.Estimated.Calories);
        Assert.Equal("Avena", Assert.Single(estimated.Analysis.Items).Name); Assert.Contains("sig=", estimated.Photo!.Url);
        Assert.Equal(clock.GetUtcNow().AddMinutes(5), estimated.Photo.ExpiresAt); Assert.Equal(1, signer.Calls);
        actor.HasPermissionAsync("Nutrition.PhotosView", Arg.Any<CancellationToken>()).Returns(false);
        var coordination = await handler.Handle(new(seed.Patient, new(2026, 11, 6), new(2026, 11, 7)), default);
        Assert.All(coordination.Days.SelectMany(d => d.Records), r => { Assert.Null(r.Photo); Assert.NotNull(r.Analysis); });
        var json = JsonSerializer.Serialize(coordination);
        Assert.DoesNotContain("foodai/", json); Assert.DoesNotContain("ImageKey", json); Assert.DoesNotContain("MaskKey", json); Assert.Equal(1, signer.Calls);
        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(new(foreign.Id, new(2026, 11, 6), new(2026, 11, 7)), default));
        Assert.Equal(1, signer.Calls);
        actor.IsAdmin.Returns(true); Assert.Single((await handler.Handle(new(foreign.Id, new(2026, 11, 6), new(2026, 11, 6)), default)).Days.Single().Records);
        Assert.Empty((await handler.Handle(new(seed.Patient, new(2026, 11, 6), new(2026, 11, 7), Page: 5), default)).Days);
        Assert.Equal(before, await db.ActivityLogs.CountAsync());
        await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(new(seed.Patient, default, default), default));
        actor.IsAdmin.Returns(false);
        actor.HasPermissionAsync("Nutrition.RecordsView", Arg.Any<CancellationToken>()).Returns(false);
        await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(new(seed.Patient, new(2026, 11, 6), new(2026, 11, 7)), default));
        actor.HasPermissionAsync("Nutrition.RecordsView", Arg.Any<CancellationToken>()).Returns(true);
        // Una etiqueta enviada por el paciente no puede atribuir autoría clínica.
        var intake = await db.NutritionIntakeLogs.SingleAsync(l => l.Id == corrected.Id);
        intake.Source = "clinical"; await db.SaveChangesAsync();
        var reserved = await handler.Handle(new(seed.Patient, new(2026, 11, 7), new(2026, 11, 7)), default);
        Assert.Equal("patient_confirmed", Assert.Single(Assert.Single(reserved.Days).Records).Origin);
        // Un vínculo histórico ajeno tampoco permite revelar análisis ni firmar su foto.
        var otherAnalysis = IntakeAnalysis(foreign.UserId); db.FoodAnalyses.Add(otherAnalysis);
        intake.FoodAnalysisId = otherAnalysis.AnalysisId; await db.SaveChangesAsync();
        actor.HasPermissionAsync("Nutrition.PhotosView", Arg.Any<CancellationToken>()).Returns(true);
        var sanitized = await handler.Handle(new(seed.Patient, new(2026, 11, 7), new(2026, 11, 7)), default);
        var row = Assert.Single(Assert.Single(sanitized.Days).Records);
        Assert.Null(row.Analysis); Assert.Null(row.Photo); Assert.Equal(1, signer.Calls);
    }
}
