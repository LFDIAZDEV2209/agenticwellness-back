using CoppAddresd.Application.Features.Nutrition.Cases;
using CoppAddresd.Application.Features.Nutrition.Queries;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Exceptions;
using FluentValidation;
using MediatR;

namespace CoppAddresd.Application.Features.Nutrition.Commands;

public sealed record HighlightIntakeHeader(Guid PatientId, DateOnly LocalDate);
public sealed record IntakeExampleDto(Guid IntakeLogId, DateOnly LocalDate, string MealCode, IntakeMacrosDto Recorded,
    int? WaterMl, Guid PinnedBy, DateTime PinnedAt, string? Note);
public sealed record IntakeExamplesPage(int Page, int PageSize, int Total, IReadOnlyList<IntakeExampleDto> Items);
public interface INutritionCycleHighlightStore
{
    Task<T> WithPatientLockAsync<T>(Guid patientId, Func<CancellationToken, Task<T>> operation, CancellationToken ct);
    Task<HighlightIntakeHeader?> IntakeAsync(Guid intakeId, CancellationToken ct);
    Task PinAsync(Guid cycleId, Guid intakeId, Guid actorId, string? note, CancellationToken ct);
    Task UnpinAsync(Guid cycleId, Guid intakeId, CancellationToken ct);
    Task<IntakeExamplesPage> ListAsync(Guid cycleId, Guid patientId, bool notes, int page, int pageSize, CancellationToken ct);
}
public sealed record PinIntakeExample(Guid CycleId, Guid IntakeLogId, string? Note = null) : IRequest;
public sealed record UnpinIntakeExample(Guid CycleId, Guid IntakeLogId) : IRequest;
public sealed record GetIntakeExamples(Guid CycleId, int Page = 1, int PageSize = 30) : IRequest<IntakeExamplesPage>;

public sealed class IntakeExamplesHandler(IPatientScopeGuard scope, IPatientScopeActor actor, INutritionCycleHighlightStore store,
    INutritionCycleReadStore cycles, TimeProvider clock) : IRequestHandler<PinIntakeExample>, IRequestHandler<UnpinIntakeExample>,
    IRequestHandler<GetIntakeExamples, IntakeExamplesPage>
{
    public async Task Handle(PinIntakeExample request, CancellationToken ct)
    {
        var patient = await AuthorizeAsync(request.CycleId, request.IntakeLogId, ct);
        if (request.Note?.Length > 2000) throw new ValidationException("La nota admite hasta 2000 caracteres.");
        var author = actor.UserId ?? throw new ForbiddenException("Falta identidad profesional.");
        await store.WithPatientLockAsync(patient, async writeCt =>
        {
            var intake = await store.IntakeAsync(request.IntakeLogId, writeCt) ?? throw new NotFoundException("Registro no encontrado.");
            var header = await cycles.HeaderAsync(request.CycleId, patient, writeCt) ?? throw new NotFoundException("Ciclo no encontrado.");
            var today = NutritionStateMachine.Today(clock.GetUtcNow(), header.Timezone);
            var end = NutritionCycleCalendar.AttributedThrough(header.Cycle, header.SuccessorStart, today, header.Timezone);
            if (intake.PatientId != patient || header.Cycle.StartDate is not { } start || end is null || intake.LocalDate < start || intake.LocalDate > end)
                throw new ValidationException("El registro no pertenece al tramo transcurrido del ciclo.");
            await store.PinAsync(request.CycleId, request.IntakeLogId, author, request.Note?.Trim(), writeCt);
            return true;
        }, ct);
    }

    public async Task Handle(UnpinIntakeExample request, CancellationToken ct)
    {
        var patient = await AuthorizeAsync(request.CycleId, request.IntakeLogId, ct);
        await store.WithPatientLockAsync(patient, async writeCt =>
        { await store.UnpinAsync(request.CycleId, request.IntakeLogId, writeCt); return true; }, ct);
    }

    private async Task<Guid> AuthorizeAsync(Guid cycleId, Guid intakeId, CancellationToken ct)
    {
        var patient = await scope.EnsureResourceAsync(PatientResource.Cycle, cycleId, "Nutrition.Manage", ct);
        var intakePatient = await scope.EnsureResourceAsync(PatientResource.Intake, intakeId, "Nutrition.Manage", ct);
        if (intakePatient != patient) throw new NotFoundException("Registro no encontrado.");
        return patient;
    }

    public async Task<IntakeExamplesPage> Handle(GetIntakeExamples request, CancellationToken ct)
    {
        var patient = await scope.EnsureResourceAsync(PatientResource.Cycle, request.CycleId, "Nutrition.View", ct);
        await scope.EnsureAsync(patient, "Nutrition.RecordsView", ct);
        if (request.Page < 1 || request.PageSize is < 1 or > 100 || (long)(request.Page - 1) * request.PageSize > int.MaxValue)
            throw new ValidationException("Paginación inválida: tamaño 1..100.");
        var notes = await actor.HasPermissionAsync("Nutrition.NotesView", ct);
        return await cycles.ReadAsync(readCt => store.ListAsync(request.CycleId, patient, notes, request.Page, request.PageSize, readCt), ct);
    }
}
