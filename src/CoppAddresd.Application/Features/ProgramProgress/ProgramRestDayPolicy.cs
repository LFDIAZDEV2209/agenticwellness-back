using CoppAddresd.Domain.Exceptions;

namespace CoppAddresd.Application.Features.ProgramProgress;

/// <summary>Guardia de despliegue: la lectura tolera descanso, su creación requiere apertura explícita.</summary>
public static class ProgramRestDayPolicy
{
    public const string ConfigurationKey = "Program:RestDays:Enabled";

    public static void EnsureAllowed(string? configuredValue, bool hasRest)
    {
        if (hasRest && !(bool.TryParse(configuredValue, out var enabled) && enabled))
            throw new UnprocessableEntityException("REST_DAYS_DISABLED: la creación de descansos aún no está habilitada.");
    }
}

/// <summary>Opciones de edición del snapshot que no pertenecen al catálogo de plantillas.</summary>
public sealed record EnrollmentWeekTaskEdits(IReadOnlySet<short> RestDays,
    IReadOnlySet<(short Weekday, CoppAddresd.Domain.Enums.ProgramProgress.TaskCode TaskCode)>? ResetCells = null);
