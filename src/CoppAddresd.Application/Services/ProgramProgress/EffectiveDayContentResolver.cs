using System.Text.Json;
using CoppAddresd.Application.Features.Wellness;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Exceptions;

namespace CoppAddresd.Application.Services.ProgramProgress;

public sealed record EffectiveContentRef(Guid? Id, string Source, int? DayNumber = null, bool Prescription = false)
{
    public bool Missing => Id is null;
    public static EffectiveContentRef Gap { get; } = new(null, "missing");
}
public sealed record EffectiveRoutine(Guid? Id, bool Rest, string Source)
{
    public bool Missing => !Rest && Id is null;
}
public sealed record EffectiveDayContent(Guid EnrollmentId, Guid PatientId, DateOnly Date,
    int WeekNumber, short Weekday, EffectiveContentRef Podcast, EffectiveRoutine Routine, EffectiveContentRef Nutrition)
{
    public bool HasGaps => Podcast.Missing || Routine.Missing || Nutrition.Missing;
}
public sealed record EffectivePrescription(Guid Id, Guid PatientId, DateOnly Start, DateOnly End,
    DateTime? PublishedAt, IReadOnlyCollection<int> MenuDays);
public sealed record EffectivePlan(NutritionPlan Plan)
{
    public Guid Id => Plan.Id;
    public bool IsTemplate => Plan.IsTemplate && Plan.PatientId is null;
    public IReadOnlyCollection<int> MenuDays { get; } = Plan.Days.Select(d => (int)d.DayNumber).Distinct().ToArray();
}
public sealed record EffectiveLegacyAssignment(Guid PatientId, Guid ContentId, DateOnly Start, DateOnly? End, bool Nutrition);

/// <summary>Datos de lectura acotados por pacientes/rango, precargados sin consultas dentro del calendario.</summary>
public sealed record EffectiveContentData
{
    public IReadOnlyList<ProgramEnrollment> Enrollments { get; init; } = [];
    public IReadOnlyList<ProgramWeek> Weeks { get; init; } = [];
    public IReadOnlySet<Guid> PublishedPodcasts { get; init; } = new HashSet<Guid>();
    public IReadOnlySet<Guid> ActiveRoutines { get; init; } = new HashSet<Guid>();
    public IReadOnlyList<EffectivePrescription> Prescriptions { get; init; } = [];
    public IReadOnlyList<EffectivePlan> Plans { get; init; } = [];
    public IReadOnlyList<EffectiveLegacyAssignment> Legacy { get; init; } = [];
    public IReadOnlyDictionary<Guid, Guid> EngineNutrition { get; init; } = new Dictionary<Guid, Guid>();
}

public interface IEffectiveDayContentStore
{
    Task<EffectiveContentData> LoadPatientAsync(Guid patientId, DateOnly from, DateOnly to, CancellationToken ct);
    Task<EffectiveContentData> LoadAsync(IReadOnlyCollection<Guid> enrollmentIds, DateOnly from, DateOnly to, CancellationToken ct);
}
public interface IEffectiveDayContentResolver
{
    Task<EffectiveDayContent> ResolveAsync(Guid enrollmentId, DateOnly date, CancellationToken ct);
    IReadOnlyList<EffectiveDayContent> ResolveRange(EffectiveContentData data, ProgramEnrollment enrollment, DateOnly from, DateOnly to, CancellationToken ct);
    EffectiveDayContent Resolve(EffectiveContentData data, ProgramEnrollment enrollment, DateOnly date, CancellationToken ct);
}

/// <summary>Única precedencia de ARQUITECTURA §5.1; funciona sin materializar semanas ni exigir tests.</summary>
public sealed class EffectiveDayContentResolver(IEffectiveDayContentStore store) : IEffectiveDayContentResolver
{
    public async Task<EffectiveDayContent> ResolveAsync(Guid enrollmentId, DateOnly date, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var data = await store.LoadAsync([enrollmentId], date, date, ct);
        var enrollment = data.Enrollments.SingleOrDefault(e => e.Id == enrollmentId)
            ?? throw new NotFoundException("Inscripción no encontrada.");
        return Resolve(data, enrollment, date, ct);
    }

