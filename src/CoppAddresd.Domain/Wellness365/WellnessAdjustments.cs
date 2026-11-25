namespace CoppAddresd.Domain.Wellness365;

internal static class WellnessAdjustments
{
    internal static WellnessEvent Weekly(WellnessGoal goal, ReadOnlySpan<WellnessDay> fortnight,
        double waistChange, double cap, ref int hunger, WellnessParameters parameters)
    {
        var end = fortnight[^1];
        var current = Measurements(fortnight[7..]);
        var previous = Measurements(fortnight[..7]);
        if (current.Count < parameters.WeighMin || previous.Count < 3)
        {
            return new(end.Day, "sin", 0, null,
                new($"Semana {end.Week}: pesajes insuficientes (<{parameters.WeighMin}): no se ajusta.",
                    $"Week {end.Week}: too few weigh-ins (<{parameters.WeighMin}): no change."));
        }

        var currentWeight = current.Total / current.Count;
        var previousWeight = previous.Total / previous.Count;
        var rate = (currentWeight - previousWeight) / previousWeight * 100;
        double adherenceSum = 0;
        foreach (var day in fortnight[7..]) adherenceSum += day.Log.Adherence;
        var (delta, message) = RateDecision(goal, end.Mode, rate, adherenceSum / 7, parameters);

        var step = parameters.Step;
        if (delta == -step && goal != WellnessGoal.GainMuscle && waistChange <= -.8)
        {
            delta = 0;
            var change = WellnessRules.FixedText(Math.Abs(waistChange), 1);
            message = new($"Peso lento pero la cintura bajó {change} cm en 2 sem (recomposición): no se recorta.",
                $"Weight slow but waist down {change} cm in 2 wks (recomposition): no cut.");
        }
        if (goal == WellnessGoal.GainMuscle && end.Mode == WellnessMode.Main && delta >= 0 && waistChange >= 1.2)
        {
            delta = -step;
            var change = WellnessRules.FixedText(waistChange, 1);
            message = new($"Cintura +{change} cm en 2 sem: probable grasa extra → −{step} kcal.",
                $"Waist +{change} cm in 2 wks: likely extra fat → −{step} kcal.");
        }
        if (delta == -step && hunger >= 2)
        {
            delta = 0;
            message = new("Hambre excesiva reportada: no se baja más; se evalúa pausa.",
                "Excess hunger reported: no further cut; break is evaluated.");
        }
        hunger = 0;

        if (Math.Abs(end.CalorieAdjustment + delta) > cap)
        {
            delta = 0;
            message = new(message.Es + " (tope de ajuste alcanzado)", message.En + " (adjustment cap reached)");
        }
        return new(end.Day, delta == 0 ? "sin" : "ajuste", delta, WellnessRules.Fixed(rate, 2),
            new($"Semana {end.Week}: {message.Es}", $"Week {end.Week}: {message.En}"));
    }

    private static (int Count, double Total) Measurements(ReadOnlySpan<WellnessDay> days)
    {
        var count = 0;
        double total = 0;
        foreach (var day in days)
        {
            if (day.Log.MeasuredWeightKg is not { } weight) continue;
            count++;
            total += weight;
        }
        return (count, total);
    }

    private static (int Delta, WellnessText Reason) RateDecision(WellnessGoal goal, WellnessMode mode,
        double rate, double adherence, WellnessParameters parameters)
    {
        var step = parameters.Step;
        if (mode == WellnessMode.Break)
            return (0, new("Semana de pausa: sin ajuste.", "Break week: no adjustment."));
        if (adherence < parameters.AdhMin)
        {
            var percent = WellnessRules.Round(adherence * 100);
            var min = WellnessRules.Round(parameters.AdhMin * 100);
            return (0, new($"Adherencia {percent}% < {min}%: no se ajusta; primero cumplir el plan.",
                $"Adherence {percent}% < {min}%: no change; follow the plan first."));
        }

        var value = WellnessRules.FixedText(rate, 2);
        if (mode == WellnessMode.Maintain)
        {
            if (rate < -.25)
                return (step, new($"Mantener: bajó {value}%/sem → +{step} kcal.",
                    $"Maintain: dropped {value}%/wk → +{step} kcal."));
            if (rate > .25)
                return (-step, new($"Mantener: subió {value}%/sem → −{step} kcal.",
                    $"Maintain: rose {value}%/wk → −{step} kcal."));
            return (0, new($"Mantener: peso estable ({value}%/sem).", $"Maintain: weight steady ({value}%/wk)."));
        }

        var band = parameters.Band.For(goal);
        if (goal == WellnessGoal.GainMuscle)
        {
            if (rate < band[0])
                return (step, new($"Ritmo {value}%/sem bajo la banda → +{step} kcal.",
                    $"Rate {value}%/wk below band → +{step} kcal."));
            if (rate > band[1])
                return (-step, new($"Ritmo {value}%/sem sobre la banda → −{step} kcal (evitar grasa).",
                    $"Rate {value}%/wk above band → −{step} kcal (limit fat gain)."));
        }
        else
        {
            if (rate > band[1])
                return (-step, new($"Ritmo {value}%/sem más lento que la banda → −{step} kcal.",
                    $"Rate {value}%/wk slower than band → −{step} kcal."));
            if (rate < band[0])
                return (step, new($"Ritmo {value}%/sem más rápido que la banda → +{step} kcal (proteger músculo).",
                    $"Rate {value}%/wk faster than band → +{step} kcal (protect muscle)."));
        }

        return (0, new($"Ritmo {value}%/sem dentro de la banda: se mantiene.", $"Rate {value}%/wk in band: kept."));
    }
}
