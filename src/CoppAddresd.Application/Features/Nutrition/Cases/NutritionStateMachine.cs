using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Exceptions;

namespace CoppAddresd.Application.Features.Nutrition.Cases;

/// <summary>Estados persistidos y vigencia por calendario local; por_renovar es virtual.</summary>
public static class NutritionStateMachine
{
    public const int RenewalLeadDays = 5;
    public static void Ensure(NutritionOperation operation, string state)
    {
        var allowed = operation switch
        {
            NutritionOperation.Publish => state is "borrador_ia" or "en_aprobacion" or "devuelto",
            NutritionOperation.CreateVersion => state == "aprobado",
            NutritionOperation.Save or NutritionOperation.ApplyDay or NutritionOperation.Regenerate or NutritionOperation.Submit
                => state is "borrador_ia" or "devuelto",
            NutritionOperation.Return or NutritionOperation.Approve => state == "en_aprobacion",
            _ => false
        };
        if (!allowed) throw new BusinessRuleViolationException("Transición nutricional inválida; la versión aprobada es inmutable.");
    }
    public static DateOnly Today(DateTimeOffset utcNow, string timezone)
    {
        try { return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, TimeZoneInfo.FindSystemTimeZoneById(timezone)).DateTime); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        { throw new UnprocessableEntityException("La zona horaria del paciente no es válida."); }
    }
    public static (DateOnly Start, DateOnly End) Dates(DateOnly today, DateOnly? requested, NutritionPrescription? previous,
        NutritionPrescription? predecessor = null)
    {
        if (previous is not null)
        {
            if (previous.StartDate is not { } start || previous.EndDate != start.AddDays(29) || previous.EndDate < today)
                throw new BusinessRuleViolationException("El ciclo terminó o es inválido; genera un nuevo ciclo de 30 días.");
            if (requested.HasValue && requested != start)
                throw new BusinessRuleViolationException("Una nueva versión conserva la vigencia del ciclo.");
            return (start, start.AddDays(29));
        }
        var begin = predecessor?.EndDate is { } end && end >= today ? end.AddDays(1) : today;
        if (requested.HasValue && requested < today)
            throw new UnprocessableEntityException("No se permite publicar con fecha anterior a hoy local.");
        if (requested.HasValue && requested < begin)
            throw new BusinessRuleViolationException("Existe otro ciclo publicado con vigencia solapada.");
        if (requested.HasValue && requested != begin)
            throw new UnprocessableEntityException("El ciclo inicia hoy local o el día siguiente al cierre del anterior; no se permiten huecos ni fechas retroactivas.");
        return (begin, begin.AddDays(29));
    }
    public static string DisplayState(NutritionPrescription plan, DateOnly today)
        => plan.State == "aprobado" && plan.IsPublished && plan.EndDate <= today.AddDays(RenewalLeadDays) ? "por_renovar" : plan.State;
}
