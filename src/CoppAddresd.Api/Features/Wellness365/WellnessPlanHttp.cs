using System.Text.Json.Serialization;
using CoppAddresd.Application.Features.Wellness365;
using CoppAddresd.Domain.Wellness365;

namespace CoppAddresd.Api.Features.Wellness365;

public static class WellnessPlanHttp
{
    public static IResult ToHttpResult(this WellnessPlanError error)
    {
        var (status, code) = error.Code switch
        {
            WellnessPlanErrorCode.PersonNotFound => (StatusCodes.Status404NotFound, "person_not_found"),
            WellnessPlanErrorCode.BadDay => (StatusCodes.Status400BadRequest, "bad_day"),
            WellnessPlanErrorCode.BadLog => (StatusCodes.Status400BadRequest, "bad_log"),
            WellnessPlanErrorCode.PlanLocked => (StatusCodes.Status409Conflict, "plan_locked"),
            WellnessPlanErrorCode.FutureDay => (StatusCodes.Status422UnprocessableEntity, "future_day"),
            _ => throw new ArgumentOutOfRangeException(nameof(error))
        };

        var messages = error.Status?.Message ?? error.Code switch
        {
            WellnessPlanErrorCode.PersonNotFound => new("Persona no encontrada.", "Person not found."),
            WellnessPlanErrorCode.BadDay => new("Indica un día entero entre 1 y 365.", "Enter a whole day between 1 and 365."),
            WellnessPlanErrorCode.BadLog => new("Revisa los datos del cierre.", "Check the daily log values."),
            WellnessPlanErrorCode.PlanLocked => new("El plan está bloqueado.", "The plan is locked."),
            WellnessPlanErrorCode.FutureDay => new("El día todavía no está disponible.", "That day is not available yet."),
            _ => throw new ArgumentOutOfRangeException(nameof(error))
        };
        return Results.Json(new PlanErrorBody(code, messages, error.Status), statusCode: status);
    }

    private sealed record PlanErrorBody(string Error, WellnessText Messages,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WellnessPlanStatus? Status);
}
