using CoppAddresd.Application.Features.Nutrition.Cases;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Exceptions;
using FluentValidation;
using MediatR;

namespace CoppAddresd.Application.Features.Nutrition.Queries;

public sealed record NutritionCycleReadHeader(NutritionCycle Cycle, string Timezone, DateOnly? SuccessorStart);
public sealed record CycleMealCount(Guid PrescriptionId, int DayNumber, int Meals);
public sealed record CycleIntake(Guid Id, DateOnly LocalDate, string MealCode);
public sealed record NutritionCycleReadData(IReadOnlyList<EffectivePrescription> Versions,
    IReadOnlyList<CycleMealCount> Menus, IReadOnlyList<CycleIntake> Intakes);

public interface INutritionCycleReadStore
{
    Task<T> ReadAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct);
    Task<NutritionCycleReadHeader?> HeaderAsync(Guid cycleId, Guid patientId, CancellationToken ct);
    Task<NutritionCycleReadData> RangeAsync(Guid cycleId, Guid patientId, DateOnly from, DateOnly to, CancellationToken ct);
}

public sealed record CycleAdherenceDayDto(DateOnly Date, bool Continuity, int RecordedMeals, int? PlannedMeals,
    decimal? Adherence, bool Interruption, Guid? PrescriptionId, int? MenuDay, IReadOnlyList<Guid> IntakeIds);
public sealed record CycleAdherenceDto(Guid CycleId, int? Number, string State, DateOnly? StartDate, DateOnly? EndDate,
    DateOnly? From, DateOnly? Through, decimal? Adherence, int DaysWithData, int ElapsedDays, int Interruptions,
    int DaysWithUnknownMenu, int Page, int PageSize, IReadOnlyList<CycleAdherenceDayDto> Days);
public sealed record GetCycleAdherenceQuery(Guid CycleId, DateOnly? From = null, DateOnly? To = null,
    int Page = 1, int PageSize = 30) : IRequest<CycleAdherenceDto>;

public sealed class GetCycleAdherenceQueryHandler(IPatientScopeGuard scope, INutritionCycleReadStore store, TimeProvider clock)
    : IRequestHandler<GetCycleAdherenceQuery, CycleAdherenceDto>
{
    public async Task<CycleAdherenceDto> Handle(GetCycleAdherenceQuery request, CancellationToken ct)
    {
        var patientId = await scope.EnsureResourceAsync(PatientResource.Cycle, request.CycleId, "Nutrition.View", ct);
        if (request.Page < 1 || request.PageSize is < 1 or > 100 || (long)(request.Page - 1) * request.PageSize > int.MaxValue
            || request.From.HasValue && request.To.HasValue && request.From > request.To)
            throw new ValidationException("Rango o paginación de ciclo inválidos.");
        return await store.ReadAsync(async readCt =>
        {
            var header = await store.HeaderAsync(request.CycleId, patientId, readCt) ?? throw new NotFoundException("Ciclo no encontrado.");
            var today = NutritionStateMachine.Today(clock.GetUtcNow(), header.Timezone);
            var through = NutritionCycleCalendar.AttributedThrough(header.Cycle, header.SuccessorStart, today, header.Timezone);
            var from = header.Cycle.StartDate;
            if (request.From.HasValue && request.From > from) from = request.From;
            if (request.To.HasValue && request.To < through) through = request.To;
            var data = from.HasValue && through.HasValue && through >= from
                ? await store.RangeAsync(request.CycleId, patientId, from.Value, through.Value, readCt) : new NutritionCycleReadData([], [], []);
            return NutritionCycleAdherence.Calculate(header, data, from, through, today, request.Page, request.PageSize, readCt);
        }, ct);
    }
}

public static class NutritionCycleAdherence
{
    public static CycleAdherenceDto Calculate(NutritionCycleReadHeader header, NutritionCycleReadData data,
        DateOnly? from, DateOnly? through, DateOnly today, int page, int pageSize, CancellationToken ct)
    {
        var rows = new List<CycleAdherenceDayDto>(); var logs = data.Intakes.ToLookup(i => i.LocalDate);
        var menus = data.Menus.ToDictionary(m => (m.PrescriptionId, m.DayNumber), m => m.Meals);
        if (from.HasValue && through.HasValue && from <= through)
        for (var day = from.Value; day <= through.Value; day = day.AddDays(1))
        {
            ct.ThrowIfCancellationRequested();
            var intake = logs[day].DistinctBy(i => i.Id).ToArray();
            var version = NutritionPublicationTimeline.Select(data.Versions, header.Cycle.PatientId, day, header.Timezone);
            var menuDay = version is null ? (int?)null : NutritionPublicationTimeline.MenuDay(version, day);
            var planned = version is not null && menus.TryGetValue((version.Id, menuDay!.Value), out var meals) && meals > 0 ? (int?)meals : null;
            var recorded = intake.Select(i => i.MealCode).Distinct().Count();
            rows.Add(new(day, NutritionCycleCalendar.IsContinuity(header.Cycle, day), recorded, planned,
                planned.HasValue && recorded > 0 ? (decimal)recorded / planned.Value : null, recorded == 0 && day < today,
                version?.Id, menuDay, intake.Select(i => i.Id).Order().ToArray()));
        }
        var measured = rows.Where(d => d.RecordedMeals > 0).ToArray();
        var unknown = measured.Count(d => !d.PlannedMeals.HasValue);
        // Hoy sin datos sigue en la tira como día en curso; todavía no es una interrupción ni un día cerrado.
        var interruptions = rows.Count(d => d.Interruption);
        var adherence = measured.Length > 0 && unknown == 0 ? (decimal?)decimal.Round(measured.Average(d => d.Adherence!.Value), 2) : null;
        return new(header.Cycle.Id, header.Cycle.Number, NutritionCycleCalendar.State(header.Cycle, today),
            header.Cycle.StartDate, header.Cycle.EndDate, from, through, adherence, measured.Length, measured.Length + interruptions,
            interruptions, unknown, page, pageSize,
            rows.Skip((page - 1) * pageSize).Take(pageSize).ToArray());
    }
}
