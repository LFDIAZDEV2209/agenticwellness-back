using CoppAddresd.Application.Features.Nutrition.Cases;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Exceptions;
using FluentValidation;
using MediatR;

namespace CoppAddresd.Application.Features.Nutrition.Queries;

public sealed record IntakeMacrosDto(decimal? Calories, decimal? ProteinG, decimal? CarbsG, decimal? FatG, decimal? FiberG);
public sealed record IntakePhotoDto(string Url, DateTimeOffset ExpiresAt);
public sealed record IntakeAnalysisItemDto(string Name, double Confidence, string? PortionSize, int? EstimatedGrams, IntakeMacrosDto Estimated);
public sealed record IntakeAnalysisDto(Guid AnalysisId, string Status, IntakeMacrosDto Estimated, IReadOnlyList<IntakeAnalysisItemDto> Items);
public sealed record IntakeRecordDto(Guid Id, string MealCode, IntakeMacrosDto Recorded, int? WaterMl, string Origin,
    IntakePhotoDto? Photo, IntakeAnalysisDto? Analysis, bool PatientCorrection);
public sealed record IntakePlannedMealDto(string MealCode, string? Name, IntakeMacrosDto Planned);
public sealed record IntakeMenuDto(Guid PrescriptionId, int Version, int DayNumber, IReadOnlyList<IntakePlannedMealDto> Meals);
public sealed record PatientIntakeDayDto(DateOnly LocalDate, Guid? CycleId, int? CycleNumber, bool Continuity,
    IntakeMenuDto? Menu, IReadOnlyList<IntakeRecordDto> Records);
public sealed record PatientIntakePage(DateOnly From, DateOnly Through, string Timezone, int Page, int PageSize,
    int TotalDays, int TotalPages, IReadOnlyList<PatientIntakeDayDto> Days);
public sealed record IntakeReadRecord(Guid Id, DateOnly LocalDate, string MealCode, IntakeMacrosDto Recorded, int? WaterMl,
    string Source, Guid? AnalysisId);
public sealed record IntakeReadAnalysis(IntakeAnalysisDto Analysis, string? PhotoKey);
public sealed record IntakeReadMenu(Guid PrescriptionId, int DayNumber, IntakePlannedMealDto Meal);
public sealed record PatientIntakeReadData(IReadOnlyList<NutritionCycle> Cycles, IReadOnlyList<EffectivePrescription> Versions,
    IReadOnlyList<IntakeReadMenu> Menus, IReadOnlyList<IntakeReadRecord> Records, IReadOnlyList<IntakeReadAnalysis> Analyses);
public interface IPatientIntakeReadStore
{
    Task<string?> TimezoneAsync(Guid patientId, CancellationToken ct);
    Task<PatientIntakeReadData> LoadAsync(Guid patientId, DateOnly from, DateOnly to, string timezone, bool photos, CancellationToken ct);
}
public interface INutritionPhotoUrlSigner
{
    /// <summary>Solo recibe referencias cuya propiedad, permiso y alcance comprobó el handler.</summary>
    Task<IntakePhotoDto?> SignAsync(Guid analysisId, string key, CancellationToken ct);
}
public sealed record PatientIntakeQuery(Guid PatientId, DateOnly From, DateOnly To, int Page = 1, int PageSize = 30) : IRequest<PatientIntakePage>;

