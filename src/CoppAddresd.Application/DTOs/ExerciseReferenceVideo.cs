using System.Globalization;
using System.Text;
using CoppAddresd.Application.Features.Media;

namespace CoppAddresd.Application.DTOs;

/// <summary>Attributed reference demonstrations already curated by the patient app.
/// An uploaded exercise medium takes precedence. Unknown names have no automatic match.</summary>
public sealed record ExerciseReferenceVideo(string EmbedUrl, string SourceUrl, string Author)
{
    /// <summary>Convierte un medio externo publicado al contrato de iframe existente.</summary>
    public static ExerciseReferenceVideo? FromExternal(string name, string? url, string author)
    {
        if (!ExternalMediaRules.TryParseVideo(url, out var provider, out var id)) return null;
        var curated = ForExercise(name);
        if (curated is not null && ExternalMediaRules.TryParseVideo(curated.EmbedUrl, out var curatedProvider, out var curatedId)
            && provider == curatedProvider && id == curatedId) return curated;
        return provider == "youtube"
            ? new($"https://www.youtube-nocookie.com/embed/{id}", $"https://www.youtube.com/watch?v={id}", author)
            : new($"https://player.vimeo.com/video/{id}?dnt=1", $"https://vimeo.com/{id}", author);
    }

    public static ExerciseReferenceVideo? ForExercise(string name)
    {
        var normalized = new string(name.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .ToArray()).ToLowerInvariant().Trim();
        return normalized switch
        {
            "marcha en el lugar" or "marcha en el sitio" => new(
                "https://player.vimeo.com/video/418873416?dnt=1",
                "https://www.lancsteachinghospitals.nhs.uk/trauma-exercises",
                "Lancashire Teaching Hospitals · NHS"),
            "sentarse y levantarse" or "sentarse y levantarse de la silla" => new(
                "https://www.youtube-nocookie.com/embed/0FkRr45YeEQ",
                "https://www.youtube.com/watch?v=0FkRr45YeEQ", "NHS Ayrshire & Arran"),
            "plancha" or "plancha abdominal" or "plancha isometrica" => new(
                "https://www.youtube-nocookie.com/embed/cCMob-mi19U",
                "https://www.youtube.com/watch?v=cCMob-mi19U", "Smart Fit Chile"),
            "estocada" or "estocadas" => new(
                "https://www.youtube-nocookie.com/embed/3U9IbwjkOfk",
                "https://www.youtube.com/watch?v=3U9IbwjkOfk", "Smart Fit Chile"),
            _ => null,
        };
    }
}
