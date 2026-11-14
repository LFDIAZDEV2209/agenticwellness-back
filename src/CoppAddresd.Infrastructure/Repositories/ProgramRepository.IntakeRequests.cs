using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

public sealed partial class ProgramRepository
{
    private async Task EnsureIntakeAnalysisOwnedAsync(Guid patientId, Guid? actorId, Guid? analysisId, CancellationToken ct)
    {
        if (!analysisId.HasValue) return;
        if (actorId is null || !await dbContext.FoodAnalyses.AsNoTracking().AnyAsync(a => a.AnalysisId == analysisId
            && a.UserId != null && a.UserId == actorId && dbContext.PatientProfiles.Any(p => p.Id == patientId && p.UserId == actorId), ct))
            throw new NotFoundException("Análisis de comida no encontrado.");
    }

    private async Task SaveIntakeRequestAsync(Guid patientId, string? key, string? hash, Guid habitCheckId, CancellationToken ct)
    {
        if (key is null) return;
        dbContext.NutritionIntakeRequests.Add(new NutritionIntakeRequest
        { PatientId = patientId, ClientRequestId = key, RequestHash = hash!, HabitCheckId = habitCheckId });
        await dbContext.SaveChangesAsync(ct);
    }
}
