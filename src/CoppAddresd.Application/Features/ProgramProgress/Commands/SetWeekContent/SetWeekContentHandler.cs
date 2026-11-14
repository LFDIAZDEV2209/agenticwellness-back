using CoppAddresd.Application.DTOs.ProgramProgress;
using CoppAddresd.Application.Features.ProgramProgress.Commands.ReplaceEnrollmentWeekTasks;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Domain.Exceptions;
using MediatR;

namespace CoppAddresd.Application.Features.ProgramProgress.Commands.SetWeekContent;

/// <summary>Adaptador del editor semanal anterior: escribe las celdas del snapshot mediante el comando diario.</summary>
public sealed class SetWeekContentHandler(
    IProgramRepository programRepository,
    IWellnessRepository wellnessRepository,
    ISender sender,
    IPatientScopeGuard scope) : IRequestHandler<SetWeekContentCommand, ProgramContentWeekDto>
{
    public async Task<ProgramContentWeekDto> Handle(SetWeekContentCommand request, CancellationToken ct)
    {
        await scope.EnsureResourceAsync(PatientResource.Enrollment, request.EnrollmentId, "Program.Edit", ct);
        if (request.NutritionPlanId is { } requestedPlan) await scope.EnsureNutritionPlansAsync([requestedPlan], ct);
        var enrollment = await programRepository.GetEnrollmentAsync(request.EnrollmentId, ct)
            ?? throw new NotFoundException("Inscripción no encontrada.");
        if (request.WeekNumber < 1 || request.WeekNumber > enrollment.TotalWeeks)
            throw new UnprocessableEntityException("WEEK_NUMBER_OUT_OF_RANGE: semana fuera del programa.");
        var template = await programRepository.GetTemplateAsync(enrollment.TemplateId, ct)
            ?? throw new NotFoundException("Plantilla no encontrada.");
        var plan = request.NutritionPlanId is { } planId
            ? await wellnessRepository.GetPlanByIdAsync(planId, ct) ?? throw new NotFoundException("Plan no encontrado.") : null;
        var routine = request.ExerciseRoutineId is { } routineId
            ? await wellnessRepository.GetRoutineByIdAsync(routineId, ct) ?? throw new NotFoundException("Rutina no encontrada.") : null;
        var start = enrollment.StartLocalDate.AddDays((request.WeekNumber - 1) * 7);
        var last = enrollment.StartLocalDate.AddDays(EffectiveDayContentResolver.DurationDays(template) - 1);
        var end = start.AddDays(6) < last ? start.AddDays(6) : last;
        var defaults = template.DayTemplates.Where(t => t.WeekNumber is null || t.WeekNumber == request.WeekNumber)
            .GroupBy(t => (t.Weekday, t.TaskCode)).ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.WeekNumber.HasValue).First());
        var changes = new List<WeeklyDayTemplateRequest>();
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            var weekday = (short)(date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek);
            foreach (var code in new[] { TaskCode.nut, TaskCode.ejercicio })
            {
                defaults.TryGetValue((weekday, code), out var source);
                changes.Add(new(weekday, code, source?.Points ?? 0, source?.SortOrder ?? 0,
                    RoutineId: code == TaskCode.ejercicio ? request.ExerciseRoutineId : null,
                    NutritionPlanId: code == TaskCode.nut ? request.NutritionPlanId : null,
                    Reset: code == TaskCode.ejercicio ? request.ExerciseRoutineId is null : request.NutritionPlanId is null));
            }
        }
        await sender.Send(new ReplaceEnrollmentWeekTasksCommand(request.EnrollmentId, request.WeekNumber, changes, request.ActorId), ct);
        return new(request.WeekNumber, start, end,
            plan is null ? null : new(plan.Id, plan.Name, plan.Name),
            routine is null ? null : new(routine.Id, routine.Name, routine.Name));
    }
}
