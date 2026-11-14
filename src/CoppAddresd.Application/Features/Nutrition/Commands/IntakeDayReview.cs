using CoppAddresd.Application.Features.Nutrition.Cases;
using CoppAddresd.Application.Features.Nutrition.Queries;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Exceptions;
using FluentValidation;
using MediatR;

namespace CoppAddresd.Application.Features.Nutrition.Commands;

public sealed record IntakeDayReviewDto(DateOnly LocalDate, bool HasRecords, DateTime? ReviewedAt, Guid? ReviewedBy,
    bool Unreviewed, bool ModifiedSinceReview);
public sealed record UnreviewedIntakeDaysDto(int Days);
public interface INutritionIntakeDayReviewStore
{
    Task<T> WithPatientLockAsync<T>(Guid patientId, Func<CancellationToken, Task<T>> operation, CancellationToken ct);
    Task MarkAsync(Guid patientId, Guid cycleId, DateOnly day, Guid actorId, CancellationToken ct);
    Task UnmarkAsync(Guid patientId, Guid cycleId, DateOnly day, CancellationToken ct);
    Task<IReadOnlyList<IntakeDayReviewDto>> DaysAsync(Guid patientId, DateOnly from, DateOnly to, CancellationToken ct);
    Task<int> CountUnreviewedAsync(CancellationToken ct);
}
public sealed record MarkIntakeDayReviewed(Guid CycleId, DateOnly LocalDate) : IRequest<IntakeDayReviewDto>;
public sealed record UnmarkIntakeDayReviewed(Guid CycleId, DateOnly LocalDate) : IRequest<IntakeDayReviewDto>;
public sealed record GetIntakeDayReviews(Guid CycleId, DateOnly From, DateOnly To) : IRequest<IReadOnlyList<IntakeDayReviewDto>>;
public sealed record GetUnreviewedIntakeDays : IRequest<UnreviewedIntakeDaysDto>;

public sealed class IntakeDayReviewHandler(IPatientScopeGuard scope, IPatientScopeActor actor,
    INutritionIntakeDayReviewStore store, INutritionCycleReadStore cycles, TimeProvider clock)
    : IRequestHandler<MarkIntakeDayReviewed, IntakeDayReviewDto>, IRequestHandler<UnmarkIntakeDayReviewed, IntakeDayReviewDto>,
      IRequestHandler<GetIntakeDayReviews, IReadOnlyList<IntakeDayReviewDto>>, IRequestHandler<GetUnreviewedIntakeDays, UnreviewedIntakeDaysDto>
{
    public Task<IntakeDayReviewDto> Handle(MarkIntakeDayReviewed request, CancellationToken ct)
        => ChangeAsync(request.CycleId, request.LocalDate, true, ct);
    public Task<IntakeDayReviewDto> Handle(UnmarkIntakeDayReviewed request, CancellationToken ct)
        => ChangeAsync(request.CycleId, request.LocalDate, false, ct);

    private async Task<IntakeDayReviewDto> ChangeAsync(Guid cycleId, DateOnly day, bool mark, CancellationToken ct)
    {
        var patient = await scope.EnsureResourceAsync(PatientResource.Cycle, cycleId, "Nutrition.Manage", ct);
        var author = actor.UserId ?? throw new ForbiddenException("Falta identidad profesional.");
        return await store.WithPatientLockAsync(patient, async writeCt =>
        {
            await ValidateRangeAsync(cycleId, patient, day, day, writeCt);
            if (mark) await store.MarkAsync(patient, cycleId, day, author, writeCt);
            else await store.UnmarkAsync(patient, cycleId, day, writeCt);
            return (await store.DaysAsync(patient, day, day, writeCt)).Single();
        }, ct);
    }

    public async Task<IReadOnlyList<IntakeDayReviewDto>> Handle(GetIntakeDayReviews request, CancellationToken ct)
    {
        var patient = await scope.EnsureResourceAsync(PatientResource.Cycle, request.CycleId, "Nutrition.View", ct);
        await scope.EnsureAsync(patient, "Nutrition.RecordsView", ct);
        return await cycles.ReadAsync(async readCt =>
        {
            await ValidateRangeAsync(request.CycleId, patient, request.From, request.To, readCt);
            return await store.DaysAsync(patient, request.From, request.To, readCt);
        }, ct);
    }

    public async Task<UnreviewedIntakeDaysDto> Handle(GetUnreviewedIntakeDays request, CancellationToken ct)
    {
        await scope.GetFilterAsync("Nutrition.View", ct);
        return new(await store.CountUnreviewedAsync(ct));
    }

    private async Task ValidateRangeAsync(Guid cycleId, Guid patient, DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (from == default || to < from || to.DayNumber - from.DayNumber >= 100)
            throw new ValidationException("Se requieren fechas y un rango de hasta 100 días.");
        var header = await cycles.HeaderAsync(cycleId, patient, ct) ?? throw new NotFoundException("Ciclo no encontrado.");
        var today = NutritionStateMachine.Today(clock.GetUtcNow(), header.Timezone);
        var end = NutritionCycleCalendar.AttributedThrough(header.Cycle, header.SuccessorStart, today, header.Timezone);
        if (header.Cycle.StartDate is not { } start || end is null || from < start || to > end)
            throw new ValidationException("La fecha no pertenece al tramo transcurrido del ciclo.");
    }
}
