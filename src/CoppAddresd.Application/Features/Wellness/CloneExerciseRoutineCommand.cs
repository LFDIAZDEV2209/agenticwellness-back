using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Domain.Exceptions;
using FluentValidation;
using MediatR;

namespace CoppAddresd.Application.Features.Wellness;

public sealed record CloneExerciseRoutineRequest(string? Name = null, Guid? PatientId = null);
public sealed record CloneExerciseRoutineCommand(Guid SourceRoutineId, CloneExerciseRoutineRequest Request) : IRequest<ExerciseRoutineDto>;

public sealed class CloneExerciseRoutineValidator : AbstractValidator<CloneExerciseRoutineCommand>
{
    public CloneExerciseRoutineValidator()
    {
        RuleFor(x => x.SourceRoutineId).NotEmpty();
        RuleFor(x => x.Request.Name).NotEmpty().MaximumLength(200).When(x => x.Request.Name is not null);
        RuleFor(x => x.Request.PatientId).NotEqual(Guid.Empty).When(x => x.Request.PatientId.HasValue);
    }
}
public sealed class CreateExerciseRoutineContraindicationsValidator : AbstractValidator<CreateExerciseRoutineCommand>
{
    public CreateExerciseRoutineContraindicationsValidator() => RuleFor(x => x.Request.Contraindications).MaximumLength(4000);
}
public sealed class UpdateExerciseRoutineContraindicationsValidator : AbstractValidator<UpdateExerciseRoutineCommand>
{
    public UpdateExerciseRoutineContraindicationsValidator() => RuleFor(x => x.Request.Contraindications).MaximumLength(4000);
}

/// <summary>Copia el catálogo y sus demostraciones; el contexto de paciente opcional usa la creación personalizada existente.</summary>
public sealed class CloneExerciseRoutineHandler(IWellnessRepository repository, IPatientScopeGuard scope, IPatientScopeActor actor, ISender sender)
    : IRequestHandler<CloneExerciseRoutineCommand, ExerciseRoutineDto>
{
    public async Task<ExerciseRoutineDto> Handle(CloneExerciseRoutineCommand request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.Request.PatientId is { } patient)
            await scope.EnsureAsync(patient, "Wellness.Manage", ct);
        else
            await scope.GetFilterAsync("Wellness.Manage", ct);
        var actorId = actor.UserId ?? throw new ForbiddenException("Usuario no identificado.");
        var source = await repository.GetRoutineByIdAsync(request.SourceRoutineId, ct) ?? throw new NotFoundException("Rutina fuente no encontrada.");
        var name = request.Request.Name?.Trim() ?? $"Copia de {source.Name}";
        if (name.Length > 200) name = name[..200];
        var copy = new CreateExerciseRoutineRequest(name, source.Description, source.Difficulty, source.EstimatedMinutes,
            source.Category, NutritionPlanStatus.Draft, source.TargetMuscles, source.Equipment, source.WarmupNotes,
            source.CooldownNotes, source.MediaId, source.Exercises.OrderBy(e => e.SortOrder).Select(e => new CreateRoutineExerciseRequest(
                e.Name, e.Description, e.Sets, e.Repetitions, e.RestSeconds, e.DurationSecs, e.WeightKg, e.TargetMuscle,
                e.Equipment, e.Tempo, e.Rpe, e.Tips, e.MediaId, e.SortOrder)).ToArray(), request.Request.PatientId, source.Contraindications);
        // Una sola inserción del grafo; con paciente, la creación existente lo asigna en la misma transacción.
        return await sender.Send(new CreateExerciseRoutineCommand(copy, actorId), ct);
    }
}
