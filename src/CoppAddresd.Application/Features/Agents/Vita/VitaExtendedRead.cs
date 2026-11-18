using System.Text.Json;
using CoppAddresd.Application.Features.Nutrition.Commands;
using CoppAddresd.Application.Features.Nutrition.Queries;
using CoppAddresd.Application.Features.Nutrition.Queries.GetMyNutritionPlan;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Exceptions;
using FluentValidation;
using FluentValidation.Results;
using MediatR;

namespace CoppAddresd.Application.Features.Agents.Vita;

public sealed record VitaNutritionDayDto(DateOnly Date, string Status, IReadOnlyList<MyNutritionPlanMealDto> Meals);
public sealed record VitaNutritionPlanDto(Guid Id, int? Revision, DateOnly StartDate, DateOnly EndDate, IReadOnlyList<VitaNutritionDayDto> Days);
public sealed record VitaNutritionRecommendationDto(int CycleNumber, DateTime PublishedAt, string PatientMessage);
public sealed record VitaNutritionHistoryDto(DateOnly Date, string MealCode, decimal? Calories, decimal? ProteinG,
    decimal? CarbsG, decimal? FatG, decimal? FiberG, int? WaterMl, string Source);
public sealed record VitaNutritionDto(DateOnly Date, VitaNutritionPlanDto? Plan,
    IReadOnlyList<VitaNutritionRecommendationDto> Recommendations, IReadOnlyList<VitaNutritionHistoryDto> History);

public sealed record VitaAssessmentResultDto(string ResultType, string Code, string Label, decimal Value,
    string? Qualifier, string? Severity);
public sealed record VitaAssessmentTestDto(string TestName, string TestCode, DateTime CompletedAt,
    decimal? Score, decimal? ScorePercentage, IReadOnlyList<VitaAssessmentResultDto> Results);
public sealed record VitaAssessmentBatteryDto(Guid Id, string Name, string Status, DateTime? DueDate, DateTime? CompletedAt,
    IReadOnlyList<VitaAssessmentTestDto> Tests);
public sealed record VitaAssessmentsDto(IReadOnlyList<VitaAssessmentBatteryDto> Batteries);

public sealed record VitaDocumentDto(Guid Id, string Name, string Type, DateTime Date, string Status);
public sealed record VitaDocumentsDto(IReadOnlyList<VitaDocumentDto> Items, int Total, int Page, int PageSize);
public sealed record VitaDocumentReadDto(VitaDocumentDto Document, string Url, int ExpiresInSeconds);

public interface IVitaExtendedReadStore
{
    Task<IReadOnlyList<VitaNutritionHistoryDto>> NutritionHistoryAsync(Guid patientId, DateOnly from, DateOnly to, CancellationToken ct);
    Task<VitaAssessmentsDto> AssessmentsAsync(Guid patientId, CancellationToken ct);
}

public interface IVitaDocumentLink
{
    Task<string> CreateAsync(Document document, TimeSpan lifetime, CancellationToken ct);
}

public sealed record GetVitaNutritionQuery(Guid UserId, DateOnly? Date) : IRequest<VitaNutritionDto>;
public sealed record GetVitaAssessmentsQuery(Guid UserId) : IRequest<VitaAssessmentsDto>;
public sealed record GetVitaDocumentsQuery(Guid UserId, int Page, int PageSize) : IRequest<VitaDocumentsDto>;
public sealed record GetVitaDocumentQuery(Guid UserId, Guid Id) : IRequest<VitaDocumentReadDto>;

