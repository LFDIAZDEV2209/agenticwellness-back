using System.Text.Json;
using CoppAddresd.Domain.Entities;

namespace CoppAddresd.Application.DTOs.ProgramProgress;

/// <summary>Prescripción histórica: referencias de medios estables, nunca URLs firmadas.</summary>
public sealed record CompletedExerciseItem(string Name, int? Sets, int? Reps, Guid? MediaRef,
    string? Description, int? DurationSecs, int? RestSeconds, string? TargetMuscle, string? Equipment,
    string? Tips, int SortOrder, decimal? WeightKg, string? Tempo, int? Rpe);

/// <summary>Contenido de la sesión completada; no se reconstruye desde el catálogo mutable.</summary>
public sealed record CompletedExerciseDetail(Guid? RoutineId, string? Name,
    IReadOnlyList<CompletedExerciseItem> Exercises, DateTimeOffset CapturedAt)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static JsonElement Capture(ExerciseRoutine? routine, DateTimeOffset capturedAt) =>
        JsonSerializer.SerializeToElement(new CompletedExerciseDetail(routine?.Id, routine?.Name,
            routine?.Exercises.OrderBy(e => e.SortOrder).ThenBy(e => e.Id).Select(e => new CompletedExerciseItem(
                e.Name, e.Sets, e.Repetitions, e.MediaId, e.Description, e.DurationSecs, e.RestSeconds, e.TargetMuscle,
                e.Equipment, e.Tips, e.SortOrder, e.WeightKg, e.Tempo, e.Rpe)).ToArray() ?? [], capturedAt), Options);

    public static CompletedExerciseDetail? Read(JsonElement? snapshot)
    {
        if (snapshot is not { ValueKind: JsonValueKind.Object } json) return null;
        try { return json.Deserialize<CompletedExerciseDetail>(Options); }
        catch (JsonException) { return null; }
    }

    public IReadOnlyList<ExerciseItemDto> ToExercises() => Exercises.Select(e => new ExerciseItemDto(
        e.Name, e.Description, e.Sets, e.Reps, e.DurationSecs, e.RestSeconds, e.TargetMuscle, e.Equipment,
        e.Tips, e.SortOrder, MediaId: e.MediaRef, WeightKg: e.WeightKg, Tempo: e.Tempo, Rpe: e.Rpe,
        ReferenceVideo: e.MediaRef is null ? ExerciseReferenceVideo.ForExercise(e.Name) : null)).ToArray();
}