public sealed class PatientIntakeQueryHandler(IPatientScopeGuard scope, IPatientScopeActor actor, IPatientIntakeReadStore store,
    INutritionCycleReadStore reading, INutritionPhotoUrlSigner photos, TimeProvider clock) : IRequestHandler<PatientIntakeQuery, PatientIntakePage>
{
    public async Task<PatientIntakePage> Handle(PatientIntakeQuery request, CancellationToken ct)
    {
        await scope.EnsureAsync(request.PatientId, "Nutrition.View", ct);
        await scope.EnsureAsync(request.PatientId, "Nutrition.RecordsView", ct);
        if (request.From == default || request.To == default || request.To < request.From || request.To.DayNumber - request.From.DayNumber > 365 || request.Page < 1
            || request.PageSize is < 1 or > 100 || (long)(request.Page - 1) * request.PageSize > int.MaxValue)
            throw new ValidationException("Rango máximo 366 días y paginación 1..100 requeridos.");
        var canPhotos = await actor.HasPermissionAsync("Nutrition.PhotosView", ct);
        if (canPhotos) await scope.EnsureAsync(request.PatientId, "Nutrition.PhotosView", ct);
        var read = await reading.ReadAsync(async readCt =>
        {
            var timezone = await store.TimezoneAsync(request.PatientId, readCt) ?? throw new NotFoundException("Paciente no encontrado.");
            var today = NutritionStateMachine.Today(clock.GetUtcNow(), timezone);
            var end = request.To > today ? today : request.To;
            var total = Math.Max(0, end.DayNumber - request.From.DayNumber + 1);
            var skip = (request.Page - 1) * request.PageSize;
            var dates = Enumerable.Range(0, Math.Min(request.PageSize, Math.Max(0, total - skip)))
                .Select(i => end.AddDays(-skip - i)).ToArray();
            var data = dates.Length == 0 ? new PatientIntakeReadData([], [], [], [], [])
                : await store.LoadAsync(request.PatientId, dates[^1], dates[0], timezone, canPhotos, readCt);
            return (timezone, end, total, dates, data);
        }, ct);
        // Firmas fuera de la transacción; se reutilizan para fotos repetidas de la misma página.
        var analysis = read.data.Analyses.ToDictionary(a => a.Analysis.AnalysisId);
        var signed = new Dictionary<Guid, IntakePhotoDto?>();
        if (canPhotos)
            foreach (var item in read.data.Analyses.Where(a => a.PhotoKey is not null))
            { ct.ThrowIfCancellationRequested(); signed[item.Analysis.AnalysisId] = await photos.SignAsync(item.Analysis.AnalysisId, item.PhotoKey!, ct); }
        var records = read.data.Records.ToLookup(r => r.LocalDate);
        var menus = read.data.Menus.ToLookup(m => (m.PrescriptionId, m.DayNumber));
        var days = new List<PatientIntakeDayDto>();
        foreach (var date in read.dates)
        {
            ct.ThrowIfCancellationRequested();
            var version = NutritionPublicationTimeline.Select(read.data.Versions, request.PatientId, date, read.timezone);
            var dayNumber = version is null ? (int?)null : NutritionPublicationTimeline.MenuDay(version, date);
            var cycle = read.data.Cycles.Where(c => c.Number.HasValue && c.StartDate <= date).OrderByDescending(c => c.StartDate).ThenByDescending(c => c.Number).FirstOrDefault();
            if (cycle is not null && NutritionCycleCalendar.AttributedThrough(cycle, null, date, read.timezone) < date) cycle = null;
            var rows = records[date].Select(r =>
            {
                var a = r.AnalysisId is { } id ? analysis.GetValueOrDefault(id)?.Analysis : null;
                var correction = a is not null && r.Recorded != a.Estimated;
                var origin = a is not null && r.Source == "ai_photo" && !correction ? "ai_estimated" : "patient_confirmed";
                return new IntakeRecordDto(r.Id, r.MealCode, r.Recorded, r.WaterMl, origin,
                    a is not null ? signed.GetValueOrDefault(a.AnalysisId) : null, a, correction);
            }).ToArray();
            var menu = version is null ? null : new IntakeMenuDto(version.Id, version.Version, dayNumber!.Value,
                menus[(version.Id, dayNumber.Value)].Select(m => m.Meal).OrderBy(m => m.MealCode, StringComparer.Ordinal).ToArray());
            days.Add(new(date, cycle?.Id, cycle?.Number, cycle is not null && NutritionCycleCalendar.IsContinuity(cycle, date), menu, rows));
        }
        return new(request.From, read.end, read.timezone, request.Page, request.PageSize, read.total,
            (read.total + request.PageSize - 1) / request.PageSize, days);
    }
}
