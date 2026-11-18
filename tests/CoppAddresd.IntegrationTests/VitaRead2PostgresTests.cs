using System.Net;
using System.Net.Http.Json;
using CoppAddresd.Api.Security;
using CoppAddresd.Application.Features.Agents.Vita;
using CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent;
using CoppAddresd.Application.Features.ProgramProgress.DTOs.Nutrition;
using CoppAddresd.Application.Features.ProgramProgress.Events;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Entities.HealthTests;
using CoppAddresd.Domain.Enums.HealthTests;
using CoppAddresd.Domain.Enums.ProgramProgress;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    [NutritionPostgresFact]
    public async Task VitaRead2_Nutrition_ExposesThirtyPublishedDaysRecommendationsAndOwnHistory()
    {
        await using var db = Context();
        var seed = await EffectiveSeedAsync(db);
        var patient = await db.PatientProfiles.SingleAsync(p => p.Id == seed.Patient);
        var other = await PatientAsync(db);
        var cycle = new NutritionCycle
        {
            PatientId = patient.Id, State = "en_curso", Number = 1,
            StartDate = VitaAToday.AddDays(-10), EndDate = VitaAToday.AddDays(19),
        };
        db.NutritionCycles.Add(cycle);
        var food = new NutritionFoodItem { Id = "vita-qa-food", Name = "Alimento de prueba", FoodGroup = "P", Calories100 = 200 };
        db.NutritionFoodItems.Add(food);
        var published = new NutritionPrescription
        {
            PatientId = patient.Id, CycleId = cycle.Id, State = "aprobado", IsPublished = true, Revision = 2,
            CreatedBy = patient.UserId!.Value, ApprovedBy = patient.UserId, ApprovedAt = VitaANow.UtcDateTime,
            StartDate = cycle.StartDate, EndDate = cycle.EndDate,
            Meals = Enumerable.Range(1, 30).Select(day => new NutritionPrescriptionMeal
            {
                DayNumber = day, MealType = "des", ScheduledTime = new TimeOnly(7, 0), MenuName = $"Menú {day}",
                Items = [new NutritionPrescriptionItem { FoodItemId = food.Id, Grams = 100, SortOrder = 1 }],
            }).ToList(),
        };
        db.NutritionPrescriptions.Add(published);
        db.NutritionPrescriptions.Add(new()
        {
            CycleId = cycle.Id, PatientId = patient.Id, Version = 2, Revision = 99, CreatedBy = patient.UserId.Value,
        });
        db.Set<NutritionCycleReview>().Add(new()
        {
            CycleId = cycle.Id, Revision = 1, ReviewerId = patient.UserId.Value, ReviewedAt = VitaANow.UtcDateTime,
            PublishedAt = VitaANow.UtcDateTime, PatientMessageJson = """{"es":"Mensaje publicado"}""",
            ProfessionalNotes = "Nota privada", ProfessionalName = "Equipo QA",
        });
        await db.SaveChangesAsync();
        var historyDate = VitaAToday.AddDays(-2);
        await EffectiveRepository(db).LogNutritionAsync(patient.Id, MealCode.des, historyDate,
            new NutritionIntakePayload(420m, null, null, null, null, null, "manual", null), patient.UserId.Value, default);

        await using var host = await VitaAHostAsync();
        using var client = VitaAClient(host);
        using var response = await client.GetAsync($"{VitaARoute}/nutrition?userId={patient.UserId}&date={VitaAToday:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var data = await response.Content.ReadFromJsonAsync<VitaNutritionDto>();
        Assert.Equal(published.Id, data!.Plan!.Id);
        Assert.Equal(2, data.Plan.Revision);
        Assert.Equal(30, data.Plan.Days.Count);
        Assert.Equal(cycle.StartDate, data.Plan.Days[0].Date);
        Assert.Equal(cycle.EndDate, data.Plan.Days[^1].Date);
        Assert.Equal("Menú 30", Assert.Single(data.Plan.Days[^1].Meals).Description);
        Assert.Equal("Mensaje publicado", Assert.Single(data.Recommendations).PatientMessage);
        Assert.Equal(420m, Assert.Single(data.History).Calories);
        Assert.Null(data.History[0].ProteinG);
        var empty = await client.GetFromJsonAsync<VitaNutritionDto>($"{VitaARoute}/nutrition?userId={other.UserId}");
        Assert.Null(empty!.Plan);
        Assert.Empty(empty.Recommendations);
        Assert.Empty(empty.History);
        using var future = await client.GetAsync($"{VitaARoute}/nutrition?userId={patient.UserId}&date=2099-01-01");
        Assert.Equal(HttpStatusCode.BadRequest, future.StatusCode);
    }

    [NutritionPostgresFact]
    public async Task VitaRead2_Assessments_ReturnsAuthorizedSnapshotAndHidesPendingScores()
    {
        await using var db = Context();
        var patient = await PatientAsync(db);
        var other = await PatientAsync(db);
        var finished = await BatteryAsync(db, patient.Id, true);
        var pending = await BatteryAsync(db, patient.Id, false);
        await BatteryAsync(db, other.Id, true);
        var evaluation = await db.HealthTestEvaluations.SingleAsync(e => e.AssignmentId == finished.Id);
        evaluation.Score = 42;
        db.Set<HealthTestResult>().Add(new HealthTestResult
        {
            Id = Guid.NewGuid(), EvaluationId = evaluation.Id, Code = "dimension", Label = "Dimensión", Value = 10, Qualifier = "privado",
            ResultType = HealthTestResultType.subscale, Severity = HealthTestSeverity.moderate,
        });
        db.HealthTestEvaluations.Add(new HealthTestEvaluation
        {
            Id = Guid.NewGuid(), PatientId = patient.Id, AssignmentId = pending.Id, VersionId = pending.VersionId,
            Status = HealthTestEvaluationStatus.completed, CompletedAt = VitaANow.UtcDateTime, Score = 999,
        });
        var emptyPatient = await PatientAsync(db);

        await using var host = await VitaAHostAsync();
        using var client = VitaAClient(host);
        var result = await client.GetFromJsonAsync<VitaAssessmentsDto>($"{VitaARoute}/assessments?userId={patient.UserId}");
        Assert.Equal(2, result!.Batteries.Count);
        var test = Assert.Single(Assert.Single(result.Batteries, b => b.Id == finished.BatteryAssignmentId).Tests);
        Assert.Equal(42, test.Score);
        Assert.Equal("moderate", Assert.Single(test.Results, r => r.Code == "nutrition_habits").Severity);
        Assert.Null(Assert.Single(test.Results, r => r.Code == "dimension").Qualifier);
        Assert.Empty(Assert.Single(result.Batteries, b => b.Id == pending.BatteryAssignmentId).Tests);
        var empty = await client.GetFromJsonAsync<VitaAssessmentsDto>($"{VitaARoute}/assessments?userId={emptyPatient.UserId}");
        Assert.Empty(empty!.Batteries);
    }

    [NutritionPostgresFact]
    public async Task VitaRead2_Documents_ListsOwnMetadataAndIssuesShortSignatureOnlyToOwner()
    {
        await using var db = Context();
        var patient = await PatientAsync(db);
        var other = await PatientAsync(db);
        var type = new ClinicalDocumentType { Id = Guid.NewGuid(), Code = "vita-test-report", Name = "Informe",
            Category = new DocumentCategory { Id = Guid.NewGuid(), Code = "vita-test-category", Name = "Documentos" } };
        var own = new Document { Id = Guid.NewGuid(), PatientId = patient.Id, Title = "Informe QA", DocumentType = type };
        own.StorageKey = $"documents/{patient.Id:D}/{own.Id:D}/original.pdf";
        var foreign = new Document { Id = Guid.NewGuid(), PatientId = other.Id, Title = "Ajeno", DocumentType = type, StorageKey = "qa/foreign.pdf" };
        var deleted = new Document { Id = Guid.NewGuid(), PatientId = patient.Id, Title = "Borrado", DocumentType = type,
            StorageKey = "qa/deleted.pdf", DeletedAt = VitaANow.UtcDateTime };
        db.Documents.AddRange(own, foreign, deleted);
        await db.SaveChangesAsync();
        var noDocuments = await PatientAsync(db);
        await using var host = await VitaAHostAsync();
        using var client = VitaAClient(host);
        var result = await client.GetFromJsonAsync<VitaDocumentsDto>($"{VitaARoute}/documents?userId={patient.UserId}");
        Assert.Equal(own.Id, Assert.Single(result!.Items).Id);
        Assert.Equal("Informe", result.Items[0].Type);
        var empty = await client.GetFromJsonAsync<VitaDocumentsDto>($"{VitaARoute}/documents?userId={noDocuments.UserId}");
        Assert.Empty(empty!.Items);
        var access = await client.GetFromJsonAsync<VitaDocumentReadDto>($"{VitaARoute}/documents/{own.Id}?userId={patient.UserId}");
        Assert.Equal(120, access!.ExpiresInSeconds);
        var url = new Uri(client.BaseAddress!, access.Url);
        var query = QueryHelpers.ParseQuery(url.Query);
        var signatures = host.Services.GetRequiredService<StorageSignatureService>();
        Assert.True(signatures.Validate(own.StorageKey, query["sig"], query["exp"], VitaANow));
        Assert.False(signatures.Validate(own.StorageKey, query["sig"], query["exp"], VitaANow.AddSeconds(121)));
        foreach (var id in new[] { foreign.Id, deleted.Id })
        {
            using var response = await client.GetAsync($"{VitaARoute}/documents/{id}?userId={patient.UserId}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [NutritionPostgresFact]
    public async Task VitaRead2_ProgramCapture_CancelsAndReplaysWithoutDuplicatingRowsOrEvents()
    {
        await using var db = Context();
        var seed = await EffectiveSeedAsync(db);
        var patient = await db.PatientProfiles.SingleAsync(p => p.Id == seed.Patient);
        var week = await db.ProgramWeeks.SingleAsync(w => w.EnrollmentId == seed.Enrollment && w.WeekNumber == 1);
        // Conserva una segunda tarea pendiente para que el check-in no cierre el día.
        var day = seed.Start.AddDays(2);
        week.TasksSnapshot = WeekSnapshotJson.Serialize([
            new WeekSnapshotTaskRow(3, "emocional", 10, 1), new WeekSnapshotTaskRow(3, "podcast", 10, 2),
        ]);
        await db.SaveChangesAsync();
        var metrics = Substitute.For<IProgramMetricsQueue>();
        await using var host = await VitaAHostAsync(captureEnabled: true, metrics: metrics);
        using var client = VitaAClient(host);
        var observedAt = new DateTimeOffset(day.ToDateTime(new TimeOnly(14, 0), DateTimeKind.Utc));
        var meal = new ProposeVitaCaptureRequest("record_meal", "vita_whatsapp", "comida-1", observedAt,
            Meal: new VitaMealCapture("des", 420m, 22m));
        var checkin = new ProposeVitaCaptureRequest("record_checkin", "vita_chat", "checkin-1", observedAt, MoodScore: 4);
        var mealProposal = await ProposeCaptureAsync(client, patient.UserId!.Value, meal);
        var checkInProposal = await ProposeCaptureAsync(client, patient.UserId.Value, checkin);
        Assert.Equal(day, mealProposal.Data.Program!.LocalDate);
        Assert.Equal(seed.Enrollment, checkInProposal.Data.Program!.EnrollmentId);
        foreach (var (input, token) in new[] { (meal, mealProposal.Token), (checkin, checkInProposal.Token) })
        {
            using var cancellation = await SendConfirmCaptureAsync(client, patient.UserId.Value, input, token, confirmed: false);
            Assert.Equal(HttpStatusCode.BadRequest, cancellation.StatusCode);
        }
        Assert.Equal(0, await db.VitaWriteReceipts.CountAsync());
        Assert.Equal(0, await db.NutritionIntakeLogs.CountAsync());
        Assert.Equal(0, await db.DailyCheckIns.CountAsync());

        // El runtime descarta la propuesta rechazada y emite otra al nuevo pedido explícito.
        meal = meal with { TurnId = "comida-2" };
        checkin = checkin with { TurnId = "checkin-2" };
        mealProposal = await ProposeCaptureAsync(client, patient.UserId.Value, meal);
        checkInProposal = await ProposeCaptureAsync(client, patient.UserId.Value, checkin);
        var mealSaved = await ConfirmCaptureAsync(client, patient.UserId.Value, meal, mealProposal.Token);
        var checkInSaved = await ConfirmCaptureAsync(client, patient.UserId.Value, checkin, checkInProposal.Token);
        Assert.Equal(mealSaved, await ConfirmCaptureAsync(client, patient.UserId.Value, meal, mealProposal.Token));
        Assert.Equal(checkInSaved, await ConfirmCaptureAsync(client, patient.UserId.Value, checkin, checkInProposal.Token));
        var changedMeal = meal with { Meal = meal.Meal! with { Calories = 500m } };
        var changedMealProposal = await ProposeCaptureAsync(client, patient.UserId.Value, changedMeal);
        using var conflict = await SendConfirmCaptureAsync(client, patient.UserId.Value, changedMeal, changedMealProposal.Token);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var repeatedCheckIn = checkin with { TurnId = "checkin-3", MoodScore = 1 };
        var repeatedCheckInProposal = await ProposeCaptureAsync(client, patient.UserId.Value, repeatedCheckIn);
        using var duplicate = await SendConfirmCaptureAsync(client, patient.UserId.Value, repeatedCheckIn, repeatedCheckInProposal.Token);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("nutrition_intake_log", mealSaved.EntityType);
        Assert.Equal("daily_checkin", checkInSaved.EntityType);
        var intake = await db.NutritionIntakeLogs.AsNoTracking().SingleAsync();
        Assert.Equal(mealSaved.EntityId, intake.Id);
        Assert.Equal("vita_whatsapp", intake.Source);
        Assert.Equal(420m, intake.Calories);
        var check = await db.DailyCheckIns.AsNoTracking().SingleAsync();
        Assert.Equal(checkInSaved.EntityId, check.Id);
        Assert.Equal((short)4, check.MoodScore);
        Assert.Equal("vita_chat", (await db.TaskCompletions.AsNoTracking().SingleAsync()).SourceRefType);
        Assert.Equal(2, await db.VitaWriteReceipts.CountAsync());
        await metrics.Received(1).EnqueueAsync(Arg.Is<TaskCompletedMetricEvent>(e => e.TaskCode == "emocional"), Arg.Any<CancellationToken>());
        await metrics.Received(1).EnqueueAsync(Arg.Is<XpAwardedMetricEvent>(e => e.Reason == "NUTRITION_des"), Arg.Any<CancellationToken>());
    }
}
