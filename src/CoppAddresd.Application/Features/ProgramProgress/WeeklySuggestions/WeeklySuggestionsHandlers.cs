using System.Text.Json;
using CoppAddresd.Application.Features.ProgramProgress.PatientSummary;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Exceptions;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CoppAddresd.Application.Features.ProgramProgress.WeeklySuggestions;

public sealed record GenerateWeeklySuggestions(Guid EnrollmentId, int WeekNumber, string Language = "es") : IRequest<WeeklySuggestionsResult>;
public sealed record GetWeeklySuggestions(Guid EnrollmentId, int WeekNumber, string Language = "es") : IRequest<WeeklySuggestionsResult>;
public sealed record ApplyWeeklySuggestion(Guid EnrollmentId, int WeekNumber, Guid BriefId, int Index, bool Confirmed) : IRequest<ApplyWeeklySuggestionResult>;

public sealed class GenerateWeeklySuggestionsHandler(IPatientScopeGuard scope, ILegalConsentService consent, IWeeklySuggestionsStore store,
    IWeeklySuggestionsClient client, ILogger<GenerateWeeklySuggestionsHandler> logger) : IRequestHandler<GenerateWeeklySuggestions, WeeklySuggestionsResult>
{
    public async Task<WeeklySuggestionsResult> Handle(GenerateWeeklySuggestions r, CancellationToken ct)
    {
        var patient = await WeeklySuggestionsAccess.EnsureAsync(scope, r.EnrollmentId, true, ct);
        WeeklySuggestionsAccess.Validate(r.WeekNumber, r.Language);
        try
        {
            await consent.EnsurePatientAsync(patient, "patient-ai", ct);
            var capture = await store.CaptureAsync(r.EnrollmentId, r.WeekNumber, r.Language, ct);
            if (capture.Input.Facts.Count == 0 || !capture.Input.Slots.Any(s => s.Options.Count > 0)) return new("insufficient");
            var response = await client.GenerateAsync(capture.Input, ct);
            await WeeklySuggestionsAccess.EnsureAsync(scope, r.EnrollmentId, true, ct);
            await consent.EnsurePatientAsync(patient, "patient-ai", ct);
            if (response.Status == "insufficient") return new("insufficient");
            if (capture.PlanVersion != await store.VersionAsync(r.EnrollmentId, r.WeekNumber, ct))
                throw new BusinessRuleViolationException("AI_PLAN_CHANGED: el plan cambió durante la generación; vuelve a generar.");
            var stored = new StoredWeeklySuggestions(r.EnrollmentId, r.WeekNumber, r.Language, capture.PlanVersion, capture.PlanVersion,
                capture.Input.StartDate, capture.Input.EndDate, response.Suggestions.Select(s => new StoredWeeklySuggestion(
                    capture.Targets.Single(t => t.SlotRef == s.SlotRef && t.OptionRef == s.OptionRef), s.Reason, s.SourceRefs)).ToArray(), capture.Sources);
            var row = new PatientAiBrief { PatientId = patient, Kind = "weekly_plan_suggestions", State = "draft", ModelName = response.Model,
                GeneratedAt = response.GeneratedAt.UtcDateTime, InputHash = WeeklySuggestionsJson.Hash(capture.Input),
                DraftJson = JsonSerializer.Serialize(stored, PatientSummaryJson.Options) };
            await store.SaveAsync(row, ct);
            return new("ok", WeeklySuggestionsAccess.Draft(row, stored, false));
        }
        catch (Exception ex) when (ex is WeeklySuggestionsUnavailableException or AiConsentException)
        { logger.LogWarning("Program.WeeklySuggestionsUnavailable Cause={Cause}", ex.GetType().Name); return new("unavailable"); }
    }
}

