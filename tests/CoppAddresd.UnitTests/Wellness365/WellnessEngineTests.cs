using System.Text.Json;
using CoppAddresd.Domain.Wellness365;

namespace CoppAddresd.UnitTests.Wellness365;

public sealed class WellnessEngineTests
{
    private static readonly JsonElement People = Fixture("people.json");
    private static readonly JsonElement Golden = Fixture("golden.json");

    public static TheoryData<string> Personas =>
        new() { "laura", "andres", "camila", "mateo", "sofia", "diego" };

    [Theory]
    [MemberData(nameof(Personas))]
    public void Simulate_ReferencePerson_MatchesV52Golden(string id)
    {
        var actual = WellnessEngine.Simulate(Profile(id));
        var expected = Golden.GetProperty(id);

        foreach (var item in expected.EnumerateObject())
        {
            if (!int.TryParse(item.Name, out var day)) continue;

            var r = actual.Days[day - 1];
            var want = item.Value;
            var context = $"{id}, día {day}";
            Match(want, "kc", r.Targets.Kcal, context);
            Match(want, "P", r.Targets.Protein, context);
            Match(want, "C", r.Targets.Carbs, context);
            Match(want, "F", r.Targets.Fat, context);
            Match(want, "w", r.WeightKg, context);
            Match(want, "lvl", r.Exercise.Level, context);
            Match(want, "mins", r.Exercise.Minutes, context);
            Match(want, "type", r.Type.ToString(), context);
            Match(want, "mode", r.Mode switch
            {
                WellnessMode.Main => "main", WellnessMode.Break => "break",
                WellnessMode.Maintain => "maint", _ => throw new InvalidOperationException()
            }, context);
            Match(want, "ex", string.Join(',', r.Exercise.Items.Select(x => x.Id)), context);
        }

        Match(expected, "events", actual.Events.Count, id);
        Match(expected, "adjustSum", actual.Events.Sum(e => e.CalorieDelta ?? 0), id);
    }

    [Theory]
    [MemberData(nameof(Personas))]
    public void Simulate_ReferencePerson_SatisfiesAll365DayInvariants(string id)
    {
        var profile = Profile(id);
        var actual = WellnessEngine.Simulate(profile);

        Assert.Equal(365, actual.Days.Count);
        foreach (var (r, index) in actual.Days.Select((day, index) => (day, index)))
        {
            var at = $"{id}, día {index + 1}";
            Assert.True(r.Day == index + 1, at);
            Assert.True(r.Targets.Kcal >= WellnessEngine.CalorieFloor(profile), at);
            var macroCalories = 4 * r.Targets.Protein + 4 * r.Targets.Carbs + 9 * r.Targets.Fat;
            Assert.True(Math.Abs(macroCalories - r.Targets.Kcal) / r.Targets.Kcal <= .03, at);
            Assert.True(r.Exercise.Items.Count == 4, at);
            Assert.True(r.Exercise.Minutes is >= 15 and <= 22, at);
            Assert.All(r.Exercise.Items, item => Assert.True(
                WellnessExercise.UnlockWeek(item.Id) <= r.Week + Math.Max(0, r.Exercise.Boost),
                $"{at}, ejercicio {item.Id}"));
        }

        foreach (var d in new[] { 1, 30, 90, 180, 365 })
        {
            var r = actual.Days[d - 1];
            var menu = WellnessEngine.PlanDay(profile, r).Menu;
            Assert.Equal(4, menu.Meals.Count);
            Assert.True(Math.Abs(menu.Deviation) <= .08, $"{id}, día {d}, menú {menu.Deviation:P2}");
        }
    }

    [Fact]
    public void Simulate_RealProfileWithoutLogs_DoesNotInventPastWeighIns()
    {
        var profile = Profile("laura") with { Real = true, CurrentDay = 30 };
        var past = WellnessEngine.Simulate(profile).Days.Take(29);
        Assert.All(past, r =>
        {
            Assert.Null(r.Log.MeasuredWeightKg);
            Assert.Equal(.8, r.Log.Adherence);
            Assert.False(r.Log.CompletedExercise);
        });
    }

