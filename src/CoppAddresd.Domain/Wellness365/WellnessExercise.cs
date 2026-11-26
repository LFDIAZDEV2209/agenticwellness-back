using System.Collections.Frozen;

namespace CoppAddresd.Domain.Wellness365;

public static class WellnessExercise
{
    private sealed record DayPattern(WellnessText Name, string[] Slots);
    private sealed record Selection(string Id, string Tag, bool NewlyUnlocked, WellnessExerciseDefinition Definition);

    private static readonly WellnessPhase[] Phases =
    [
        new(new("Adaptación", "Adaptation"), 1, 8, 0),
        new(new("Construcción", "Build"), 9, 20, 1),
        new(new("Intensificación", "Intensify"), 21, 36, 2),
        new(new("Consolidación", "Consolidate"), 37, 52, 3)
    ];

    private static readonly Dictionary<char, DayPattern> Patterns = new()
    {
        ['C'] = new(new("Cardio suave", "Easy cardio"), ["mob", "card", "card", "str"]),
        ['F'] = new(new("Cuerpo completo", "Full body"), ["legs", "push", "pull", "core"]),
        ['M'] = new(new("Movilidad", "Mobility"), ["mob", "bal", "breath", "str"]),
        ['P'] = new(new("Empuje", "Push"), ["push", "push", "core", "str"]),
        ['L'] = new(new("Piernas", "Legs"), ["legs", "legs", "core", "str"]),
        ['J'] = new(new("Tirón", "Pull"), ["pull", "pull", "core", "str"]),
        ['R'] = new(new("Recuperación activa", "Active recovery"), ["cardlow", "mob", "str", "breath"]),
        ['K'] = new(new("Core + cardio", "Core + cardio"), ["core", "core", "card", "str"]),
        ['H'] = new(new("Intervalos + fuerza", "Intervals + strength"), ["cardhi", "cardhi", "push", "str"])
    };

    private static readonly Dictionary<string, string[]> Pools = new(StringComparer.Ordinal)
    {
        ["card"] = ["walk", "bike", "ellip", "hill", "int"],
        ["cardlow"] = ["walk", "bike", "ellip"],
        ["cardhi"] = ["int", "hill", "ellip", "bike", "walk"],
        ["push"] = ["wall", "tri", "kpush", "press"],
        ["pull"] = ["row", "curl", "drow"],
        ["legs"] = ["chair", "bridge", "calf", "lunge", "step", "gob", "rdl"],
        ["core"] = ["dead", "bird", "plank"],
        ["mob"] = ["mob"], ["str"] = ["str"], ["bal"] = ["bal"], ["breath"] = ["breath"]
    };

    private static readonly Dictionary<string, int> Unlocks = new(StringComparer.Ordinal)
    {
        ["walk"] = 1, ["bike"] = 1, ["mob"] = 1, ["str"] = 1, ["breath"] = 1, ["bal"] = 1,
        ["chair"] = 1, ["wall"] = 1, ["bridge"] = 1, ["row"] = 1, ["dead"] = 1, ["tri"] = 2,
        ["curl"] = 2, ["ellip"] = 3, ["calf"] = 3, ["kpush"] = 4, ["bird"] = 4, ["drow"] = 5,
        ["press"] = 7, ["hill"] = 8, ["plank"] = 9, ["lunge"] = 10, ["int"] = 14,
        ["step"] = 16, ["gob"] = 20, ["rdl"] = 26
    };

    public static IReadOnlyList<WellnessExerciseDefinition> OrderedCatalog { get; } =
        Array.AsReadOnly(WellnessRules.ReadReference<WellnessExerciseDefinition[]>("catalog.json"));

    public static IReadOnlyDictionary<string, WellnessExerciseDefinition> Catalog { get; } =
        OrderedCatalog.ToFrozenDictionary(x => x.Id, StringComparer.Ordinal);

    private static readonly Lazy<FrozenDictionary<string, WellnessExerciseGuide>> GuideCatalog = new(LoadGuides);

    public static IReadOnlyDictionary<string, WellnessExerciseGuide> Guides => GuideCatalog.Value;

    private static readonly Lazy<FrozenDictionary<string, WellnessText>> DescriptionCatalog = new(() =>
    {
        var descriptions = WellnessRules.ReadReference<Dictionary<string, WellnessText>>("descriptions.json");
        if (descriptions.Count != Catalog.Count || descriptions.Keys.Any(id => !Catalog.ContainsKey(id)))
            throw new InvalidOperationException("Las descripciones no coinciden con el catálogo de ejercicio.");
        return descriptions.ToFrozenDictionary(StringComparer.Ordinal);
    });

    public static IReadOnlyDictionary<string, WellnessText> Descriptions => DescriptionCatalog.Value;

    public static int UnlockWeek(string id) => Unlocks[id];

    public static WellnessPhase PhaseForWeek(int week) =>
        Array.Find(Phases, p => week >= p.FirstWeek && week <= p.LastWeek) ?? Phases[^1];

    public static char DayType(WellnessGoal goal, int dayOfWeek) => (goal switch
    {
        WellnessGoal.LoseWeight => "CFCFCMC",
        WellnessGoal.GainMuscle => "PLJRFKR",
        WellnessGoal.Tone => "PLJHFKR",
        _ => throw new ArgumentOutOfRangeException(nameof(goal))
    })[dayOfWeek];

    public static bool IsTrainingDay(char type) => type is not ('M' or 'R');

