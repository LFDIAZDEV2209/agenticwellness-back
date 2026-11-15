using System.Text.Json;
using CoppAddresd.Application.Features.LegalDocuments;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Exceptions;
using FluentValidation;
using MediatR;

namespace CoppAddresd.Application.Features.ProgramProgress.PatientSummary;

public sealed record GeneratePatientSummary(Guid PatientId, string Language = "es") : IRequest<PatientSummaryResult>;
public sealed record GetPatientSummary(Guid PatientId, string Language = "es") : IRequest<PatientSummaryResult>;

/// <summary>Generación humana a pedido; la llamada externa nunca mantiene una transacción de escritura.</summary>
public sealed class GeneratePatientSummaryHandler(IPatientScopeGuard scope, ILegalConsentService consent,
    IPatientSummaryStore store, IPatientSummaryClient client, TimeProvider clock) : IRequestHandler<GeneratePatientSummary, PatientSummaryResult>
{
    public async Task<PatientSummaryResult> Handle(GeneratePatientSummary request, CancellationToken ct)
    {
        await PatientSummaryAuthorization.EnsureAsync(scope, request.PatientId, true, ct);
        PatientSummaryAuthorization.Language(request.Language);
        try
        {
            await consent.EnsurePatientAsync(request.PatientId, LegalConsentScopes.PatientAi, ct);
            var now = clock.GetUtcNow();
            var zone = TimeZoneInfo.FindSystemTimeZoneById(await store.TimezoneAsync(request.PatientId, ct));
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
            var start = today.AddDays(-6);
            var localStart = start.ToDateTime(TimeOnly.MinValue);
            while (zone.IsInvalidTime(localStart)) localStart = localStart.AddMinutes(1);
            var utcStart = zone.IsAmbiguousTime(localStart)
                ? new DateTimeOffset(localStart, zone.GetAmbiguousTimeOffsets(localStart).Max()).UtcDateTime
                : TimeZoneInfo.ConvertTimeToUtc(localStart, zone);
            var data = await store.ReadAsync(request.PatientId, start, today, utcStart, now.UtcDateTime, ct);
            var facts = PatientSummaryFacts.Build(data);
            if (facts.Count == 0) return new("insufficient");
            var input = new PatientSummaryInput(start, today, request.Language, facts) { SubjectPatientId = request.PatientId };
            var response = await client.GenerateAsync(input, ct);
            // Durante la red se pueden revocar permiso, asignación o consentimiento.
            await PatientSummaryAuthorization.EnsureAsync(scope, request.PatientId, true, ct);
            await consent.EnsurePatientAsync(request.PatientId, LegalConsentScopes.PatientAi, ct);
            if (response.Status == "insufficient") return new("insufficient");
            var stored = new StoredPatientSummary(request.Language, start, today, response,
                facts.Select(f => new PatientSummarySource(f.Ref, f.Section, start, today)).ToArray());
            var row = new PatientAiBrief { PatientId = request.PatientId, Kind = "patient_summary", State = "draft",
                DraftJson = JsonSerializer.Serialize(stored, PatientSummaryJson.Options), ModelName = response.Model,
                GeneratedAt = response.GeneratedAt.UtcDateTime, InputHash = PatientSummaryJson.Hash(input) };
            ct.ThrowIfCancellationRequested();
            await store.SaveAsync(row, ct);
            return new("ok", PatientSummaryAuthorization.Draft(row, stored));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is PatientSummaryUnavailableException or AiConsentException)
        { return new("unavailable"); }
    }
}

/// <summary>Hechos desidentificados reutilizados por los asistentes del Perfil 360.</summary>
public static class PatientSummaryFacts
{
    public static IReadOnlyList<PatientSummaryFact> Build(PatientSummaryData d)
    {
        var facts = new List<PatientSummaryFact>(6);
        if (d.ProgramState is not null) facts.Add(new("program.current", "program",
            $"Estado registrado del programa: {d.ProgramState}. Semana registrada: {d.Week}. Fecha inicial: {d.ProgramStart:yyyy-MM-dd}."));
        if (d.NutritionRecords > 0) facts.Add(new("nutrition.records", "nutrition",
            $"Hay {d.NutritionRecords} registros de comida en {d.NutritionDays} días del intervalo. Son registros, no una medida de cumplimiento del plan."));
        if (d.ExerciseCompletions > 0) facts.Add(new("exercise.completions", "exercise", $"Hay {d.ExerciseCompletions} tareas de ejercicio registradas como completadas en el intervalo."));
        if (d.PodcastCompletions > 0) facts.Add(new("podcasts.completions", "podcasts", $"Hay {d.PodcastCompletions} tareas de podcast registradas como completadas en el intervalo."));
        if (d.CompletedTests > 0) facts.Add(new("tests.completed", "tests", $"Hay {d.CompletedTests} evaluaciones de tests completadas en el intervalo. No se incluyen sus respuestas ni diagnósticos."));
        if (d.Measurements > 0) facts.Add(new("measurements.recorded", "measurements", $"Hay {d.Measurements} mediciones clínicas observadas y registradas en el intervalo. No se infiere normalidad ni cambio clínico."));
        return facts;
    }
}

/// <summary>Lectura independiente de IA: nunca genera ni reintenta ni cambia el Perfil 360.</summary>
public sealed class GetPatientSummaryHandler(IPatientScopeGuard scope, ILegalConsentService consent, IPatientSummaryStore store)
    : IRequestHandler<GetPatientSummary, PatientSummaryResult>
{
    public async Task<PatientSummaryResult> Handle(GetPatientSummary request, CancellationToken ct)
    {
        await PatientSummaryAuthorization.EnsureAsync(scope, request.PatientId, false, ct);
        PatientSummaryAuthorization.Language(request.Language);
        try
        {
            await consent.EnsurePatientAsync(request.PatientId, LegalConsentScopes.PatientAi, ct);
            var row = await store.LatestAsync(request.PatientId, request.Language, ct);
            if (row is null) return new("empty");
            var stored = JsonSerializer.Deserialize<StoredPatientSummary>(row.DraftJson, PatientSummaryJson.Options);
            return stored is null || stored.Sources is null || stored.Language != request.Language
                || stored.Response is null || stored.Response.Status != "ok"
                || !PatientSummaryContract.ValidResponse(stored.Response, stored.Sources.Select(s => s.Ref))
                ? new("unavailable") : new("ok", PatientSummaryAuthorization.Draft(row, stored));
        }
        catch (Exception ex) when (ex is JsonException or AiConsentException) { return new("unavailable"); }
    }
}

internal static class PatientSummaryAuthorization
{
    public static async Task EnsureAsync(IPatientScopeGuard scope, Guid patient, bool generate, CancellationToken ct)
    {
        await scope.EnsureAsync(patient, "Program.Profile360View", ct);
        await scope.EnsureAsync(patient, "Program.AiView", ct);
        if (generate) await scope.EnsureAsync(patient, "Program.AiManage", ct);
    }
    public static void Language(string language)
    { if (language is not ("es" or "en")) throw new ValidationException("Idioma inválido; usa es o en."); }
    public static PatientSummaryDraft Draft(PatientAiBrief row, StoredPatientSummary stored)
        => new(row.Id, "patient_summary", true, stored.Response.Summary, stored.Response.SourceRefs,
            stored.Response.Attention, row.ModelName, new DateTimeOffset(row.GeneratedAt, TimeSpan.Zero),
            stored.Language, stored.StartDate, stored.EndDate, stored.Sources);
}
