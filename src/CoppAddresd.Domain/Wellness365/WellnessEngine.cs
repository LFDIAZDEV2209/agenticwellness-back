namespace CoppAddresd.Domain.Wellness365;

public static class WellnessEngine
{
    public const int PlanLength = 365;
    public const string ReferenceVersion = "5.2";
    public const int ReferenceParametersVersion = 1;

    public static double BasalMetabolicRate(WellnessProfile profile, double weightKg) =>
        10 * weightKg + 6.25 * profile.HeightCm - 5 * profile.Age +
        (profile.Sex == WellnessSex.Male ? 5 : -161);

    public static int CalorieFloor(WellnessProfile profile) => profile.Sex == WellnessSex.Male ? profile.Parameters.FloorMale : profile.Parameters.FloorFemale;

    public static double BodyMassIndex(WellnessProfile profile, double weightKg) =>
        weightKg / Math.Pow(profile.HeightCm / 100, 2);

    public static WellnessBlock? CheckBlock(WellnessProfile profile)
    {
        if (profile.Age < 18)
            return new("age", new("Esta versión es para mayores de 18 años.", "This version is for adults (18+)."));
        if (BodyMassIndex(profile, profile.InitialWeightKg) < 18.5 && profile.Goal != WellnessGoal.GainMuscle)
            return new("bmi", new("Con tu IMC actual no se recomienda un objetivo de bajar o definir.",
                "With your current BMI a lose/cut goal is not recommended."));
        return null;
    }

    public static int EstimateWeeks(WellnessProfile profile)
    {
        if (profile.TargetWeightKg is not { } target)
            throw new ArgumentException("La estimación necesita un peso meta.", nameof(profile));
        var tdee = BasalMetabolicRate(profile, profile.InitialWeightKg) *
                   WellnessRules.ActivityMultiplier(profile.ActivityLevel);
        var dailyDelta = tdee * WellnessRules.TargetShare(profile);
        var kgPerWeek = dailyDelta * 7 / (profile.Goal == WellnessGoal.GainMuscle ? 6500 : 7700);
        return (int)WellnessRules.Round(Math.Abs(target - profile.InitialWeightKg) / Math.Max(kgPerWeek, .05));
    }