    private static FrozenDictionary<string, WellnessExerciseGuide> LoadGuides()
    {
        var guides = WellnessRules.ReadReference<Dictionary<string, WellnessExerciseGuide>>("guide.json");
        if (guides.Count != Catalog.Count || guides.Keys.Any(id => !Catalog.ContainsKey(id)))
            throw new InvalidOperationException("Los identificadores de la guía y el catálogo de ejercicio no coinciden.");

        return guides.ToFrozenDictionary(pair => pair.Key,
            pair => new WellnessExerciseGuide(ReadOnlySteps(pair.Value.Es), ReadOnlySteps(pair.Value.En)),
            StringComparer.Ordinal);

        static WellnessExerciseInstructions ReadOnlySteps(WellnessExerciseInstructions instructions) =>
            instructions with { Steps = Array.AsReadOnly(instructions.Steps.ToArray()) };
    }

    public static WellnessExerciseDay ForDay(WellnessProfile profile, int day, bool swapForPain = false,
        int levelDelta = 0, int boost = 0) => BuildDay(profile, day, swapForPain, levelDelta, boost, reference: false);

    // Solo para la simulación de paridad con el kit v5.2 original.
    internal static WellnessExerciseDay ForReferenceDay(WellnessProfile profile, int day, bool swapForPain,
        int levelDelta, int boost) => BuildDay(profile, day, swapForPain, levelDelta, boost, reference: true);

    private static WellnessExerciseDay BuildDay(WellnessProfile profile, int day, bool swapForPain,
        int levelDelta, int boost, bool reference)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(day, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(day, WellnessEngine.PlanLength);
        var baseWeek = reference ? (day + 6) / 7 : Math.Min(52, (day + 6) / 7);
        var rotationWeek = reference ? Math.Max(1, baseWeek + boost) : Math.Clamp(baseWeek + boost, 1, 52);
        var week = reference ? rotationWeek : baseWeek;
        var dow = (day - 1) % 7;
        var type = DayType(profile.Goal, dow);
        var pattern = Patterns[type];
        var phase = PhaseForWeek(week);
        var level = Math.Clamp(phase.Level + profile.FitnessLevel + levelDelta, 0, 3);
        var deload = baseWeek % 4 == 0;
        var minutes = 15 + (int)WellnessRules.Round(7 * ((week - 1) / 51d)) +
                      (profile.FitnessLevel != 0 ? 1 : 0);
        if (!reference) minutes = Math.Min(minutes, 22);
        minutes = Math.Clamp(minutes - (deload ? 2 : 0) - (levelDelta < 0 ? 2 : 0), 15, 22);

        var selected = SelectExercises(pattern, week, rotationWeek, dow, swapForPain);
        var weights = selected.Select(x => x.Definition.Category switch
        {
            "Cardio" => 1.7, "Fuerza" => 1.2, _ => .6
        }).ToArray();
        var totalWeight = weights.Sum();
        var left = minutes;
        var items = new WellnessExerciseItem[selected.Length];
        for (var i = 0; i < items.Length; i++)
        {
            var choice = selected[i];
            var allotted = Math.Max(2,
                i == items.Length - 1 ? left : (int)WellnessRules.Round(minutes * weights[i] / totalWeight));
            if (!reference) allotted = Math.Min(allotted, left - 2 * (items.Length - i - 1));
            left -= allotted;
            items[i] = new(choice.Id, choice.Tag, choice.NewlyUnlocked, allotted,
                choice.Definition, StrengthDose(choice, profile.Goal, level, reference));
        }

        return new(type, pattern.Name, level, minutes, deload, baseWeek, boost, phase,
            Array.AsReadOnly(items), IsTrainingDay(type));
    }

    private static Selection[] SelectExercises(DayPattern pattern, int week, int rotationWeek, int dow, bool swapForPain)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var selected = new Selection[pattern.Slots.Length];
        for (var slot = 0; slot < selected.Length; slot++)
        {
            var tag = pattern.Slots[slot];
            var pool = Pools[tag].Where(id => UnlockWeek(id) <= week).ToArray();
            if (swapForPain)
            {
                var lowImpact = pool.Where(id => Catalog[id].Impact == "bajo").ToArray();
                if (lowImpact.Length > 0) pool = lowImpact;
            }

            var fresh = pool.FirstOrDefault(id => IsNew(id, week));
            var unused = pool.Where(id => !used.Contains(id)).ToArray();
            var candidates = unused.Length > 0 ? unused : pool;
            var id = tag == "cardhi"
                ? candidates[(rotationWeek + slot) % Math.Min(candidates.Length, 2)]
                : fresh is not null && dow % 2 == 0 && slot == 0 && tag is not ("mob" or "str") &&
                  candidates.Contains(fresh)
                    ? fresh
                    : candidates[(rotationWeek * 2 + dow + slot * 3) % candidates.Length];
            used.Add(id);
            selected[slot] = new(id, tag, IsNew(id, week), Catalog[id]);
        }

        return selected;
    }

    private static bool IsNew(string id, int week) => UnlockWeek(id) > 1 && UnlockWeek(id) > week - 3;

    private static WellnessStrengthDose? StrengthDose(Selection selection, WellnessGoal goal, int level, bool reference)
    {
        if (selection.Definition.Category != "Fuerza") return null;
        ReadOnlySpan<int> hold = [15, 20, 30, 40];
        return new(
            (level >= 2 ? 3 : 2) + (reference && goal == WellnessGoal.GainMuscle && level >= 2 ? 1 : 0),
            level == 3 ? 15 : 8 + level * 2,
            level >= 2 ? 60 : 45,
            selection.Id == "plank" ? hold[level] : 0,
            goal == WellnessGoal.LoseWeight ? "2–3" : "1–2");
    }
}
