using System.Globalization;
using System.Text.Json;
using CoppAddresd.Domain.Wellness365;

namespace CoppAddresd.UnitTests.Wellness365;

public sealed class WellnessDetailedParityTests
{
    private static readonly JsonElement Plans = WellnessEngineTests.Fixture("reference-plans.json");
    private static readonly JsonElement Histories = WellnessEngineTests.Fixture("reference-histories.json");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [MemberData(nameof(WellnessEngineTests.Personas), MemberType = typeof(WellnessEngineTests))]
    public void PlanDay_V52ReferenceMatchesMealsAndExerciseDetails(string id)
    {
        var profile = WellnessEngineTests.Profile(id);
        var reference = WellnessEngine.Simulate(profile);
        var cases = Plans.EnumerateArray().Where(x => x.GetProperty("id").GetString() == id).ToArray();
        Assert.Equal(12, cases.Length);
        foreach (var expected in cases)
        {
            var day = expected.GetProperty("day").GetInt32();
            var swaps = expected.GetProperty("swaps").Deserialize<int[]>();
            var actual = WellnessEngine.PlanDay(profile, reference.Days[day - 1], swaps);
            var at = $"{id}, día {day}, swaps [{string.Join(',', swaps ?? [])}]";
            var ex = expected.GetProperty("exercise");

            Match(ex, "paused", actual.Recommendation.Paused, at);
            Match(ex, "swapForPain", actual.Recommendation.SwapForPain, at);
            Match(ex, "simple", actual.Recommendation.SimpleMenu, at);
            Match(ex, "boost", actual.Recommendation.Exercise.Boost, at);
            Match(ex, "reasons", actual.Recommendation.Reasons.ToArray(), at);
            var expectedExercises = ex.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(expectedExercises.Length, actual.Recommendation.Exercise.Items.Count);
            for (var i = 0; i < expectedExercises.Length; i++)
            {
                var wanted = expectedExercises[i];
                var item = actual.Recommendation.Exercise.Items[i];
                var location = $"{at}, ejercicio {i + 1}";
                Match(wanted, "id", item.Id, location);
                Match(wanted, "tag", item.Tag, location);
                Match(wanted, "newlyUnlocked", item.NewlyUnlocked, location);
                Match(wanted, "minutes", item.Minutes, location);
                Match(wanted, "strength", item.Strength, location);
            }

            var menu = expected.GetProperty("menu");
            var expectedMeals = menu.GetProperty("meals").EnumerateArray().ToArray();
            Assert.Equal(expectedMeals.Length, actual.Menu.Meals.Count);
            for (var i = 0; i < expectedMeals.Length; i++)
            {
                var meal = actual.Menu.Meals[i];
                var location = $"{at}, comida {i + 1}";
                Match(expectedMeals[i], "index", meal.TemplateIndex, location);
                var items = expectedMeals[i].GetProperty("items").EnumerateArray().ToArray();
                Assert.Equal(items.Length, meal.Items.Count);
                for (var j = 0; j < items.Length; j++)
                {
                    var position = $"{location}, ingrediente {j + 1}";
                    Match(items[j], "r", meal.Items[j].Role.ToString(), position);
                    Match(items[j], "id", meal.Items[j].FoodId, position);
                    Match(items[j], "g", meal.Items[j].Grams, position);
                    Assert.True(WellnessNutrition.IsAllowed(meal.Items[j].FoodId, profile), position);
                }
            }

            var totals = menu.GetProperty("totals");
            Match(totals, "k", actual.Menu.Totals.Kcal, at);
            Match(totals, "P", actual.Menu.Totals.Protein, at);
            Match(totals, "C", actual.Menu.Totals.Carbs, at);
            Match(totals, "F", actual.Menu.Totals.Fat, at);
            Match(menu, "deviation", actual.Menu.Deviation, at);
        }
    }

    [Theory]
    [MemberData(nameof(WellnessEngineTests.Personas), MemberType = typeof(WellnessEngineTests))]
    public void Simulate_V52ReferenceMatchesEveryEventAndTodaysMeasurements(string id)
    {
        var profile = WellnessEngineTests.Profile(id);
        var expected = Histories.EnumerateArray().Single(x => x.GetProperty("id").GetString() == id);
        var actual = WellnessEngine.Simulate(profile);
        Match(expected, "reached", actual.TargetReached, id);
        Match(expected, "weeks", WellnessEngine.EstimateWeeks(profile), id);

        var r = actual.Days[profile.CurrentDay - 1];
        var t = expected.GetProperty("today");
        Match(t, "weight", r.WeightKg, id);
        Match(t, "endWeight", r.EndWeightKg, id);
        Match(t, "tdee", r.Tdee, id);
        Match(t, "fiber", r.FiberGrams, id);
        Match(t, "water", r.WaterMl, id);
        Match(t, "calorieAdjustment", r.CalorieAdjustment, id);

        var expectedEvents = expected.GetProperty("events").EnumerateArray().ToArray();
        Assert.Equal(expectedEvents.Length, actual.Events.Count);
        for (var i = 0; i < expectedEvents.Length; i++)
        {
            var e = actual.Events[i];
            var want = expectedEvents[i];
            var at = $"{id}, evento {i + 1}, día {e.Day}";
            Match(want, "d", e.Day, at);
            Match(want, "k", e.Kind, at);
            Match(want, "delta", e.CalorieDelta, at);
            Match(want, "rate", e.RatePercent, at);
            Match(want, "es", e.Message.Es, at);
            Match(want, "en", e.Message.En, at);
        }
    }

    private static void Match<T>(JsonElement expected, string field, T actual, string context)
    {
        var found = expected.TryGetProperty(field, out var source);
        T? value = found ? source.Deserialize<T>(Json) : default;
        var equal = actual is WellnessText[] texts && value is WellnessText[] want
            ? texts.SequenceEqual(want)
            : EqualityComparer<T>.Default.Equals(value!, actual);
        var display = actual is double number ? number.ToString("R", CultureInfo.InvariantCulture)
            : JsonSerializer.Serialize(actual, Json);
        Assert.True(equal, $"{context}, {field}: esperado {(found ? source.GetRawText() : "ausente")}, obtenido {display}");
    }
}
