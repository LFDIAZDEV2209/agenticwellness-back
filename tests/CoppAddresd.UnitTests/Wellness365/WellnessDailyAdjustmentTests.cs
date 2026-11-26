using CoppAddresd.Domain.Wellness365;

namespace CoppAddresd.UnitTests.Wellness365;

public sealed class WellnessDailyAdjustmentTests
{
    private static WellnessDailyLog Log(double adherence = .8, double sleep = 7, int energy = 4, int pain = 0,
        string[]? symptoms = null, double? weight = null) =>
        new(adherence, sleep, energy, pain, weight, false) { Symptoms = symptoms ?? [] };

    private static WellnessProfile Profile(int day, Dictionary<int, WellnessDailyLog> logs) =>
        new(WellnessSex.Female, 36, 170, 85, 95, 65, 2, WellnessGoal.LoseWeight, day, 42)
        { Logs = logs, FitnessLevel = 1 };

    private static WellnessDay Day(int day, Dictionary<int, WellnessDailyLog> logs) =>
        WellnessEngine.EvaluateRecordedTimeline(Profile(day, logs)).Days[^1];

    [Theory]
    [InlineData(5.49, 3, false, -1)]
    [InlineData(5.5, 2, false, -1)]
    [InlineData(5.5, 3, true, -1)]
    [InlineData(5, 2, true, -1)]
    [InlineData(5.5, 3, false, 0)]
    public void SleepEnergyAndFatigueReduceExerciseOnce(double sleep, int energy, bool fatigue, int delta)
    {
        var day = Day(2, new() { [1] = Log(sleep: sleep, energy: energy, symptoms: fatigue ? ["fatiga"] : []) });
        Assert.Equal(delta, day.LevelDelta);
        Assert.Equal(1 + delta, day.Exercise.Level);
        Assert.Equal(delta != 0, day.Reasons.Count > 0);
    }

    [Theory]
    [InlineData(3, false, false)]
    [InlineData(4, true, false)]
    [InlineData(6, true, false)]
    [InlineData(7, false, true)]
    [InlineData(10, false, true)]
    public void PainSeparatesLowImpactFromTwoFullDaysOfRest(int pain, bool lowImpact, bool paused)
    {
        const int loggedDay = 100;
        var logs = new Dictionary<int, WellnessDailyLog> { [loggedDay] = Log(pain: pain) };
        var days = WellnessEngine.EvaluateRecordedTimeline(Profile(loggedDay + 3, logs)).Days;
        Assert.False(days[loggedDay - 1].Paused);
        var tomorrow = days[loggedDay];
        Assert.Equal(lowImpact, tomorrow.SwapForPain);
        if (lowImpact) Assert.All(tomorrow.Exercise.Items, item => Assert.Equal("bajo", item.Definition.Impact));
        Assert.Equal(paused, tomorrow.Paused);
        Assert.Equal(paused, days[loggedDay + 1].Paused);
        Assert.Equal(paused, days[loggedDay + 1].Reasons.Count > 0);
        Assert.False(days[loggedDay + 2].Paused);
    }

    [Theory]
    [InlineData("mareo", true, true)]
    [InlineData("hambre", true, false)]
    [InlineData("nauseas", false, false)]
    public void YesterdaySymptomsCanBlockTheNextFoodCut(string symptom, bool blocked, bool paused)
    {
        var logs = Enumerable.Range(1, 14).ToDictionary(d => d, d => Log(weight: d <= 7 ? 85 : 86,
            symptoms: d == 14 ? [symptom] : []));
        var after = WellnessEngine.EvaluateRecordedTimeline(Profile(16, logs));
        Assert.Equal(paused, after.Days[14].Paused);
        Assert.False(after.Days[15].Paused);
        var adjustment = after.Events.Single(e => e.Day == 14);
        if (blocked)
        {
            Assert.Equal(0, adjustment.CalorieDelta);
            Assert.True(after.Days[14].Targets.Kcal >= after.Days[13].Targets.Kcal);
        }
        else Assert.True(adjustment.CalorieDelta < 0);
    }