    public static int DurationDays(ProgramTemplate template) => template.TotalWeeks == 12 ? 83 : checked(template.TotalWeeks * 7);

    public IReadOnlyList<EffectiveDayContent> ResolveRange(EffectiveContentData data, ProgramEnrollment enrollment, DateOnly from, DateOnly to, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var template = enrollment.Template ?? throw new NotFoundException("Plantilla no encontrada.");
        var start = from > enrollment.StartLocalDate ? from : enrollment.StartLocalDate;
        var end = enrollment.StartLocalDate.AddDays(DurationDays(template) - 1);
        if (to < end) end = to;
        var scoped = data with
        {
            Weeks = data.Weeks.Where(w => w.EnrollmentId == enrollment.Id).ToArray(),
            Prescriptions = data.Prescriptions.Where(p => p.PatientId == enrollment.PatientId).ToArray(),
            Legacy = data.Legacy.Where(a => a.PatientId == enrollment.PatientId).ToArray(),
        };
        var snapshots = scoped.Weeks.ToDictionary(w => w.WeekNumber, w => WeekSnapshotJson.Parse(w.TasksSnapshot));
        var result = new List<EffectiveDayContent>();
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            ct.ThrowIfCancellationRequested();
            var number = (date.DayNumber - enrollment.StartLocalDate.DayNumber) / 7 + 1;
            result.Add(ResolveCore(scoped, enrollment, date, snapshots.GetValueOrDefault(number) ?? [], scoped.Weeks.FirstOrDefault(w => w.WeekNumber == number), ct));
        }
        return result;
    }

    public EffectiveDayContent Resolve(EffectiveContentData data, ProgramEnrollment enrollment, DateOnly date, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var number = (date.DayNumber - enrollment.StartLocalDate.DayNumber) / 7 + 1;
        var week = data.Weeks.FirstOrDefault(w => w.EnrollmentId == enrollment.Id && w.WeekNumber == number);
        return ResolveCore(data, enrollment, date, week is null ? [] : WeekSnapshotJson.Parse(week.TasksSnapshot), week, ct);
    }

    private static EffectiveDayContent ResolveCore(EffectiveContentData data, ProgramEnrollment enrollment, DateOnly date,
        IReadOnlyList<WeekSnapshotTaskRow> snapshot, ProgramWeek? week, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var template = enrollment.Template ?? throw new NotFoundException("Plantilla no encontrada.");
        var day = date.DayNumber - enrollment.StartLocalDate.DayNumber;
        if (day < 0 || day >= DurationDays(template))
            throw new UnprocessableEntityException("PROGRAM_DAY_OUT_OF_RANGE: fecha fuera del programa.");
        var weekNumber = day / 7 + 1;
        var weekday = (short)(date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek);
        var rows = snapshot.Where(t => t.Weekday == weekday).ToArray();
        var days = template.DayTemplates.Where(t => t.Weekday == weekday && (t.WeekNumber is null || t.WeekNumber == weekNumber))
            .OrderByDescending(t => t.WeekNumber.HasValue).ThenBy(t => t.SortOrder).ToArray();
        EffectiveContentRef podcast = EffectiveContentRef.Gap;
        // Los ítems antiguos sin origen conservan la precedencia individual hasta su clasificación.
        var frozen = week is not null && (week.Status != CoppAddresd.Domain.Enums.ProgramProgress.ProgramWeekStatus.Locked
            || week.ActivatedAt.HasValue || week.CompletedAt.HasValue);
        // Decisión de Luis 13-11: una semana iniciada conserva su podcast publicado,
        // incluso program. Solo las futuras vuelven a la plantilla vigente.
        var individual = rows.FirstOrDefault(t => t.TaskCode == "podcast" && (t.Origin != "program" || frozen));
        if (individual?.MediaId is { } personal && data.PublishedPodcasts.Contains(personal))
            podcast = new(personal, individual.Origin == "program" ? "snapshot" : "individual");
        else
        {
            foreach (var row in days.Where(t => t.TaskCode.ToString() == "podcast"))
                if (row.MediaId is { } id && data.PublishedPodcasts.Contains(id))
                { podcast = new(id, row.WeekNumber.HasValue ? "template_week" : "template_generic"); break; }
        }

        var routine = EffectiveRoutineResolver.Resolve(data, enrollment.PatientId, date, rows, days);

        var published = data.Prescriptions.Where(p => p.PatientId == enrollment.PatientId && p.Start <= date
                && (p.PublishedAt is null || LocalDate(p.PublishedAt.Value, enrollment.Timezone) <= date))
            .OrderByDescending(p => p.Start).ThenByDescending(p => p.PublishedAt).ThenByDescending(p => p.Id).ToArray();
        var current = published.FirstOrDefault(p => p.End >= date);
        EffectiveContentRef nutrition;
        if (current is not null && current.MenuDays.Contains(date.DayNumber - current.Start.DayNumber + 1)) nutrition = new(current.Id, "prescription", date.DayNumber - current.Start.DayNumber + 1, true);
        else
        {
            var previous = published.FirstOrDefault(p => p.End < date && p.MenuDays.Contains(p.End.DayNumber - p.Start.DayNumber + 1));
            // Un sucesor ya iniciado bloquea continuidad de ciclos anteriores, aunque su menú esté incompleto.
            if (previous is not null && !published.Any(p => p.Start > previous.Start))
                nutrition = new(previous.Id, "continuity", previous.End.DayNumber - previous.Start.DayNumber + 1, true);
            else
            {
                var engineId = data.EngineNutrition.GetValueOrDefault(enrollment.Id);
                var engine = data.Plans.FirstOrDefault(p => p.Id == engineId && p.IsTemplate && p.Plan.Status == NutritionPlanStatus.Active && p.MenuDays.Contains(weekday));
                // El motor existente declara useTemplateNutritionDefault cuando no selecciona una regla.
                engine ??= days.Where(d => d.TaskCode.ToString() == "nut").Select(d => data.Plans.FirstOrDefault(p => p.Id == d.NutritionPlanId && p.IsTemplate && p.Plan.Status == NutritionPlanStatus.Active && p.MenuDays.Contains(weekday)))
                    .FirstOrDefault(p => p is not null);
                if (engine is not null) nutrition = new(engine.Id, "engine", weekday);
                else
                {
                    nutrition = EffectiveContentRef.Gap;
                    foreach (var assignment in data.Legacy.Where(a => a.PatientId == enrollment.PatientId && a.Nutrition && Covers(a, date))
                        .OrderByDescending(a => a.Start).ThenBy(a => a.ContentId))
                    {
                        var legacy = data.Plans.FirstOrDefault(p => p.Id == assignment.ContentId && p.Plan.PatientId == enrollment.PatientId && !p.Plan.IsTemplate);
                        if (legacy is null) continue;
                        // El legado de 7/30 menús usa días transcurridos y aprobación exacta, nunca weekday.
                        var menuDay = NutritionPlanReviewRules.MenuDay(legacy.Plan, assignment.Start.ToDateTime(TimeOnly.MinValue), date);
                        if (menuDay.HasValue && legacy.MenuDays.Contains(menuDay.Value))
                        { nutrition = new(legacy.Id, "legacy", menuDay.Value); break; }
                    }
                }
            }
        }
        return new(enrollment.Id, enrollment.PatientId, date, weekNumber, weekday, podcast, routine, nutrition);
    }

    private static bool Covers(EffectiveLegacyAssignment assignment, DateOnly date) => assignment.Start <= date && (assignment.End is null || assignment.End >= date);
    private static DateOnly LocalDate(DateTime utc, string timezone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.FindSystemTimeZoneById(timezone)));

    /// <summary>Decisión aplicada; una elección explícitamente vacía no revive la propuesta original.</summary>
    public static Guid? EnginePlanId(string proposal)
    {
        try
        {
            using var json = JsonDocument.Parse(proposal);
            var content = json.RootElement;
            if (content.ValueKind != JsonValueKind.Object) return null;
            if (content.TryGetProperty("appliedContent", out var applied)) content = applied;
            return content.ValueKind == JsonValueKind.Object && content.TryGetProperty("nutritionPlan", out var plan) && plan.ValueKind == JsonValueKind.Object
                && plan.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out var value) ? value : null;
        }
        catch (JsonException) { return null; }
    }
}
