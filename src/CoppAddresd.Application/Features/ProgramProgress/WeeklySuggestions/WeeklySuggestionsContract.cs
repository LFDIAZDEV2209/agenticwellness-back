using System.Text.RegularExpressions;
using CoppAddresd.Application.Features.ProgramProgress.PatientSummary;

namespace CoppAddresd.Application.Features.ProgramProgress.WeeklySuggestions;

public static class WeeklySuggestionsContract
{
    public static bool Reference(string? s) => s is not null && Regex.IsMatch(s, @"^[a-zA-Z][a-zA-Z0-9_.:-]{0,79}$");
    public static bool SafeLabel(string? s) => !string.IsNullOrWhiteSpace(s) && s.Length <= 200 && SafeText(s);
    private static bool SafeText(string s) => !Regex.IsMatch(s, @"https?://|<[^>]+>", RegexOptions.IgnoreCase);
    public static bool ValidInput(WeeklySuggestionsInput i)
        => PatientSummaryContract.ValidInput(new(i.StartDate, i.EndDate, i.Language, i.Facts)) && i.Slots is { Count: <= 14 }
            && i.Slots.All(s => s is not null && Reference(s.Ref) && s.Date >= i.StartDate && s.Date <= i.EndDate
                && s.Kind is "routine" or "podcast" && (s.Current is null || !string.IsNullOrWhiteSpace(s.Current) && s.Current.Length <= 2000)
                && s.Options is { Count: <= 10 } && s.Options.All(o => o is not null && Reference(o.Ref) && SafeLabel(o.Label)
                    && (s.Kind == "routine" ? o.Kind is "routine" or "rest" : o.Kind == "podcast"))
                && s.Options.Select(o => o.Ref).Distinct(StringComparer.Ordinal).Count() == s.Options.Count)
            && i.Slots.Select(s => s.Ref).Distinct(StringComparer.Ordinal).Count() == i.Slots.Count
            && i.Slots.Select(s => (s.Date, s.Kind)).Distinct().Count() == i.Slots.Count;
    public static bool ValidResponse(WeeklySuggestionsResponse r, WeeklySuggestionsInput input)
    {
        if (r.Kind != "weekly_plan_suggestions" || !r.IsEstimate || r.GeneratedAt == default || r.GeneratedAt.Offset != TimeSpan.Zero
            || r.Model is not null && (string.IsNullOrWhiteSpace(r.Model) || r.Model.Length > 120)
            || r.Suggestions is not { Count: <= 5 }) return false;
        if (r.Status == "insufficient") return r.Suggestions.Count == 0 && r.Model is null;
        if (r.Status != "ok") return false;
        var sources = input.Facts.Select(f => f.Ref).ToHashSet(StringComparer.Ordinal);
        return r.Suggestions.Select(s => s?.SlotRef).Distinct(StringComparer.Ordinal).Count() == r.Suggestions.Count
            && r.Suggestions.All(s => s is not null && input.Slots.Any(slot => slot.Ref == s.SlotRef && slot.Options.Any(o => o.Ref == s.OptionRef))
                && !string.IsNullOrWhiteSpace(s.Reason) && s.Reason.Length <= 1000 && SafeText(s.Reason)
                && s.SourceRefs is { Count: >= 1 and <= 10 } && s.SourceRefs.All(sources.Contains)
                && Regex.Matches(s.Reason, @"\[([^\[\]]+)\]").All(m => s.SourceRefs.Contains(m.Groups[1].Value)));
    }
}
