using System.Collections.Frozen;
using System.Globalization;

namespace CoppAddresd.Domain.Wellness365;

public sealed record WellnessFood(
    string Es, string En, double Kcal, double Protein, double Carbs, double Fat,
    string Group, int UnitGrams, string UnitEs, string UnitEn);

public static class WellnessNutrition
{
    private sealed record ReferenceData(Dictionary<string, WellnessFood> Foods, string[][][] Templates);
    private sealed record Slot(char Role, string[] Options, int? FixedGrams);
    private sealed class Portion(char role, string id, double grams)
    {
        internal char Role { get; } = role;
        internal string Id { get; } = id;
        internal double Grams { get; set; } = grams;
    }

    private static readonly Lazy<ReferenceData> Reference = new(() =>
        WellnessRules.ReadReference<ReferenceData>("food-and-templates.json"));
    private static readonly Lazy<Slot[][][]> Templates = new(() =>
        Reference.Value.Templates.Select(meal => meal.Select(template => template.Select(ParseSlot).ToArray())
            .ToArray()).ToArray());
    private static readonly Lazy<FrozenDictionary<string, WellnessFood>> FoodCatalog = new(() =>
        Reference.Value.Foods.ToFrozenDictionary(StringComparer.Ordinal));

    private static readonly Dictionary<string, int> GramCaps = new(StringComparer.Ordinal)
    {
        ["oats"] = 120, ["bread"] = 140, ["arepa"] = 240, ["oil"] = 20, ["alm"] = 50,
        ["pb"] = 40, ["chia"] = 40, ["avo"] = 140, ["egg"] = 250, ["queso"] = 120,
        ["yog"] = 400, ["tofu"] = 300
    };

    private static readonly (WellnessText Name, double Share)[] MealTypes =
    [
        (new("Desayuno", "Breakfast"), .25),
        (new("Almuerzo", "Lunch"), .35),
        (new("Merienda", "Snack"), .15),
        (new("Cena", "Dinner"), .25)
    ];

    public static IReadOnlyDictionary<string, WellnessFood> Catalog => FoodCatalog.Value;

    public static bool IsAllowed(string id, WellnessProfile profile)
    {
        var group = Catalog[id].Group;
        return !(profile.Vegetarian && group is ("meat" or "fish") ||
                 profile.LactoseFree && group == "dairy");
    }

    public static WellnessMacros MacrosFor(string id, double grams)
    {
        var food = Catalog[id];
        return new(food.Kcal * grams / 100, food.Protein * grams / 100,
            food.Carbs * grams / 100, food.Fat * grams / 100);
    }

    public static WellnessMenu BuildDay(WellnessProfile profile, WellnessMacros target, int day,
        IReadOnlyList<int>? swaps = null, bool simple = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(day, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(day, WellnessEngine.PlanLength);
        var portions = new Portion[MealTypes.Length][];
        var indices = new int[MealTypes.Length];
        for (var i = 0; i < portions.Length; i++)
        {
            var templates = Templates.Value[i];
            var swap = swaps is not null && i < swaps.Count ? swaps[i] : 0;
            var index = simple ? 0 : (int)((day + i * 2L + swap) % templates.Length);
            ArgumentOutOfRangeException.ThrowIfNegative(index, nameof(swaps));
            indices[i] = index;
            var share = MealTypes[i].Share;
            portions[i] = BuildMeal(profile, templates[index],
                new(target.Kcal * share, target.Protein * share, target.Carbs * share, target.Fat * share));
        }

        var totals = Calculate(portions);
        var adjustable = portions.SelectMany(x => x).Where(x => x.Role is 'C' or 'F').ToArray();
        for (var iteration = 0; iteration < 3; iteration++)
        {
            var missing = target.Kcal - totals.Sum(x => x.Kcal);
            if (Math.Abs(missing) < target.Kcal * .02) break;
            var adjustableCalories = adjustable.Sum(x => MacrosFor(x.Id, x.Grams).Kcal);
            var factor = 1 + missing / Math.Max(adjustableCalories, 1);
            foreach (var item in adjustable)
            {
                var unit = Catalog[item.Id].UnitGrams;
                item.Grams = Math.Min(GramCaps.GetValueOrDefault(item.Id, 420),
                    Math.Max(unit != 0 ? unit : 5, item.Grams * factor));
                item.Grams = unit != 0 ? RoundServing(item.Grams, unit) : WellnessRules.RoundTo(item.Grams, 5);
            }
            totals = Calculate(portions);
        }

        var meals = portions.Select((meal, i) => new WellnessMeal(indices[i], MealTypes[i].Name,
            Array.AsReadOnly(meal.Select(x => new WellnessMealItem(x.Role, x.Id, x.Grams)).ToArray()),
            totals[i])).ToArray();
        var total = totals.Aggregate(WellnessMacros.Zero, (sum, next) => sum + next);
        return new(Array.AsReadOnly(meals), total, (total.Kcal - target.Kcal) / target.Kcal);
    }

    private static WellnessMacros[] Calculate(Portion[][] portions) => portions.Select(meal =>
        meal.Aggregate(WellnessMacros.Zero, (sum, item) => sum + MacrosFor(item.Id, item.Grams))).ToArray();

    private static Portion[] BuildMeal(WellnessProfile profile, Slot[] template, WellnessMacros target)
    {
        var items = template.Select(slot => new Portion(slot.Role,
            slot.Options.FirstOrDefault(id => IsAllowed(id, profile)) ?? slot.Options[^1],
            slot.FixedGrams ?? (slot.Role is 'P' or 'C' ? 120 : 8))).ToArray();

        for (var i = 0; i < 6; i++)
        foreach (var role in "PCF")
        {
            var item = Array.Find(items, x => x.Role == role);
            if (item is null) continue;
            var other = items.Sum(x => MacrosFor(x.Id, x.Grams).For(role)) -
                        MacrosFor(item.Id, item.Grams).For(role);
            var perGram = MacrosFor(item.Id, 1).For(role);
            var desired = (target.For(role) - other) / Math.Max(perGram, .01);
            var (min, max) = role switch { 'P' => (30, 320), 'C' => (20, 420), _ => (3, 30) };
            item.Grams = Math.Min(GramCaps.GetValueOrDefault(item.Id, max), Math.Max(min, desired));
        }

        foreach (var item in items)
        {
            var unit = Catalog[item.Id].UnitGrams;
            item.Grams = unit != 0 && item.Role != 'X' ? RoundServing(item.Grams, unit) :
                item.Grams >= 60 ? WellnessRules.RoundTo(item.Grams, 5) : WellnessRules.Round(item.Grams);
        }
        return items;
    }

    private static double RoundServing(double grams, int unit) =>
        Math.Max(unit, WellnessRules.RoundTo(grams, unit >= 100 ? unit / 2d : unit));

    private static Slot ParseSlot(string text)
    {
        var parts = text.Split(':');
        return new(parts[0][0], parts[1].Split('>'),
            parts.Length == 3 ? int.Parse(parts[2], CultureInfo.InvariantCulture) : null);
    }
}
