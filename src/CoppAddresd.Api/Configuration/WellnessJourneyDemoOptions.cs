namespace CoppAddresd.Api.Configuration;

public sealed record WellnessJourneyDemoOptions(bool Enabled, bool FailureOnFirstAttempt, TimeSpan Delay)
{
    public static WellnessJourneyDemoOptions From(IConfiguration configuration, IHostEnvironment environment)
    {
        var enabled = environment.IsDevelopment() && configuration.GetValue<bool>("WellnessJourneyDemo:Enabled");
        return new(
            enabled,
            enabled && configuration.GetValue<bool>("WellnessJourneyDemo:FailureOnFirstAttempt"),
            TimeSpan.FromSeconds(enabled ? Math.Clamp(configuration.GetValue("WellnessJourneyDemo:DelaySeconds", 2), 1, 8) : 0));
    }
}
