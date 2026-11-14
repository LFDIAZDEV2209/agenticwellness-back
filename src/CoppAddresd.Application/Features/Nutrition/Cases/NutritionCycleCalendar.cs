using CoppAddresd.Domain.Entities;

namespace CoppAddresd.Application.Features.Nutrition.Cases;

/// <summary>Calendario local de ciclo y continuidad; nunca depende de meses ni de pausas.</summary>
public static class NutritionCycleCalendar
{
    public static string State(NutritionCycle cycle, DateOnly today)
        => cycle.State == "en_curso" && cycle.EndDate < today ? "por_revisar" : cycle.State;

    public static DateOnly? AttributedThrough(NutritionCycle cycle, DateOnly? successorStart, DateOnly today, string timezone = "America/Bogota")
    {
        if (cycle.Number is null || cycle.StartDate is not { } start || start > today) return null;
        var end = successorStart is { } next && next <= today ? next.AddDays(-1) : today;
        if (cycle.InterruptedAt is { } interrupted)
        {
            var last = NutritionStateMachine.Today(new DateTimeOffset(DateTime.SpecifyKind(interrupted, DateTimeKind.Utc)), timezone);
            if (last < end) end = last;
        }
        return end >= start ? end : null;
    }

    public static bool IsContinuity(NutritionCycle cycle, DateOnly day)
        => cycle.EndDate is { } end && day > end;
}
