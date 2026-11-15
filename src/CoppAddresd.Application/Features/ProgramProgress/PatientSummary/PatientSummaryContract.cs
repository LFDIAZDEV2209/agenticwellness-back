using System.Text.RegularExpressions;

namespace CoppAddresd.Application.Features.ProgramProgress.PatientSummary;

/// <summary>La frontera de confianza rechaza referencias inventadas, HTML y URLs del proveedor.</summary>
public static class PatientSummaryContract
{
    private static bool Ref(string? value) => value is not null && Regex.IsMatch(value, @"^[a-zA-Z][a-zA-Z0-9_.:-]{0,79}$");
    private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 2000;
    private static bool Safe(string value) => !Regex.IsMatch(value, @"https?://|<[^>]+>", RegexOptions.IgnoreCase);
    private static HashSet<string> Cited(string value) => Regex.Matches(value, @"\[([^\[\]]+)\]").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
    public static bool ValidInput(PatientSummaryInput input)
        => input.Language is "es" or "en" && input.EndDate.DayNumber - input.StartDate.DayNumber is >= 0 and < 7
            && input.Facts is { Count: <= 60 } && input.Facts.All(f => f is not null && Ref(f.Ref) && Text(f.Text)
                && f.Section is "program" or "tests" or "nutrition" or "exercise" or "podcasts" or "measurements" or "alerts")
            && input.Facts.Select(f => f.Ref).Distinct(StringComparer.Ordinal).Count() == input.Facts.Count;
    public static bool ValidResponse(PatientSummaryResponse r, IEnumerable<string> allowed)
    {
        if (r.Kind != "patient_summary" || !r.IsEstimate || r.GeneratedAt == default || r.GeneratedAt.Offset != TimeSpan.Zero
            || r.Model is not null && (!Text(r.Model) || r.Model.Length > 200)
            || r.SourceRefs is not { Count: <= 60 } || r.Attention is not { Count: <= 5 }) return false;
        if (r.Status == "insufficient") return r.Summary == "" && r.SourceRefs.Count == 0 && r.Attention.Count == 0 && r.Model is null;
        if (r.Status != "ok" || !Text(r.Summary) || !Safe(r.Summary) || r.SourceRefs.Count == 0 || !r.SourceRefs.All(Ref)) return false;
        var known = allowed.ToHashSet(StringComparer.Ordinal);
        var refs = r.SourceRefs.ToHashSet(StringComparer.Ordinal);
        return refs.IsSubsetOf(known) && Cited(r.Summary).SetEquals(refs) && r.Attention.All(a => a is not null
            && Text(a.Text) && Safe(a.Text) && a.SourceRefs is { Count: >= 1 and <= 10 } && a.SourceRefs.All(Ref)
            && a.SourceRefs.All(known.Contains) && Cited(a.Text).IsSubsetOf(a.SourceRefs));
    }
}
