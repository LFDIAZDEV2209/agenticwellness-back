using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Domain.Exceptions;
using FluentValidation;
using MediatR;

namespace CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent;

public sealed record BulkAssignPatientRoutinesCommand(IReadOnlyList<Guid> PatientIds, Guid RoutineId, int SemanaDesde, int SemanaHasta)
    : IRequest<BulkAssignPatientPodcastsResult>;
public interface IBulkPatientRoutineStore
{
    Task<BulkAssignPatientPodcastsResult> AssignRoutinesAsync(BulkAssignPatientRoutinesCommand request, CancellationToken ct);
}
public sealed class BulkAssignPatientRoutinesValidator : AbstractValidator<BulkAssignPatientRoutinesCommand>
{
    public BulkAssignPatientRoutinesValidator()
    {
        RuleFor(x => x.PatientIds).NotNull().Must(ids => ids is { Count: >= 1 and <= 100 });
        RuleForEach(x => x.PatientIds).NotEmpty(); RuleFor(x => x.RoutineId).NotEmpty();
        RuleFor(x => x.SemanaDesde).InclusiveBetween(1, 83);
        RuleFor(x => x.SemanaHasta).InclusiveBetween(1, 83).GreaterThanOrEqualTo(x => x.SemanaDesde);
    }
}
public sealed class BulkAssignPatientRoutinesHandler(IBulkPatientRoutineStore store)
    : IRequestHandler<BulkAssignPatientRoutinesCommand, BulkAssignPatientPodcastsResult>
{
    public Task<BulkAssignPatientPodcastsResult> Handle(BulkAssignPatientRoutinesCommand request, CancellationToken ct) => store.AssignRoutinesAsync(request, ct);
}
public static class BulkPatientRoutinePlanner
{
    public static PatientPodcastPlan Build(BulkAssignPatientRoutinesCommand request, ExerciseRoutine routine,
        IReadOnlyCollection<Guid> visiblePatients, IReadOnlyList<ProgramEnrollment> enrollments, IReadOnlyList<ProgramWeek> weeks,
        Guid actorId, DateTimeOffset now, CancellationToken ct)
    {
        if (routine.Status != NutritionPlanStatus.Active) throw new UnprocessableEntityException("ROUTINE_NOT_ACTIVE: se requiere una rutina activa.");
        return BulkPatientContentPlanner.Build(request.PatientIds, routine.Id, request.SemanaDesde, request.SemanaHasta,
            "ejercicio", visiblePatients, enrollments, weeks, actorId, now, ct);
    }
}