public sealed class GetWeeklySuggestionsHandler(IPatientScopeGuard scope, ILegalConsentService consent, IWeeklySuggestionsStore store)
    : IRequestHandler<GetWeeklySuggestions, WeeklySuggestionsResult>
{
    public async Task<WeeklySuggestionsResult> Handle(GetWeeklySuggestions r, CancellationToken ct)
    {
        var patient = await WeeklySuggestionsAccess.EnsureAsync(scope, r.EnrollmentId, false, ct);
        WeeklySuggestionsAccess.Validate(r.WeekNumber, r.Language);
        try
        {
            await consent.EnsurePatientAsync(patient, "patient-ai", ct);
            var row = await store.LatestAsync(patient, r.EnrollmentId, r.WeekNumber, r.Language, ct);
            if (row is null) return new("empty");
            var stored = WeeklySuggestionsAccess.Read(row, r.EnrollmentId, r.WeekNumber);
            return new("ok", WeeklySuggestionsAccess.Draft(row, stored, stored.ExpectedPlanVersion != await store.VersionAsync(r.EnrollmentId, r.WeekNumber, ct)));
        }
        catch (AiConsentException) { return new("unavailable"); }
    }
}

public sealed class ApplyWeeklySuggestionHandler(IPatientScopeGuard scope, IPatientScopeActor actor, ILegalConsentService consent,
    IWeeklySuggestionsStore store) : IRequestHandler<ApplyWeeklySuggestion, ApplyWeeklySuggestionResult>
{
    public async Task<ApplyWeeklySuggestionResult> Handle(ApplyWeeklySuggestion r, CancellationToken ct)
    {
        var patient = await WeeklySuggestionsAccess.EnsureAsync(scope, r.EnrollmentId, true, ct);
        if (!r.Confirmed) throw new UnprocessableEntityException("HUMAN_CONFIRMATION_REQUIRED: confirma explícitamente la aplicación.");
        if (r.BriefId == Guid.Empty || r.Index is < 0 or > 4 || r.WeekNumber < 1) throw new ValidationException("Sugerencia inválida.");
        var author = actor.UserId is { } user && user != Guid.Empty ? user : throw new ForbiddenException("Falta identidad profesional.");
        await consent.EnsurePatientAsync(patient, "patient-ai", ct);
        return await store.ApplyAsync(patient, r.EnrollmentId, r.WeekNumber, r.BriefId, r.Index, author, ct);
    }
}

public static class WeeklySuggestionsAccess
{
    public static async Task<Guid> EnsureAsync(IPatientScopeGuard scope, Guid enrollment, bool mutate, CancellationToken ct)
    {
        var patient = await scope.EnsureResourceAsync(PatientResource.Enrollment, enrollment, "Program.Profile360View", ct);
        await scope.EnsureAsync(patient, "Program.AiView", ct);
        if (mutate) { await scope.EnsureAsync(patient, "Program.AiManage", ct); await scope.EnsureAsync(patient, "Program.Edit", ct); }
        return patient;
    }
    public static void Validate(int week, string language)
    { if (week < 1 || language is not ("es" or "en")) throw new ValidationException("Semana o idioma inválidos."); }
    public static StoredWeeklySuggestions Read(PatientAiBrief row, Guid enrollment, int week)
    {
        StoredWeeklySuggestions? stored;
        try { stored = JsonSerializer.Deserialize<StoredWeeklySuggestions>(row.DraftJson, PatientSummaryJson.Options); }
        catch (JsonException) { throw new BusinessRuleViolationException("AI_DRAFT_INVALID: vuelve a generar las sugerencias."); }
        if (row.Kind != "weekly_plan_suggestions" || stored is null || stored.EnrollmentId != enrollment || stored.WeekNumber != week
            || stored.Suggestions is null || stored.Suggestions.Count > 5 || stored.Sources is null)
            throw new NotFoundException("Sugerencia no encontrada.");
        return stored;
    }
    public static WeeklySuggestionsDraft Draft(PatientAiBrief row, StoredWeeklySuggestions stored, bool stale)
        => new(row.Id, row.Kind, true, row.State, row.ModelName, new(row.GeneratedAt, TimeSpan.Zero), stored.Language,
            stored.StartDate, stored.EndDate, stored.ExpectedPlanVersion, stale, stored.Suggestions.Select((s, index) => new WeeklySuggestionDto(index,
                s.Target.Date, s.Target.SlotKind, s.Target.Kind, s.Target.ResourceId, s.Target.Label, s.Reason, s.SourceRefs,
                s.AppliedAt is null ? "draft" : "applied", s.AppliedAt, s.AppliedBy)).ToArray(), stored.Sources);
}