    [Fact]
    public void FoodCutVetoDoesNotBlockAnIncrease()
    {
        var logs = Enumerable.Range(1, 14).ToDictionary(d => d, d => Log(weight: d <= 7 ? 85 : 83,
            symptoms: d == 14 ? ["hambre"] : []));
        var result = WellnessEngine.EvaluateRecordedTimeline(Profile(15, logs));
        Assert.True(result.Events.Single(e => e.Day == 14).CalorieDelta > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatDizzinessEncouragesProfessionalFollowUp(bool repeats)
    {
        var day = Day(5, new() { [1] = Log(symptoms: repeats ? ["mareo"] : []), [4] = Log(symptoms: ["mareo"]) });
        Assert.True(day.Paused);
        Assert.Equal(repeats, day.Reasons.Any(reason => reason.Es.Contains("volviste", StringComparison.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InsomniaLightensDinnerWithoutCuttingTheWholeDay(bool insomnia)
    {
        var profile = Profile(2, new() { [1] = Log(symptoms: insomnia ? ["insomnio"] : []) });
        var day = WellnessEngine.EvaluateRecordedTimeline(profile).Days[^1];
        var menu = WellnessNutrition.BuildPublishedDay(profile, day);
        var options = WellnessNutrition.BuildOptions(profile, day, menu);
        Assert.Equal(day.Targets.Kcal * (insomnia ? .20 : .25), options[3].TargetKcal, 4);
        Assert.Equal(day.Targets.Kcal, options.Sum(slot => slot.TargetKcal), 4);
        var normal = WellnessNutrition.BuildPublishedDay(profile, Day(2, new()));
        if (insomnia) Assert.True(menu.Meals[3].Totals.Kcal < normal.Meals[3].Totals.Kcal);
    }

    [Theory]
    [InlineData(.59, .59, true)]
    [InlineData(.59, .60, false)]
    [InlineData(.60, .59, false)]
    [InlineData(null, .59, false)]
    public void MenuSimplifiesOnlyAfterTwoRecordedLowDays(double? first, double second, bool simple)
    {
        var logs = new Dictionary<int, WellnessDailyLog> { [2] = Log(adherence: second) };
        if (first is { } value) logs[1] = Log(adherence: value);
        Assert.Equal(simple, Day(3, logs).SimpleMenu);
    }

    [Theory]
    [InlineData(6, 0, 0)]
    [InlineData(7, 1, 0)]
    [InlineData(13, 1, 0)]
    [InlineData(14, 2, 1)]
    public void ConsecutiveSixtyPercentAdvancesDoseButNotCatalogUnlocks(int streak, int boost, int dosePhase)
    {
        var logs = Enumerable.Range(49 - streak, streak).ToDictionary(d => d, _ => Log(adherence: .60));
        var day = Day(49, logs);
        Assert.Equal(streak, Day(48, logs).Streak);
        Assert.Equal(boost, day.Exercise.Boost);
        Assert.Equal(1 + dosePhase, day.Exercise.Level);
        Assert.Equal(0, day.Exercise.Phase.Level);
        Assert.All(day.Exercise.Items, item => Assert.True(WellnessExercise.UnlockWeek(item.Id) <= day.Exercise.Week));
    }

    [Theory]
    [InlineData(14, .59, true, 0, 0)]
    [InlineData(15, .59, true, -1, -1)]
    [InlineData(15, null, true, 0, 0)]
    [InlineData(15, .59, false, 0, 0)]
    [InlineData(15, .60, true, 0, 0)]
    public void BrokenStreakRequiresAnActualLowClosureAfterDay14(int day, double? adherence, bool prior, int boost, int delta)
    {
        var logs = new Dictionary<int, WellnessDailyLog>();
        if (prior) logs[day - 2] = Log(adherence: .60);
        if (adherence is { } value) logs[day - 1] = Log(adherence: value);
        var result = Day(day, logs);
        Assert.Equal(boost, result.Exercise.Boost);
        Assert.Equal(delta, result.LevelDelta);
        Assert.Equal(delta != 0, result.Reasons.Count > 0);
    }

    [Fact]
    public void ExerciseProtectionTakesPriorityOverAnEarnedBoost()
    {
        var logs = Enumerable.Range(35, 14).ToDictionary(d => d, _ => Log());
        logs[48] = Log(pain: 4, sleep: 5);
        var day = Day(49, logs);
        Assert.Equal(14, Day(48, logs).Streak);
        Assert.Equal(0, day.Exercise.Boost);
        Assert.Equal(-1, day.LevelDelta);
        Assert.True(day.SwapForPain);
        Assert.All(day.Exercise.Items, item => Assert.Equal("bajo", item.Definition.Impact));
    }

    [Fact]
    public void AnnualProjectionDoesNotUseFutureLogs()
    {
        var logs = new Dictionary<int, WellnessDailyLog> { [2] = Log(pain: 10, symptoms: ["mareo", "insomnio"]) };
        var days = WellnessEngine.EvaluateRecordedTimeline(Profile(1, logs), includeProjections: true).Days;
        Assert.False(days[2].Paused);
        Assert.False(days[2].LighterDinner);
        Assert.Empty(days[2].Reasons);
    }
}
