using CoppAddresd.Domain.Wellness365;

namespace CoppAddresd.Application.Features.Motor365;

internal static class MotorProjection
{
    internal static MotorDayDto Day(WellnessDay day, int version) =>
        new(day.Day, day.Week, day.Mode switch { WellnessMode.Break => "break", WellnessMode.Maintain => "maint", _ => "main" },
            day.Targets.Kcal, day.Targets.Protein, day.Targets.Carbs, day.Targets.Fat, day.FiberGrams, day.WaterMl,
            day.Log.MeasuredWeightKg, day.Type, day.Training && !day.Paused, day.Paused, day.SimpleMenu, day.CalorieAdjustment,
            new(day.Exercise.Level, day.Paused ? 0 : day.Exercise.Minutes, day.Exercise.Deload,
                day.Exercise.Phase.Name.Es, Text(day.Exercise.Phase.Name),
                day.Paused ? [] : day.Exercise.Items.Select(e => new MotorExerciseItemDto(e.Id, e.Minutes,
                    e.Strength?.Sets, e.Strength?.Reps, e.Strength?.RestSeconds, e.Strength?.HoldSeconds,
                    e.Strength?.RepsInReserve)).ToArray()),
            day.Reasons.Select(Reason).ToArray(), WellnessEngine.ReferenceVersion, version);

    internal static MotorDayDto WithMenu(WellnessPlanDay plan, int version) => Day(plan.Recommendation, version) with
    {
        Menu = plan.Menu.Meals.Select(m => new MotorMealDto(m.Name.Es, Text(m.Name),
            m.Items.Select(i => new MotorFoodPortion(i.Role, i.FoodId, i.Grams)).ToArray(),
            Round(m.Totals.Kcal), Round(m.Totals.Protein), Round(m.Totals.Carbs), Round(m.Totals.Fat))).ToArray(),
        Totals = new(plan.Menu.Totals.Kcal, plan.Menu.Totals.Protein, plan.Menu.Totals.Carbs, plan.Menu.Totals.Fat),
        DeviationPct = plan.Menu.Deviation,
    };

    internal static MotorEventDto Event(WellnessEvent e, int version)
    {
        var text = e.Message.En.Contains("likely extra fat", StringComparison.Ordinal)
            ? new MotorText("Se ajustó el objetivo diario según los registros.", "The daily target changed based on your entries.")
            : Text(e.Message);
        return new(e.Day, e.Kind, e.CalorieDelta, e.RatePercent, text.Es, text.En, WellnessEngine.ReferenceVersion, version);
    }

    private static MotorText Reason(WellnessText value) => value.En switch
    {
        var s when s.StartsWith("Severe pain", StringComparison.Ordinal) || s.StartsWith("Dizziness", StringComparison.Ordinal)
            => new("La sesión de ejercicio está en pausa.", "Your exercise session is paused."),
        var s when s.StartsWith("Pain 4+", StringComparison.Ordinal)
            => new("La sesión de hoy usa ejercicios de bajo impacto.", "Today's session uses low-impact exercises."),
        var s when s.StartsWith("Insomnia", StringComparison.Ordinal)
            => new("Se tuvo en cuenta tu registro de descanso.", "Your rest entry was taken into account."),
        var s when s.StartsWith("Streak broken", StringComparison.Ordinal)
            => new("La sesión de hoy tiene una carga más ligera.", "Today's session uses a lighter workload."),
        _ => Text(value),
    };

    private static MotorText Text(WellnessText t) => new(t.Es, t.En);
    private static double Round(double n) => Math.Floor(n + .5);
}
