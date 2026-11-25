using System.Text.Json.Serialization;
using CoppAddresd.Application.Features.Wellness365;

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

        return Results.Json(new PlanErrorBody(code, error.Status), statusCode: status);
    }

    private sealed record PlanErrorBody(string Error,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WellnessPlanStatus? Status);
}