    [Fact]
    public void CheckBlock_MinorOrUnderweightLoss_ReturnsReferenceReason()
    {
        var profile = Profile("laura");
        Assert.Equal("age", WellnessEngine.CheckBlock(profile with { Age = 17 })?.Code);
        Assert.Equal("bmi", WellnessEngine.CheckBlock(profile with { InitialWeightKg = 48, HeightCm = 170 })?.Code);
        Assert.Null(WellnessEngine.CheckBlock(profile));
    }

    [Fact]
    public void Simulate_YesterdaysSleepPainOrDizziness_AppliesReferenceDailyRules()
    {
        WellnessSimulation WithLog(WellnessDailyLog log) => WellnessEngine.Simulate(Profile("laura") with
        {
            Real = true, CurrentDay = 10, Logs = new Dictionary<int, WellnessDailyLog> { [9] = log }
        });

        Assert.Equal(-1, WithLog(new(.9, 4.5, 3, 0, 77, true)).Days[9].LevelDelta);
        var pain = WithLog(new(.9, 7, 4, 8, 77, false)).Days;
        Assert.True(pain[9].Paused);
        Assert.True(pain[10].Paused);
        Assert.False(pain[11].Paused);
        var dizziness = WithLog(new(.9, 7, 4, 0, 77, true) { Symptoms = ["mareo"] }).Days;
        Assert.True(dizziness[9].Paused);
        Assert.False(dizziness[10].Paused);
    }

    // Caracteriza v5.2, no permite usar esta conducta como regla aprobada de producción.
    [Fact]
    public void Simulate_V52DizzinessAtWeeklyBoundary_ReportsPauseButStillAppliesExistingCut()
    {
        var profile = Profile("laura") with
        {
            Real = true,
            CurrentDay = 15,
            Logs = Enumerable.Range(1, 14).ToDictionary(day => day,
                day => new WellnessDailyLog(.9, 7, 4, 0, 78, true) { Symptoms = day == 14 ? ["mareo"] : [] })
        };

        var result = WellnessEngine.Simulate(profile);
        Assert.Equal(-100, result.Events.Single(e => e.Day == 14).CalorieDelta);
        Assert.True(result.Days[14].Paused);
        Assert.Equal(1690, result.Days[13].Targets.Kcal);
        Assert.Equal(1590, result.Days[14].Targets.Kcal);
    }

    internal static WellnessProfile Profile(string id)
    {
        var p = People.EnumerateArray().Single(x => x.GetProperty("id").GetString() == id);
        string S(string key) => p.GetProperty(key).GetString()!;
        int I(string key, int fallback = 0) => p.TryGetProperty(key, out var x) ? x.GetInt32() : fallback;
        double D(string key, double fallback = 0) => p.TryGetProperty(key, out var x) ? x.GetDouble() : fallback;

        return new WellnessProfile(S("sex") == "M" ? WellnessSex.Male : WellnessSex.Female, I("age"),
            D("h"), D("w0"), D("waist0"), D("target"), I("act"), S("goal") switch
            {
                "bajar" => WellnessGoal.LoseWeight, "masa" => WellnessGoal.GainMuscle,
                "def" => WellnessGoal.Tone, _ => throw new InvalidOperationException()
            }, I("day"), (uint)I("seed"))
        {
            Real = false,
            Pace = S("pace") switch
            {
                "s" => WellnessPace.Slow, "m" => WellnessPace.Moderate,
                "max" => WellnessPace.Maximum, _ => throw new InvalidOperationException()
            },
            MetabolicModifier = D("met"), FitnessLevel = I("fit"), DemoHistoricalAdherence = D("adhBase", .8),
            DemoWeighProbability = D("weigh", .8), ProjectedAdherence = D("adhFut", .85),
            Vegetarian = I("veg") != 0, LactoseFree = I("nolac") != 0
        };
    }

    internal static JsonElement Fixture(string name) => JsonSerializer.Deserialize<JsonElement>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Wellness365", "Fixtures", name)));

    private static void Match(JsonElement expected, string field, double actual, string context) =>
        Assert.True(expected.GetProperty(field).GetDouble() == actual,
            $"{context}, {field}: esperado {expected.GetProperty(field)}, obtenido {actual}");

    private static void Match(JsonElement expected, string field, string actual, string context) =>
        Assert.True(expected.GetProperty(field).GetString() == actual,
            $"{context}, {field}: esperado {expected.GetProperty(field)}, obtenido {actual}");
}
