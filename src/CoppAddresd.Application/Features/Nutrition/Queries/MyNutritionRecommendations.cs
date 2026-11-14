using System.Text.Json;
using CoppAddresd.Application.Features.Nutrition.Commands;
using CoppAddresd.Application.Interfaces;
using MediatR;

namespace CoppAddresd.Application.Features.Nutrition.Queries;

public sealed record RecommendationAttachmentDto(string Url, string Caption);
public sealed record RecommendationDto(int CycleNumber, DateOnly StartDate, DateOnly EndDate, DateTime PublishedAt,
    string ProfessionalName, string PatientMessage, IReadOnlyList<RecommendationAttachmentDto> Attachments);
public sealed record NutritionRecommendationsDto(RecommendationDto? Current, IReadOnlyList<RecommendationDto> Previous);
public sealed record RecommendationReadHeader(Guid ReviewId, int CycleNumber, DateOnly StartDate, DateOnly EndDate,
    DateTime PublishedAt, string ProfessionalName, string PatientMessageJson);
public sealed record RecommendationReadAttachment(Guid ReviewId, Guid AttachmentId, string? Caption);
public sealed record RecommendationReadData(IReadOnlyList<RecommendationReadHeader> Reviews, IReadOnlyList<RecommendationReadAttachment> Attachments);
public interface INutritionRecommendationStore
{
    Task<RecommendationReadData> ReadAsync(Guid patientId, CancellationToken ct);
}
/// <summary>UserId exclusivo del JWT; la ruta no acepta IDs del paciente ni del ciclo.</summary>
public sealed record GetMyNutritionRecommendations(Guid UserId, string Language = "es") : IRequest<NutritionRecommendationsDto>;
public sealed class MyNutritionRecommendationsHandler(IPatientScopeGuard scope, INutritionCycleReadStore reading,
    INutritionRecommendationStore store, INutritionAttachmentUrlSigner urls, TimeProvider clock)
    : IRequestHandler<GetMyNutritionRecommendations, NutritionRecommendationsDto>
{
    public async Task<NutritionRecommendationsDto> Handle(GetMyNutritionRecommendations request, CancellationToken ct)
    {
        var patient = await scope.EnsureSelfAsync(request.UserId, ct);
        var data = await reading.ReadAsync(readCt => store.ReadAsync(patient, readCt), ct);
        var attachments = data.Attachments.ToLookup(a => a.ReviewId);
        var expiry = clock.GetUtcNow().AddMinutes(5);
        var rows = new List<RecommendationDto>();
        foreach (var review in data.Reviews)
        {
            ct.ThrowIfCancellationRequested();
            var message = JsonSerializer.Deserialize<CyclePatientMessage>(review.PatientMessageJson, CycleReviewHandler.Json);
            var text = request.Language == "en" ? message?.En ?? message?.Es : message?.Es ?? message?.En;
            // Cabeceras históricas podían incluir «nombre · rol»; el rol lo presenta y traduce la app.
            var professionalName = review.ProfessionalName.Split('·', 2)[0].Trim();
            rows.Add(new(review.CycleNumber, review.StartDate, review.EndDate, review.PublishedAt, professionalName, text ?? "",
                attachments[review.ReviewId].Select(a => new RecommendationAttachmentDto(urls.Sign(a.AttachmentId, expiry), a.Caption ?? "")).ToArray()));
        }
        return new(rows.FirstOrDefault(), rows.Skip(1).ToArray());
    }
}
