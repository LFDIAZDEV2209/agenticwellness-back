using System.Text.Json;
using CoppAddresd.Application.Features.ProgramProgress;
using CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent;
using CoppAddresd.Application.Features.ProgramProgress.PatientSummary;
using CoppAddresd.Application.Features.ProgramProgress.WeeklySuggestions;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace CoppAddresd.Infrastructure.Repositories;

/// <summary>Catálogos acotados y mapeo durable; la aplicación usa la transacción del editor semanal existente.</summary>
public sealed class WeeklySuggestionsStore(AppDbContext db, IEffectiveDayContentStore effective, IEffectiveDayContentResolver resolver,
    IPatientSummaryStore summary, IClinicalContextService clinical, ISafetyRulesService safety, IProgramRepository program,
    IPatientScopeGuard scope, ILegalConsentService consent, IConfiguration configuration, TimeProvider clock) : IWeeklySuggestionsStore
{
    private AppDbContext Db => db;
    private IPatientScopeGuard Scope => scope;
    private ILegalConsentService Consent => consent;
    private TimeProvider Clock => clock;
    private sealed record Current(ProgramEnrollment Enrollment, ProgramWeek Week, bool HasHistory, DateOnly Today, string Version);

    private async Task<Current> CurrentAsync(Guid id, int number, CancellationToken ct)
    {
        var enrollment = await db.ProgramEnrollments.AsNoTracking().Include(e => e.Template).ThenInclude(t => t!.DayTemplates)
            .SingleOrDefaultAsync(e => e.Id == id, ct) ?? throw new NotFoundException("Inscripción no encontrada.");
        if (number < 1 || number > enrollment.Template!.TotalWeeks) throw new UnprocessableEntityException("WEEK_NUMBER_OUT_OF_RANGE: semana inválida.");
        var week = await db.ProgramWeeks.AsNoTracking().SingleOrDefaultAsync(w => w.EnrollmentId == id && w.WeekNumber == number, ct)
            ?? throw new NotFoundException("Semana no encontrada.");
        var history = await db.TaskCompletions.AnyAsync(t => t.ProgramWeekId == week.Id, ct) || await db.DailyCheckIns.AnyAsync(d => d.ProgramWeekId == week.Id, ct);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById(enrollment.Timezone)).DateTime);
        var version = WeeklySuggestionsJson.HashValue(new
        {
            enrollment.Id, enrollment.PatientId, enrollmentState = enrollment.Status, enrollment.StartLocalDate, enrollment.Timezone,
            templateVersion = enrollment.Template.Version,
            rows = enrollment.Template.DayTemplates.OrderBy(d => d.WeekNumber).ThenBy(d => d.Weekday).ThenBy(d => d.TaskCode).ThenBy(d => d.Id)
                .Select(d => new { d.WeekNumber, d.Weekday, d.TaskCode, d.Points, d.SortOrder, d.MediaId, d.RoutineId, d.NutritionPlanId }),
            weekId = week.Id, weekState = week.Status, week.TemplateVersionAtStart, week.ActivatedAt, week.CompletedAt, week.UpdatedAt,
            snapshot = week.TasksSnapshot, history,
        });
        return new(enrollment, week, history, today, version);
    }
    private static (DateOnly Start, DateOnly End) Editable(Current current)
    {
        if (current.Enrollment.Status != ProgramEnrollmentStatus.Active) throw new BusinessRuleViolationException("AI_ENROLLMENT_NOT_ACTIVE: inscripción no activa.");
        var start = ProgramWeekTaskEditor.EditableWeekStart(current.Enrollment.StartLocalDate, current.Week, current.Today, current.HasHistory);
        var last = current.Enrollment.StartLocalDate.AddDays(EffectiveDayContentResolver.DurationDays(current.Enrollment.Template!) - 1);
        return (start, start.AddDays(6) < last ? start.AddDays(6) : last);
    }
    public async Task<string> VersionAsync(Guid enrollment, int week, CancellationToken ct)
    {
        var current = await CurrentAsync(enrollment, week, ct);
        try { _ = Editable(current); return current.Version; }
        catch (BusinessRuleViolationException) { return current.Version + ":closed"; }
    }
    public async Task<WeeklySuggestionsCapture> CaptureAsync(Guid enrollment, int week, string language, CancellationToken ct)
    {
        var current = await CurrentAsync(enrollment, week, ct); var (start, end) = Editable(current);
        var patient = current.Enrollment.PatientId; var now = clock.GetUtcNow();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(current.Enrollment.Timezone);
        var since = current.Today.AddDays(-6); var local = since.ToDateTime(TimeOnly.MinValue);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        var utc = zone.IsAmbiguousTime(local) ? new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max()).UtcDateTime : TimeZoneInfo.ConvertTimeToUtc(local, zone);
        // El store de resumen ya usa una transacción de lectura corta; no se extiende hasta la red.
        var facts = PatientSummaryFacts.Build(await summary.ReadAsync(patient, since, current.Today, utc, now.UtcDateTime, ct))
            .Select(f => f with { Text = $"Contexto {since:yyyy-MM-dd} a {current.Today:yyyy-MM-dd}: {f.Text}" }).ToArray();
        var allowRoutine = (await safety.EvaluateAsync(await clinical.ConsolidateAsync(patient, ct), ct)).Count == 0;
        var data = await effective.LoadAsync([enrollment], start, end, ct);
        var days = resolver.ResolveRange(data, current.Enrollment, start, end, ct);
        var templateRoutines = current.Enrollment.Template!.DayTemplates.Where(d => d.RoutineId.HasValue).Select(d => d.RoutineId!.Value).Distinct().ToArray();
        var currentRoutineIds = days.Where(d => d.Routine.Id.HasValue).Select(d => d.Routine.Id!.Value).ToArray();
        var routines = await db.ExerciseRoutines.AsNoTracking().Include(r => r.Exercises)
            .Where(r => templateRoutines.Contains(r.Id) || currentRoutineIds.Contains(r.Id)).OrderBy(r => r.Id).ToListAsync(ct);
        var podcasts = await db.MediaItems.AsNoTracking().Where(m => data.PublishedPodcasts.Contains(m.Id)).OrderBy(m => m.Id).Take(10).ToArrayAsync(ct);
        var slots = new List<WeeklySuggestionSlot>(); var targets = new List<WeeklySuggestionTarget>();
        var defaults = current.Enrollment.Template.DayTemplates.Where(d => d.WeekNumber == null || d.WeekNumber == week)
            .GroupBy(d => (d.Weekday, d.TaskCode)).ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.WeekNumber.HasValue).First());
        var snapshot = WeekSnapshotJson.Parse(current.Week.TasksSnapshot);
        foreach (var day in days)
        {
            var routine = routines.FirstOrDefault(r => r.Id == day.Routine.Id);
            var routineOptions = new List<WeeklySuggestionOption>(); var podcastOptions = new List<WeeklySuggestionOption>();
            var routineRef = $"routine.d{day.Date.DayNumber - start.DayNumber + 1}"; var podcastRef = $"podcast.d{day.Date.DayNumber - start.DayNumber + 1}";
            defaults.TryGetValue((day.Weekday, TaskCode.ejercicio), out var routineDefault);
            defaults.TryGetValue((day.Weekday, TaskCode.podcast), out var podcastDefault);
            var routinePrior = snapshot.FirstOrDefault(t => t.Weekday == day.Weekday && t.TaskCode == "ejercicio");
            var podcastPrior = snapshot.FirstOrDefault(t => t.Weekday == day.Weekday && t.TaskCode == "podcast");
            if (allowRoutine && !day.Routine.Rest && routine is not null && routine.Status == NutritionPlanStatus.Active && string.IsNullOrWhiteSpace(routine.Contraindications))
            {
                foreach (var candidate in routines.Where(r => templateRoutines.Contains(r.Id) && r.Id != routine.Id && r.Status == NutritionPlanStatus.Active
                    && string.IsNullOrWhiteSpace(r.Contraindications) && (int)r.Difficulty <= (int)routine.Difficulty && WeeklySuggestionsContract.SafeLabel(r.Name)).Take(9))
                {
                    var reference = $"r{routineOptions.Count + 1}";
                    routineOptions.Add(new(reference, "routine", candidate.Name));
                    targets.Add(new(routineRef, reference, day.Date, "routine", "routine", candidate.Id, candidate.Name,
                        RoutineVersion(candidate), routinePrior?.Points ?? routineDefault?.Points ?? 0, routinePrior?.SortOrder ?? routineDefault?.SortOrder ?? 0));
                }
                if (bool.TryParse(configuration[ProgramRestDayPolicy.ConfigurationKey], out var enabled) && enabled)
                {
                    var label = language == "en" ? "Rest" : "Descanso"; routineOptions.Add(new("rest", "rest", label));
                    targets.Add(new(routineRef, "rest", day.Date, "routine", "rest", null, label, "rest-enabled", routinePrior?.Points ?? routineDefault?.Points ?? 0, routinePrior?.SortOrder ?? routineDefault?.SortOrder ?? 0));
                }
            }
            foreach (var candidate in podcasts.Where(p => p.Id != day.Podcast.Id && WeeklySuggestionsContract.SafeLabel(p.Title)))
            {
                var reference = $"p{podcastOptions.Count + 1}"; podcastOptions.Add(new(reference, "podcast", candidate.Title));
                targets.Add(new(podcastRef, reference, day.Date, "podcast", "podcast", candidate.Id, candidate.Title,
                    PodcastVersion(candidate), podcastPrior?.Points ?? podcastDefault?.Points ?? 0, podcastPrior?.SortOrder ?? podcastDefault?.SortOrder ?? 0));
            }
            slots.Add(new(routineRef, day.Date, "routine", day.Routine.Rest ? (language == "en" ? "Rest" : "Descanso")
                : routine is not null && WeeklySuggestionsContract.SafeLabel(routine.Name) ? routine.Name : null, routineOptions));
            slots.Add(new(podcastRef, day.Date, "podcast", null, podcastOptions));
        }
        return new(patient, enrollment, week, current.Version, new(start, end, language, facts, slots) { SubjectPatientId = patient }, targets,
            facts.Select(f => new PatientSummarySource(f.Ref, f.Section, since, current.Today)).ToArray());
    }
    private static string RoutineVersion(ExerciseRoutine r) => WeeklySuggestionsJson.HashValue(new { r.Id, r.Name, r.Status, r.Difficulty, r.Contraindications,
        r.UpdatedAt, r.MediaId, r.Description, r.EstimatedMinutes, r.TargetMuscles, r.Equipment, r.WarmupNotes, r.CooldownNotes,
        exercises = r.Exercises.OrderBy(e => e.Id).Select(e => new { e.Id, e.Name, e.Description, e.Sets, e.Repetitions, e.RestSeconds, e.DurationSecs,
            e.WeightKg, e.TargetMuscle, e.Equipment, e.Tempo, e.Rpe, e.Tips, e.MediaId, e.SortOrder, e.UpdatedAt }) });
    private static string PodcastVersion(MediaItem p) => WeeklySuggestionsJson.HashValue(new { p.Id, p.Title, p.Status, p.StorageKey, p.UpdatedAt, p.MediaType });
    public Task<PatientAiBrief?> LatestAsync(Guid patient, Guid enrollment, int week, string language, CancellationToken ct)
        => db.PatientAiBriefs.FromSqlInterpolated($"""
            SELECT * FROM app.patient_ai_briefs WHERE patient_id={patient} AND kind='weekly_plan_suggestions' AND state IN ('draft','applied')
              AND draft_json ->> 'enrollmentId' = {enrollment.ToString()} AND draft_json ->> 'weekNumber' = {week.ToString(System.Globalization.CultureInfo.InvariantCulture)} AND draft_json ->> 'language' = {language}
            ORDER BY generated_at DESC, id DESC LIMIT 1
            """).AsNoTracking().SingleOrDefaultAsync(ct);
    public Task<PatientAiBrief?> FindAsync(Guid patient, Guid id, CancellationToken ct)
        => db.PatientAiBriefs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id && r.PatientId == patient && r.Kind == "weekly_plan_suggestions", ct);
    public async Task SaveAsync(PatientAiBrief row, CancellationToken ct) { db.PatientAiBriefs.Add(row); await db.SaveChangesAsync(ct); }

    public async Task<ApplyWeeklySuggestionResult> ApplyAsync(Guid patient, Guid enrollment, int week, Guid brief, int index, Guid actor, CancellationToken ct)
    {
        var row = await FindAsync(patient, brief, ct) ?? throw new NotFoundException("Sugerencia no encontrada.");
        var stored = WeeklySuggestionsAccess.Read(row, enrollment, week);
        if (index < 0 || index >= stored.Suggestions.Count) throw new NotFoundException("Sugerencia no encontrada.");
        var selected = stored.Suggestions[index].Target;
        var guard = new Application(this, patient, enrollment, week, brief, index, actor, selected);
        var weekday = (short)(selected.Date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)selected.Date.DayOfWeek);
        var task = new WeeklyDayTemplate { Weekday = weekday, TaskCode = selected.SlotKind == "podcast" ? TaskCode.podcast : TaskCode.ejercicio,
            Points = selected.Points, SortOrder = selected.SortOrder, RoutineId = selected.Kind == "routine" ? selected.ResourceId : null,
            MediaId = selected.Kind == "podcast" ? selected.ResourceId : null };
        var edits = new EnrollmentWeekTaskEdits(selected.Kind == "rest" && stored.Suggestions[index].AppliedAt is null ? new HashSet<short> { weekday } : new HashSet<short>(), Transaction: guard);
        var detail = await program.ReplaceEnrollmentWeekTasksAsync(enrollment, week, [task], actor, ct, edits)
            ?? throw new NotFoundException("Inscripción no encontrada.");
        return new(guard.Replay ? "already_applied" : "applied", brief, index, detail);
    }

    private sealed class Application(WeeklySuggestionsStore owner, Guid patient, Guid enrollment, int weekNumber, Guid briefId, int index,
        Guid actor, WeeklySuggestionTarget requestedTarget) : IEnrollmentWeekEditTransaction
    {
        private StoredWeeklySuggestions stored = null!;
        public bool Replay { get; private set; }
        public JsonElement AuditMetadata => JsonSerializer.SerializeToElement(new { brief_id = briefId, suggestion_index = index,
            confirmed = true, plan_version = stored.ExpectedPlanVersion, input_hash = inputHash });
        private string inputHash = "";
        public async Task<bool> BeforeAsync(ProgramEnrollment enrollmentRow, ProgramWeek week, CancellationToken ct)
        {
            if (enrollmentRow.PatientId != patient) throw new NotFoundException("Sugerencia no encontrada.");
            await WeeklySuggestionsAccess.EnsureAsync(owner.Scope, enrollment, true, ct);
            await owner.Consent.EnsurePatientAsync(patient, "patient-ai", ct);
            var row = await owner.Db.PatientAiBriefs.FromSqlInterpolated($"SELECT * FROM app.patient_ai_briefs WHERE id={briefId} FOR UPDATE")
                .AsNoTracking().SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Sugerencia no encontrada.");
            if (row.PatientId != patient || row.State is not ("draft" or "applied")) throw new NotFoundException("Sugerencia no encontrada.");
            stored = WeeklySuggestionsAccess.Read(row, enrollment, weekNumber); inputHash = row.InputHash;
            if (index >= stored.Suggestions.Count || stored.Suggestions[index].Target != requestedTarget) throw new BusinessRuleViolationException("AI_DRAFT_CHANGED: recarga las sugerencias.");
            if (stored.Suggestions[index].AppliedAt is not null) { Replay = true; return false; }
            // El editor ya bloqueó inscripción/semana. Se fijan plantilla y recurso para revalidar antes de escribir.
            await owner.Db.ProgramTemplates.FromSqlInterpolated($"SELECT * FROM app.program_templates WHERE id={enrollmentRow.TemplateId} FOR SHARE").AsNoTracking().SingleAsync(ct);
            if (requestedTarget.ResourceId is { } resource)
            {
                if (requestedTarget.Kind == "routine") await owner.Db.ExerciseRoutines.FromSqlInterpolated($"SELECT * FROM app.exercise_routines WHERE id={resource} FOR SHARE").AsNoTracking().SingleOrDefaultAsync(ct);
                else await owner.Db.MediaItems.FromSqlInterpolated($"SELECT * FROM app.media_items WHERE id={resource} FOR SHARE").AsNoTracking().SingleOrDefaultAsync(ct);
            }
            var capture = await owner.CaptureAsync(enrollment, weekNumber, stored.Language, ct);
            if (capture.PlanVersion != stored.ExpectedPlanVersion) throw new BusinessRuleViolationException("AI_PLAN_CHANGED: el plan cambió; vuelve a generar.");
            if (!capture.Targets.Any(t => t == requestedTarget)) throw new BusinessRuleViolationException("AI_OPTION_UNAVAILABLE: la alternativa ya no está disponible; vuelve a generar.");
            await owner.Db.Entry(week).ReloadAsync(ct);
            return true;
        }
        public async Task CompleteAsync(ProgramEnrollment enrollmentRow, ProgramWeek week, CancellationToken ct)
        {
            await owner.Db.Entry(week).ReloadAsync(ct);
            var items = stored.Suggestions.ToArray(); items[index] = items[index] with { AppliedAt = owner.Clock.GetUtcNow(), AppliedBy = actor };
            stored = stored with { Suggestions = items, ExpectedPlanVersion = await owner.VersionAsync(enrollment, weekNumber, ct) };
            var json = JsonSerializer.Serialize(stored, PatientSummaryJson.Options);
            await owner.Db.PatientAiBriefs.Where(r => r.Id == briefId && r.PatientId == patient)
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.DraftJson, json)
                    .SetProperty(r => r.State, items.All(i => i.AppliedAt != null) ? "applied" : "draft"), ct);
        }
    }
}