public sealed class VitaExtendedReadQueries(VitaPatientAccess access, INutritionPrescriptionRepository prescriptions,
    INutritionRecommendationStore recommendations, IVitaExtendedReadStore store)
    : IRequestHandler<GetVitaNutritionQuery, VitaNutritionDto>, IRequestHandler<GetVitaAssessmentsQuery, VitaAssessmentsDto>
{
    public async Task<VitaNutritionDto> Handle(GetVitaNutritionQuery request, CancellationToken ct)
    {
        var patient = await access.RequireAsync(request.UserId, "chat", ct);
        var today = access.Today(patient.Timezone);
        var date = request.Date ?? today;
        if (date > today || date.DayNumber < 30)
            throw new ValidationException([new ValidationFailure("date", "VITA_NUTRITION_INVALID_DATE")]);

        var prescription = await prescriptions.GetCurrentPublishedAsync(patient.Id, date, ct);
        VitaNutritionPlanDto? plan = null;
        if (prescription is { StartDate: { } start, EndDate: { } end })
        {
            var composed = GetMyNutritionPlanQueryHandler.Compose(prescription, date, ct);
            var indexed = composed.Days.ToDictionary(x => x.DayNumber);
            var days = Enumerable.Range(0, Math.Min(30, end.DayNumber - start.DayNumber + 1)).Select(offset =>
                indexed.GetValueOrDefault(offset + 1) is { Meals.Count: > 0 } menu
                    ? new VitaNutritionDayDto(start.AddDays(offset), "planned", menu.Meals)
                    : new VitaNutritionDayDto(start.AddDays(offset), "unavailable", [])).ToArray();
            plan = new(composed.Id, composed.Revision, start, end, days);
        }

        var headers = await recommendations.ReadPublishedHeadersAsync(patient.Id, 12, ct);
        var published = headers.Select(h =>
        {
            var message = JsonSerializer.Deserialize<CyclePatientMessage>(h.PatientMessageJson, CycleReviewHandler.Json);
            return new VitaNutritionRecommendationDto(h.CycleNumber, h.PublishedAt, message?.Es ?? message?.En ?? "");
        }).ToArray();
        var history = await store.NutritionHistoryAsync(patient.Id, date.AddDays(-29), date, ct);
        return new(date, plan, published, history);
    }

    public async Task<VitaAssessmentsDto> Handle(GetVitaAssessmentsQuery request, CancellationToken ct)
    {
        var patient = await access.RequireAsync(request.UserId, "chat", ct);
        return await store.AssessmentsAsync(patient.Id, ct);
    }
}

public sealed class VitaDocumentsQueries(VitaPatientAccess access, IDocumentRepository documents, IVitaDocumentLink links)
    : IRequestHandler<GetVitaDocumentsQuery, VitaDocumentsDto>, IRequestHandler<GetVitaDocumentQuery, VitaDocumentReadDto>
{
    public async Task<VitaDocumentsDto> Handle(GetVitaDocumentsQuery request, CancellationToken ct)
    {
        var patient = await access.RequireAsync(request.UserId, "chat", ct);
        if (request.Page is < 1 or > 10000 || request.PageSize is < 1 or > 50)
            throw new ValidationException([new ValidationFailure("page", "VITA_DOCUMENTS_INVALID_PAGE")]);
        var (items, total) = await documents.ListLatestAsync(patient.Id, null, null, null, null, null,
            request.Page, request.PageSize, ct);
        return new(items.Select(ToDto).ToArray(), total, request.Page, request.PageSize);
    }

    public async Task<VitaDocumentReadDto> Handle(GetVitaDocumentQuery request, CancellationToken ct)
    {
        var patient = await access.RequireAsync(request.UserId, "chat", ct);
        var doc = await documents.GetByIdAsync(request.Id, ct);
        if (doc is null || doc.PatientId != patient.Id || doc.DeletedAt is not null
            || doc.Status is not ("Ready" or "Archived") || string.IsNullOrWhiteSpace(doc.StorageKey))
            throw new NotFoundException("VITA_DOCUMENT_UNAVAILABLE");

        const int seconds = 120;
        return new(ToDto(doc), await links.CreateAsync(doc, TimeSpan.FromSeconds(seconds), ct), seconds);
    }

    private static VitaDocumentDto ToDto(Document doc) => new(doc.Id, doc.Title, doc.DocumentType.Name, doc.CreatedAt, doc.Status);
}
