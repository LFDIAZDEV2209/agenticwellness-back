using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Exceptions;

namespace CoppAddresd.Application.Features.ProgramProgress.Commands.PublishTemplate;

public sealed record TemplateDayGapDto(int Day, int WeekNumber, short Weekday, string TaskCode);

public sealed class TemplateContentGapsException(IReadOnlyList<TemplateDayGapDto> gaps)
    : UnprocessableEntityException("TEMPLATE_CONTENT_GAPS: completa el contenido de los días indicados antes de publicar.")
{
    public IReadOnlyList<TemplateDayGapDto> Gaps { get; } = gaps;
}

/// <summary>Valida el recorrido de 83 días sin pacientes ni prescripciones que oculten un respaldo incompleto.</summary>
public static class TemplatePublication
{
    public static IReadOnlyList<TemplateDayGapDto> Gaps(ProgramTemplate template, EffectiveContentData catalog,
        IEffectiveDayContentResolver resolver, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var start = new DateOnly(2000, 1, 3); // Lunes: misma cuadrícula 12 × 7 del editor; no existe día 84.
        var simulated = new ProgramEnrollment { TemplateId = template.Id, StartLocalDate = start,
            Template = new ProgramTemplate { Id = template.Id, TotalWeeks = 12, DayTemplates = template.DayTemplates } };
        var data = catalog with { Enrollments = [simulated], Weeks = [], Prescriptions = [], Legacy = [],
            EngineNutrition = new Dictionary<Guid, Guid>() };
        var result = new List<TemplateDayGapDto>();
        foreach (var day in resolver.ResolveRange(data, simulated, start, start.AddDays(82), ct))
        {
            if (day.Podcast.Missing) result.Add(new(day.Date.DayNumber - start.DayNumber + 1, day.WeekNumber, day.Weekday, "podcast"));
            if (day.Routine.Missing) result.Add(new(day.Date.DayNumber - start.DayNumber + 1, day.WeekNumber, day.Weekday, "ejercicio"));
            if (day.Nutrition.Missing) result.Add(new(day.Date.DayNumber - start.DayNumber + 1, day.WeekNumber, day.Weekday, "nut"));
        }
        return result;
    }
}