    public static WellnessPlanDay PlanDay(WellnessProfile profile, int day, IReadOnlyList<int>? swaps = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(day, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(day, PlanLength);
        return PlanDay(profile, Simulate(profile).Days[day - 1], swaps);
    }

    public static WellnessPlanDay PlanDay(WellnessProfile profile, WellnessDay day,
        IReadOnlyList<int>? swaps = null) =>
        new(day, WellnessNutrition.BuildDay(profile, day.Targets, day.Day, swaps, day.SimpleMenu));

    public static WellnessSimulation Simulate(WellnessProfile profile, CancellationToken ct = default) =>
        RunTimeline(profile, PlanLength, recordedOnly: false, ct);

    // La simulación de referencia conserva sus proyecciones v5.2.
    public static WellnessSimulation EvaluateRecordedTimeline(WellnessProfile profile, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(profile.CurrentDay, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(profile.CurrentDay, PlanLength);
        return RunTimeline(profile with
        {
            Real = true, CurrentWeightKg = null, CurrentCalorieAdjustment = null, CurrentStreak = null
        }, profile.CurrentDay, recordedOnly: true, ct);
    }

    private static WellnessSimulation RunTimeline(WellnessProfile profile, int length, bool recordedOnly, CancellationToken ct)
    {
        var days = new WellnessDay[length];
        var events = new List<WellnessEvent>();
        var waistHistory = new List<double> { profile.InitialWaistCm };
        var random = new WellnessRandom(profile.Seed);
        var weight = profile.InitialWeightKg;
        var observedWeight = weight;
        double adjustment = 0;
        var share = WellnessRules.TargetShare(profile);
        var activity = WellnessRules.ActivityMultiplier(profile.ActivityLevel);
        var gain = profile.Goal == WellnessGoal.GainMuscle;
        var reached = false;
        var lastMode = WellnessMode.Main;
        var points = 0;
        var streak = 0;
        var pauseUntil = 0;
        var hunger = 0;

        for (var index = 0; index < days.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            var day = index + 1;
            var week = (day + 6) / 7;
            var dow = index % 7;
            var previous = index >= 1 ? days[index - 1] : null;
            var previous2 = index >= 2 ? days[index - 2] : null;
            // La referencia consume seis números todos los días, incluso si hay un registro real.
            var rolls = new DailyRolls(random.Next(), random.Next(), random.Next(), random.Next(), random.Next(), random.Next());
            if (day == profile.CurrentDay)
            {
                if (profile.CurrentWeightKg is { } weightAt)
                {
                    weight = weightAt;
                    if (profile.CurrentCalorieAdjustment is { } adjustmentAt) adjustment = adjustmentAt;
                }
                if (profile.CurrentStreak is { } streakAt)
                {
                    streak = streakAt;
                    if (previous is not null) previous = previous with { Streak = streakAt };
                }
            }

            var targetCheckWeight = recordedOnly ? observedWeight : weight;
            if (!reached && day > 1 && profile.TargetWeightKg is { } target && target != 0 &&
                (gain ? targetCheckWeight >= target - .05 : targetCheckWeight <= target + .05))
            {
                reached = true;
                events.Add(new(day, "meta", null, null,
                    new("Meta de peso alcanzada: el motor pasa a fase Mantener.",
                        "Target weight reached: engine moves to Maintain phase.")));
            }
            var mode = WellnessRules.Mode(profile.Goal, week, reached, profile.Parameters);
            if (mode != lastMode)
            {
                if (day > 1)
                {
                    var name = WellnessRules.ModeName(mode);
                    events.Add(new(day, "modo", null, null, new($"Cambio de fase: {name.Es}.", $"Phase change: {name.En}.")));
                }
                adjustment = 0;
                lastMode = mode;
            }

            var tdee = BasalMetabolicRate(profile, weight) * activity;
            var baseCalories = mode == WellnessMode.Main ? tdee * (gain ? 1 + share : 1 - share) : tdee;
            var cap = tdee * WellnessRules.AdjustmentCap;
            var kcal = Math.Max(CalorieFloor(profile), WellnessRules.RoundTo(baseCalories +
                Math.Max(-cap, Math.Min(cap, adjustment)), 10));
            var daily = DailyRules(day, previous, previous2, ref pauseUntil, ref hunger);
            var exercise = WellnessExercise.ForDay(profile, day, daily.SwapForPain, daily.LevelDelta, daily.Boost);
            var macros = MacroTargets(profile.Goal, mode, weight, kcal, exercise.Training, profile.Parameters);

            var past = day < profile.CurrentDay;
            var hasLog = profile.Logs.TryGetValue(day, out var recorded);
            var log = recorded is not null
                ? recorded with { Symptoms = Array.AsReadOnly(recorded.Symptoms.ToArray()) }
                : recordedOnly ? new WellnessDailyLog(.8, 7, 3, 0, null, false)
                : ReadUnloggedDay(profile, weight, past, daily.Paused, rolls);
            if (recordedOnly && recorded?.MeasuredWeightKg is { } measured) observedWeight = measured;
            var deviation = (1 - log.Adherence) * (gain ? -.30 : .35);
            var eaten = kcal * (1 + deviation);
            var effectiveTdee = tdee * (1 + profile.MetabolicModifier);
            var startWeight = weight;
            weight += (eaten - effectiveTdee) / (gain && eaten > effectiveTdee ? 6500 : 7700);
            double? waist = null;
            if (day % 7 == 0)
            {
                var noise = past || hasLog ? (random.Next() - .5) * .6 : 0;
                var waistTrend = profile.InitialWaistCm + (weight - profile.InitialWeightKg) * .9 -
                                 (profile.Goal != WellnessGoal.LoseWeight ? Math.Min(day / 7, 26) * .08 : 0);
                waist = WellnessRules.Fixed(waistTrend + noise, 1);
                waistHistory.Add(waist.Value);
            }

            int? recordedPoints = null, recordedStreak = null;
            if (hasLog || (!recordedOnly && past))
            {
                points += (log.CompletedExercise ? 15 : 0) + (int)WellnessRules.Round(log.Adherence * 20) +
                          (log.MeasuredWeightKg.HasValue ? 3 : 0) + 2;
                streak = log.Adherence >= .6 ? streak + 1 : 0;
                recordedPoints = points;
                recordedStreak = streak;
            }
            else if (past)
            {
                streak = 0;
                recordedStreak = 0;
            }

            days[index] = new(day, week, dow, mode, macros, Math.Max(25, (int)WellnessRules.Round(14 * kcal / 1000)),
                (int)WellnessRules.RoundTo(35 * startWeight, 250), WellnessRules.Fixed(startWeight, 2),
                exercise.Training, exercise.Type, daily.Simple, daily.Paused, daily.Reasons, daily.LevelDelta,
                daily.SwapForPain, (int)WellnessRules.Round(tdee), adjustment, exercise, log, past,
                day == profile.CurrentDay, WellnessRules.Fixed(weight, 2), waist, recordedPoints, recordedStreak);

            if (day % 7 == 0 && day >= 14)
            {
                var decision = WellnessAdjustments.Weekly(profile.Goal, days.AsSpan(index - 13, 14),
                    waistHistory[^1] - waistHistory[^3], cap, ref hunger, profile.Parameters);
                adjustment += decision.CalorieDelta ?? 0;
                events.Add(decision);
            }
        }

        return new(Array.AsReadOnly(days), events.AsReadOnly(), reached);
    }

    private static WellnessMacros MacroTargets(WellnessGoal goal, WellnessMode mode, double weight, double kcal, bool train, WellnessParameters parameters)
    {
        var protein = WellnessRules.Round(Math.Min(parameters.ProCap, parameters.Pro.For(goal)) * weight);
        var share = train && mode != WellnessMode.Maintain ? parameters.Fat.Train : parameters.Fat.Rest;
        var fat = WellnessRules.Round(kcal * share / 9);
        var carbs = WellnessRules.Round((kcal - 4 * protein - 9 * fat) / 4);
        if (carbs < parameters.CarbMin)
        {
            carbs = parameters.CarbMin;
            fat = WellnessRules.Round((kcal - 4 * protein - 4 * carbs) / 9);
        }
        return new(kcal, protein, carbs, fat);
    }

    private sealed record DailyAdjustment(bool Simple, bool Paused, bool SwapForPain, int LevelDelta,
        int Boost, IReadOnlyList<WellnessText> Reasons);

    private static DailyAdjustment DailyRules(int day, WellnessDay? previous, WellnessDay? previous2,
        ref int pauseUntil, ref int hunger)
    {
        var reasons = new List<WellnessText>();
        var simple = previous?.Log.Adherence < .6 && previous2?.Log.Adherence < .6;
        var paused = day < pauseUntil;
        var swapForPain = false;
        var levelDelta = 0;
        var boost = 0;

        if (previous is not null)
        {
            var log = previous.Log;
            if (log.SleepHours < 5.5 || log.Energy <= 2)
            {
                levelDelta = -1;
                reasons.Add(new("Sueño bajo o poca energía ayer: bajamos un nivel el ejercicio de hoy.",
                    "Low sleep/energy yesterday: exercise lowered one level today."));
            }
            if (log.Pain >= 7)
            {
                pauseUntil = day + 2;
                paused = true;
                reasons.Add(new("Dolor intenso reportado: ejercicio en pausa 2 días. Si persiste, consulta a un profesional de salud.",
                    "Severe pain reported: exercise paused 2 days. If it persists, see a health professional."));
            }
            else if (log.Pain >= 4)
            {
                swapForPain = true;
                reasons.Add(new("Dolor 4+ ayer: hoy solo ejercicios de bajo impacto.", "Pain 4+ yesterday: low-impact exercises only today."));
            }
        }

        if (simple)
            reasons.Add(new("Adherencia baja 2 días: menú simplificado (platos fáciles y repetibles).",
                "Low adherence 2 days: simplified menu (easy, repeatable meals)."));

        if (previous is not null)
        {
            var streak = previous.Streak ?? 0;
            if (streak >= 14)
            {
                boost = 2;
                reasons.Add(new($"Racha de {streak} días: desbloqueas ejercicios más difíciles antes de lo previsto.",
                    $"{streak}-day streak: harder exercises unlock earlier than planned."));
            }
            else if (streak >= 7)
            {
                boost = 1;
                reasons.Add(new($"Racha de {streak} días: la progresión avanza una semana.",
                    $"{streak}-day streak: progression moves up one week."));
            }
            else if (streak == 0 && day > 14)
            {
                boost = -1;
                reasons.Add(new("Racha rota: retomamos con carga un poco menor para volver sin lesiones.",
                    "Streak broken: restart with slightly lower load to return safely."));
            }

            var symptoms = previous.Log.Symptoms;
            if (symptoms.Contains("mareo"))
            {
                pauseUntil = Math.Max(pauseUntil, day + 1);
                paused = true;
                reasons.Add(new("Mareo reportado: sin ejercicio hoy y no se baja la comida. Si se repite, consulta a un profesional de salud.",
                    "Dizziness reported: no exercise today and no calorie cut. If it repeats, see a health professional."));
            }
            if (symptoms.Contains("hambre")) hunger++;
            if (symptoms.Contains("fatiga") && levelDelta == 0)
            {
                levelDelta = -1;
                reasons.Add(new("Fatiga reportada: ejercicio un nivel más suave hoy.", "Fatigue reported: exercise one level easier today."));
            }
            if (symptoms.Contains("insomnio"))
                reasons.Add(new("Insomnio: cena más temprana y ligera; prioriza dormir.", "Insomnia: earlier, lighter dinner; prioritise sleep."));
        }

        return new(simple, paused, swapForPain, levelDelta, boost, reasons.AsReadOnly());
    }

    private readonly record struct DailyRolls(double Adherence, double Sleep, double Energy,
        double Pain, double ExerciseAndWeigh, double WeightNoise);

    private static WellnessDailyLog ReadUnloggedDay(WellnessProfile profile, double weight, bool past,
        bool paused, DailyRolls rolls)
    {
        if (!past) return new(profile.ProjectedAdherence, 7, 4, 0, weight, true);
        if (profile.Real) return new(.8, 7, 3, 0, null, false);

        var baseline = profile.DemoHistoricalAdherence;
        var adherence = Math.Clamp(baseline + (rolls.Adherence - .5) * .5, .2, 1);
        var exercise = !paused && rolls.ExerciseAndWeigh < baseline + .1;
        if (!exercise && adherence > .8) adherence -= .2;
        var pain = rolls.Pain < .05 ? 4 + (int)WellnessRules.Round(rolls.Pain * 40) : rolls.Pain < .1 ? 1 : 0;
        double? measured = rolls.ExerciseAndWeigh < profile.DemoWeighProbability
            ? weight + (rolls.WeightNoise - .5) * .9 : null;
        IReadOnlyList<string> symptoms = rolls.Energy < .06 ? ["fatiga"] :
            rolls.WeightNoise < .04 ? ["hambre"] : rolls.Sleep < .02 ? ["insomnio"] : [];
        return new(adherence, 5 + rolls.Sleep * 3.2, (int)WellnessRules.Round(2 + rolls.Energy * 3),
            pain, measured, exercise) { Symptoms = Array.AsReadOnly(symptoms.ToArray()) };
    }
}
