using CoppAddresd.Application.DTOs.ProgramProgress;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Domain.Exceptions;
using FluentValidation;
using MediatR;

namespace CoppAddresd.Application.Features.ProgramProgress.Commands.CreateTemplate;

public sealed record CreateTemplateVersionCommand(Guid SourceTemplateId) : IRequest<ProgramTemplateDto>;
public sealed class CreateTemplateVersionValidator : AbstractValidator<CreateTemplateVersionCommand>
{
    public CreateTemplateVersionValidator() => RuleFor(x => x.SourceTemplateId).NotEmpty();
}

/// <summary>Duplica únicamente la plantilla solicitada del catálogo compartido; nunca toca sus pacientes.</summary>
public sealed class CreateTemplateVersionHandler(IProgramRepository repository, IPatientScopeGuard scope, IPatientScopeActor actor)
    : IRequestHandler<CreateTemplateVersionCommand, ProgramTemplateDto>
{
    public async Task<ProgramTemplateDto> Handle(CreateTemplateVersionCommand request, CancellationToken ct)
    {
        await scope.GetFilterAsync("Program.Edit", ct);
        var source = await repository.GetTemplateAsync(request.SourceTemplateId, ct) ?? throw new NotFoundException("Plantilla no encontrada.");
        var copy = TemplateVersionCopier.Build(source, actor.UserId, ct);
        var created = await repository.UpsertTemplateAsync(copy.Template, copy.Days, actor.UserId, ct);
        return ProgramTemplateDto.FromEntity(created);
    }
}

public sealed record TemplateVersionCopy(ProgramTemplate Template, IReadOnlyList<WeeklyDayTemplate> Days);

/// <summary>Copia efectiva del calendario: específica antes que genérica; 83 podcasts sin día 84.</summary>
public static class TemplateVersionCopier
{
    public static TemplateVersionCopy Build(ProgramTemplate source, Guid? actorId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (source.Status != TemplateStatus.Active) throw new BusinessRuleViolationException("TEMPLATE_NOT_ACTIVE: solo se crea una versión desde una plantilla activa.");
        if (source.Version == int.MaxValue) throw new UnprocessableEntityException("TEMPLATE_VERSION_EXHAUSTED: versión fuera del rango admitido.");
        var id = Guid.NewGuid(); var version = source.Version + 1;
        var prefix = string.IsNullOrWhiteSpace(source.Code) ? "program" : source.Code[..Math.Min(10, source.Code.Length)];
        var target = new ProgramTemplate { Id = id, Code = $"{prefix}-v{version}-{id.ToString("N")[..16]}", Name = source.Name,
            Description = source.Description, TotalWeeks = source.TotalWeeks, Status = TemplateStatus.Draft, Version = version,
            StreakMinTasks = source.StreakMinTasks, EssentialTaskCodes = source.EssentialTaskCodes.ToList(), CreatedBy = actorId };
        WeeklyDayTemplate Copy(WeeklyDayTemplate row, int? week) => new() { Id = Guid.NewGuid(), TemplateId = id,
            WeekNumber = week, Weekday = row.Weekday, TaskCode = row.TaskCode, Points = row.Points, SortOrder = row.SortOrder,
            MediaId = row.MediaId, RoutineId = row.RoutineId, NutritionPlanId = row.NutritionPlanId, CreatedBy = actorId };
        var rows = source.DayTemplates.Where(row => row.TaskCode != TaskCode.podcast).Select(row => Copy(row, row.WeekNumber)).ToList();
        var podcasts = source.DayTemplates.Where(row => row.TaskCode == TaskCode.podcast).ToLookup(row => row.Weekday);
        for (var week = 1; week <= source.TotalWeeks; week++)
            for (short day = 1; day <= 7; day++)
            {
                ct.ThrowIfCancellationRequested();
                if (!ProgramTemplateWeekRules.IsAllowedDay(source.TotalWeeks, week, day)) continue;
                var row = podcasts[day].Where(r => r.WeekNumber is null || r.WeekNumber == week)
                    .OrderByDescending(r => r.WeekNumber.HasValue).FirstOrDefault()
                    ?? throw new UnprocessableEntityException("TEMPLATE_PODCAST_DAY_MISSING: falta una celda de podcast en la plantilla origen.");
                rows.Add(Copy(row, week));
            }
        return new(target, rows);
    }
}
