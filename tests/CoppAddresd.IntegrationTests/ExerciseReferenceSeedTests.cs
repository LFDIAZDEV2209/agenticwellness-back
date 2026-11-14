using System.Diagnostics;
using System.Text.Json;
using CoppAddresd.Application.DTOs;
using CoppAddresd.Application.Features.Media;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    [NutritionPostgresFact]
    public async Task SemillaDemostraciones_CuatroMedios_EnlacesExactos_ConcurrenteIdempotenteYConservaSubidos()
    {
        var root = ExerciseSeedRoot();
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "scripts", "exercise_reference_videos.json")));
        var videos = manifest.RootElement.EnumerateArray().ToArray(); Assert.Equal(4, videos.Length);
        var appFile = Path.GetFullPath(Path.Combine(root, "..", "antares-paciente", "src", "data", "exerciseVideos.ts"));
        var appSource = File.Exists(appFile) ? await File.ReadAllTextAsync(appFile) : null;
        await using var db = Context();
        var uploaded = new MediaItem { Id = Guid.NewGuid(), Title = "Técnica personalizada", Author = "Equipo", MediaType = MediaType.Video,
            Status = MediaStatus.Published, StorageKey = "media/personal.mp4" };
        db.MediaItems.Add(uploaded);
        var routine = new ExerciseRoutine { Id = Guid.NewGuid(), Name = "Referencias", Status = NutritionPlanStatus.Active };
        var aliasToKey = new Dictionary<string, string>();
        foreach (var video in videos)
        {
            var url = video.GetProperty("embedUrl").GetString()!;
            var key = ExternalMediaRules.StorageKey("external", ExternalMediaRules.ProviderFor(url), url, MediaType.Video, null);
            if (appSource is not null) { Assert.Contains(url, appSource); Assert.Contains(video.GetProperty("sourceUrl").GetString()!, appSource); }
            foreach (var alias in video.GetProperty("aliases").EnumerateArray())
            {
                var name = alias.GetString()!;
                Assert.Equal(url, ExerciseReferenceVideo.ForExercise(name)!.EmbedUrl);
                aliasToKey.Add(name, key);
                routine.Exercises.Add(new() { Id = Guid.NewGuid(), Name = name, SortOrder = routine.Exercises.Count + 1 });
            }
        }
        var preserved = new RoutineExercise { Id = Guid.NewGuid(), Name = "Plancha", MediaId = uploaded.Id, SortOrder = 20 };
        var variant = new RoutineExercise { Id = Guid.NewGuid(), Name = "Plancha lateral", SortOrder = 21 };
        var walking = new RoutineExercise { Id = Guid.NewGuid(), Name = "Estocadas caminando", SortOrder = 22 };
        routine.Exercises.Add(preserved); routine.Exercises.Add(variant); routine.Exercises.Add(walking);
        db.ExerciseRoutines.Add(routine); await db.SaveChangesAsync();
        var simulation = await ExerciseSeedScriptAsync("simulate");
        Assert.Equal(0, simulation.GetProperty("escrituras").GetInt32());
        Assert.Equal(4, simulation.GetProperty("counts").GetProperty("medios_propuestos").GetInt32());
        Assert.Equal(aliasToKey.Count, simulation.GetProperty("counts").GetProperty("enlaces_propuestos").GetInt32());
        Assert.Equal(1, await db.MediaItems.CountAsync());
        var concurrent = await Task.WhenAll(ExerciseSeedScriptAsync("apply"), ExerciseSeedScriptAsync("apply"));
        Assert.Equal(new[] { 0, 4 + aliasToKey.Count }, concurrent.Select(r => r.GetProperty("escrituras").GetInt32()).Order());
        Assert.Equal(5, await db.MediaItems.CountAsync());
        var external = await db.MediaItems.AsNoTracking().Where(m => m.SourceKind == "external").ToDictionaryAsync(m => m.StorageKey);
        Assert.Equal(4, external.Count);
        foreach (var item in external.Values)
        {
            Assert.Equal(MediaStatus.Published, item.Status); Assert.Equal(MediaType.Video, item.MediaType);
            Assert.Equal(item.StorageKey, ExternalMediaRules.StorageKey(item.SourceKind, ExternalMediaRules.ProviderFor(item.ExternalUrl), item.ExternalUrl, item.MediaType, null));
        }
        var exercises = await db.RoutineExercises.AsNoTracking().Where(e => e.RoutineId == routine.Id).ToArrayAsync();
        foreach (var exercise in exercises.Where(e => aliasToKey.ContainsKey(e.Name)))
            Assert.Equal(external[aliasToKey[exercise.Name]].Id, exercise.MediaId);
        Assert.Equal(uploaded.Id, exercises.Single(e => e.Id == preserved.Id).MediaId);
        Assert.Null(exercises.Single(e => e.Id == variant.Id).MediaId); Assert.Null(exercises.Single(e => e.Id == walking.Id).MediaId);
        var timestamps = exercises.OrderBy(e => e.Id).Select(e => e.UpdatedAt).ToArray();
        Assert.Equal(0, (await ExerciseSeedScriptAsync("apply")).GetProperty("escrituras").GetInt32());
        Assert.Equal(timestamps, await db.RoutineExercises.AsNoTracking().Where(e => e.RoutineId == routine.Id).OrderBy(e => e.Id).Select(e => e.UpdatedAt).ToArrayAsync());
        // La vinculación no debe enviar external/* a storage ni al reproductor de archivos.
        var seed = await EffectiveSeedAsync(db);
        await db.RoutineExercises.Where(e => e.RoutineId == routine.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.RoutineId, seed.Routine));
        var snapshot = await EffectiveRepository(db).GetSnapshotAsync(seed.Enrollment, seed.Start, CancellationToken.None);
        var delivered = Assert.Single(snapshot!.TodayTasks, t => t.TaskCode == CoppAddresd.Domain.Enums.ProgramProgress.TaskCode.ejercicio).Content!.Exercises!;
        foreach (var exercise in delivered.Where(e => aliasToKey.ContainsKey(e.Name) && e.MediaId != uploaded.Id))
        {
            Assert.NotNull(exercise.MediaId); Assert.Null(exercise.MediaUrl);
            Assert.Equal(ExerciseReferenceVideo.ForExercise(exercise.Name), exercise.ReferenceVideo);
        }
        var personal = Assert.Single(delivered, e => e.MediaId == uploaded.Id);
        Assert.Equal(uploaded.StorageKey, personal.MediaUrl); Assert.Null(personal.ReferenceVideo);
        var retired = external.Values.First();
        await db.MediaItems.Where(m => m.Id == retired.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, MediaStatus.Archived));
        var repeated = await ExerciseSeedScriptAsync("apply");
        Assert.Equal(0, repeated.GetProperty("escrituras").GetInt32());
        Assert.Equal(1, repeated.GetProperty("counts").GetProperty("medios_no_publicados_conservados").GetInt32());
        Assert.Equal(MediaStatus.Archived, await db.MediaItems.Where(m => m.Id == retired.Id).Select(m => m.Status).SingleAsync());
        var afterArchive = await EffectiveRepository(db).GetSnapshotAsync(seed.Enrollment, seed.Start, CancellationToken.None);
        var archivedNames = aliasToKey.Where(pair => pair.Value == retired.StorageKey).Select(pair => pair.Key).ToArray();
        foreach (var exercise in afterArchive!.TodayTasks.Single(t => t.TaskCode == CoppAddresd.Domain.Enums.ProgramProgress.TaskCode.ejercicio).Content!.Exercises!.Where(e => archivedNames.Contains(e.Name)))
        {
            Assert.Null(exercise.MediaUrl); Assert.Null(exercise.MediaId); Assert.Null(exercise.ReferenceVideo);
        }
    }

    private static string ExerciseSeedRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "scripts", "seed_exercise_reference_media.py"))) directory = directory.Parent;
        Assert.NotNull(directory); return directory.FullName;
    }

    private async Task<JsonElement> ExerciseSeedScriptAsync(string mode)
    {
        var start = new ProcessStartInfo("python") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(Path.Combine(ExerciseSeedRoot(), "scripts", "seed_exercise_reference_media.py"));
        start.ArgumentList.Add("--mode"); start.ArgumentList.Add(mode);
        var builder = new NpgsqlConnectionStringBuilder(connection);
        start.Environment["PGHOST"] = builder.Host; start.Environment["PGPORT"] = builder.Port.ToString();
        start.Environment["PGDATABASE"] = builder.Database; start.Environment["PGUSER"] = builder.Username; start.Environment["PGPASSWORD"] = builder.Password;
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync(); var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, error);
        return JsonSerializer.Deserialize<JsonElement>(output);
    }
}
