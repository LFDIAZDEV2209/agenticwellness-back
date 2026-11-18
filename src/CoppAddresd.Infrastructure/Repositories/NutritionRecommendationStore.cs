using CoppAddresd.Application.Features.Nutrition.Queries;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

/// <summary>Proyección explícita: no carga notas internas, resumen, borrador IA ni claves de archivos.</summary>
public sealed class NutritionRecommendationStore(AppDbContext db) : INutritionRecommendationStore
{
    public async Task<RecommendationReadData> ReadAsync(Guid patientId, CancellationToken ct)
    {
        var headers = await PublishedHeaders(patientId).ToArrayAsync(ct);
        var ids = headers.Select(r => r.ReviewId).ToArray();
        var files = await db.Set<NutritionCycleAttachment>().AsNoTracking()
            .Where(a => ids.Contains(a.ReviewId) && a.VisibleToPatient && a.Kind == "image")
            .OrderBy(a => a.Id).Select(a => new RecommendationReadAttachment(a.ReviewId, a.Id, a.Caption)).ToArrayAsync(ct);
        return new(headers, files);
    }

    public async Task<IReadOnlyList<RecommendationReadHeader>> ReadPublishedHeadersAsync(Guid patientId, int limit, CancellationToken ct)
        => await PublishedHeaders(patientId).Take(limit).ToArrayAsync(ct);

    private IQueryable<RecommendationReadHeader> PublishedHeaders(Guid patientId) =>
        from r in db.Set<NutritionCycleReview>().AsNoTracking()
        join c in db.NutritionCycles on r.CycleId equals c.Id
        where c.PatientId == patientId && r.PublishedAt != null && c.Number != null && c.StartDate != null && c.EndDate != null
        orderby r.PublishedAt descending, r.Id descending
        select new RecommendationReadHeader(r.Id, c.Number!.Value, c.StartDate!.Value, c.EndDate!.Value,
            r.PublishedAt!.Value, r.ProfessionalName, r.PatientMessageJson);
}
