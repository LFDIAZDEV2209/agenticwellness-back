using CoppAddresd.Application.Services.ProgramProgress;

namespace CoppAddresd.Application.Features.Nutrition.Cases;

/// <summary>Versión publicada vigente en la fecha local; una versión posterior no reescribe el pasado.</summary>
public static class NutritionPublicationTimeline
{
    public static EffectivePrescription? Select(IEnumerable<EffectivePrescription> versions, Guid patientId, DateOnly day, string timezone)
        => versions.Where(p => p.PatientId == patientId && p.Start <= day
                && (p.PublishedAt is null || NutritionStateMachine.Today(new DateTimeOffset(DateTime.SpecifyKind(p.PublishedAt.Value, DateTimeKind.Utc)), timezone) <= day))
            .OrderByDescending(p => p.Start).ThenByDescending(p => p.PublishedAt)
            .ThenByDescending(p => p.Version).ThenByDescending(p => p.Id).FirstOrDefault();

    public static int MenuDay(EffectivePrescription version, DateOnly day)
        => Math.Min(day.DayNumber - version.Start.DayNumber + 1, version.End.DayNumber - version.Start.DayNumber + 1);
}
