using CoppAddresd.Application.DTOs.ProgramProgress;
using CoppAddresd.Application.Features.ProgramProgress;
using CoppAddresd.Application.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CoppAddresd.Application.Features.ProgramProgress.Commands.ReplaceEnrollmentWeekTasks;

/// <summary>
/// Handler de <see cref="ReplaceEnrollmentWeekTasksCommand"/>: mapea las tareas a entidades
/// y actualiza atómicamente el TasksSnapshot de la semana en la inscripción del paciente.
/// </summary>
public sealed class ReplaceEnrollmentWeekTasksCommandHandler(
    IProgramRepository repository,
    ILogger<ReplaceEnrollmentWeekTasksCommandHandler> logger,
    IPatientScopeGuard scope) : IRequestHandler<ReplaceEnrollmentWeekTasksCommand, EnrollmentWeekDetailDto>
{
    public async Task<EnrollmentWeekDetailDto> Handle(ReplaceEnrollmentWeekTasksCommand request, CancellationToken ct)
    {
        await scope.EnsureResourceAsync(PatientResource.Enrollment, request.EnrollmentId, "Program.Edit", ct);
        await scope.EnsureNutritionPlansAsync(request.Tasks.Where(t => t.NutritionPlanId.HasValue).Select(t => t.NutritionPlanId!.Value), ct);
        var tasks = ProgramProgressTemplateMapper.ToEntities(request.Tasks);

        var updatedWeek = await repository.ReplaceEnrollmentWeekTasksAsync(
            request.EnrollmentId, request.WeekNumber, tasks, request.ActorId, ct,
            new EnrollmentWeekTaskEdits(request.Tasks.Where(t => t.Rest).Select(t => t.Weekday).ToHashSet(),
                request.Tasks.Where(t => t.Reset).Select(t => (t.Weekday, t.TaskCode)).ToHashSet()));

        logger.LogInformation(
            "Program.ReplaceEnrollmentWeekTasks: enrollment={EnrollmentId} week={WeekNumber} tasks={Count}",
            request.EnrollmentId, request.WeekNumber, tasks.Count);

        return updatedWeek;
    }
}
