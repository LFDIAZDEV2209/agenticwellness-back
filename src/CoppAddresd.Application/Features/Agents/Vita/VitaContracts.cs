using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoppAddresd.Application.DTOs.ProgramProgress;

namespace CoppAddresd.Application.Features.Agents.Vita;

/// <summary>Respuesta mínima por capacidad; asOf indica lectura, no actualización clínica.</summary>
public sealed record VitaContextDto(string Status, DateTimeOffset AsOf, DateOnly? LocalDate,
    string? ResourceVersion, JsonElement? Data, IReadOnlyList<VitaActionDto> Actions,
    IReadOnlyList<VitaSourceDto> Sources);

/// <summary>Destino cerrado de la app; nunca contiene URL, código o identidad de paciente.</summary>
public sealed record VitaActionDto(string Type, string Destination,
    [property: JsonPropertyName("resourceId")] Guid? ResourceId,
    [property: JsonPropertyName("localDate")] DateOnly LocalDate,
    [property: JsonPropertyName("resourceVersion")] string ResourceVersion);
public sealed record VitaSourceDto(string Capability, string CaseUse);
public sealed record VitaDayDto(Guid EnrollmentId, int WeekNumber, string WeekStatus,
    bool Rest, IReadOnlyList<VitaTaskDto> Tasks);
public sealed record VitaTaskDto(string TaskCode, string Title, string Status,
    bool ContentUnavailable, Guid? ResourceId);
public sealed record VitaPodcastDto(Guid MediaId, string? Title, string? Description,
    string? Author, int? DurationSecs, IReadOnlyList<PodcastChapterDto>? Chapters,
    IReadOnlyList<string>? Takeaways);
public sealed record VitaExerciseDto(bool Rest, Guid? RoutineId, string? Name,
    IReadOnlyList<VitaExerciseItemDto> Exercises);
public sealed record VitaExerciseItemDto(string Name, string? Description, int? Sets,
    int? Repetitions, int? DurationSecs, int? RestSeconds, string? Tips,
    decimal? WeightKg, string? Tempo, int? Rpe);

/// <summary>Serialización canónica para revisiones opacas de los datos visibles.</summary>
public static class VitaVersions
{
    public static JsonSerializerOptions JsonOptions { get; } = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
    public static string Hash(object value) => Convert.ToHexStringLower(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions)));
    public static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, JsonOptions);
}
