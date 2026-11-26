using CoppAddresd.Api.Features.Wellness365;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using System.Security.Claims;
using CoppAddresd.Api.Localization;
using CoppAddresd.Application.Features.Motor365;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Repositories;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi.Models;
using Npgsql;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace CoppAddresd.Api.Configuration;

/// <summary>Configuración HTTP y autenticación del contrato v1 del motor.</summary>
public static class Motor365Api
{
    public const string People = "Motor365.People";
    public const string ReadParams = "Motor365.Params.Read";
    public const string WriteParams = "Motor365.Params.Write";
    public const string Audit = "Motor365.Audit";

    private static readonly string[] StaffRoles = ["Admin", "Professional", "ClinicalDirector", "CareCoordinator", "Nurse"];

    public static void AddMotor365Api(this IServiceCollection services)
    {
        services.AddScoped<IMotor365Actor, MotorHttpActor>();
        services.AddScoped<IMotor365Store, Motor365Store>();
        services.AddScoped<MotorPeople>();
        services.AddSingleton(sp => new Motor365Telemetry(sp.GetRequiredService<IConfiguration>()["Motor365:TraceKey"]));
        services.AddSingleton<Motor365SnapshotWorker>();
        services.AddSingleton<IMotor365SnapshotQueue>(sp => sp.GetRequiredService<Motor365SnapshotWorker>());
        services.AddHostedService(sp => sp.GetRequiredService<Motor365SnapshotWorker>());
        services.AddAuthorizationBuilder()
            .AddPolicy(People, p => p.RequireAuthenticatedUser().RequireAssertion(c =>
                ValidIdentity(c.User) && (c.User.HasClaim("aud", "app") || ErpRole(c.User, StaffRoles))))
            .AddPolicy(ReadParams, p => p.RequireAuthenticatedUser().RequireAssertion(c =>
                ValidIdentity(c.User) && ErpRole(c.User, [.. StaffRoles, "clinical_admin"])))
            .AddPolicy(WriteParams, p => p.RequireAuthenticatedUser().RequireAssertion(c =>
                ValidIdentity(c.User) && ErpRole(c.User, ["Admin", "clinical_admin"])))
            .AddPolicy(Audit, p => p.RequireAuthenticatedUser().RequireAssertion(c =>
                ValidIdentity(c.User) && ErpRole(c.User, ["Admin", "Auditor"])));

        services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            var fallback = options.Events.OnChallenge;
            options.Events.OnChallenge = context =>
            {
                if (!context.Request.Path.StartsWithSegments("/v1")) return fallback(context);
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                return Task.CompletedTask;
            };
        });

        services.Configure<MvcOptions>(options => options.Filters.Add(new Motor365ClientErrorFilter()));
        services.PostConfigure<ApiBehaviorOptions>(options =>
        {
            var fallback = options.InvalidModelStateResponseFactory;
            options.InvalidModelStateResponseFactory = context =>
                context.HttpContext.Request.Path.StartsWithSegments("/v1")
                    ? new BadRequestObjectResult(Motor365Errors.Create(context.HttpContext, "invalid_request",
                        issues: [.. context.ModelState.Where(x => x.Value?.Errors.Count > 0)
                            .Select(x => new MotorErrorIssue("invalid_field", Motor365Errors.SafeField(x.Key)))]))
                    : fallback(context);
        });
    }

    public static void ConfigureMotor365OpenApi(SwaggerGenOptions options)
    {
        options.SwaggerDoc("motor365", new OpenApiInfo
        {
            Title = "Agentic Wellness IA API",
            Version = "1.0.0",
            Description = "Contrato del Motor 365. El servidor calcula los planes de bienestar.",
        });
        options.DocInclusionPredicate((document, endpoint) =>
            endpoint.GroupName == document || (document != "motor365" && endpoint.GroupName is null));
        options.OperationFilter<Motor365OpenApiOperation>();
    }

    private static bool ValidIdentity(ClaimsPrincipal user) => TryUserId(user) is not null;
    private static Guid? TryUserId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) && id != Guid.Empty ? id : null;
    private static bool ErpRole(ClaimsPrincipal user, string[] roles) =>
        user.HasClaim("aud", "erp") && !user.HasClaim("aud", "app") && roles.Any(user.IsInRole);

    private sealed class MotorHttpActor(IHttpContextAccessor accessor) : IMotor365Actor
    {
        public Guid? UserId => accessor.HttpContext is { } context ? TryUserId(context.User) : null;
        public bool IsApp => accessor.HttpContext?.User.HasClaim("aud", "app") == true;
    }
}

internal sealed class Motor365ClientErrorFilter : IAlwaysRunResultFilter, IOrderedFilter
{
    public int Order => -2100;

    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.HttpContext.Request.Path.StartsWithSegments("/v1") &&
            context.Result is IStatusCodeActionResult { StatusCode: >= 400 } result &&
            context.Result is not ObjectResult { Value: MotorApiError })
        {
            var status = result.StatusCode!.Value;
            context.Result = new ObjectResult(Motor365Errors.Create(context.HttpContext, Motor365Errors.ForStatus(status)))
            {
                StatusCode = status,
            };
        }
    }

    public void OnResultExecuted(ResultExecutedContext context) { }
}

public sealed class Motor365OpenApiOperation : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.ApiDescription.GroupName != "motor365") return;
        operation.Summary = operation.OperationId switch
        {
            "Motor365UpsertPerson" => "Guarda el perfil y devuelve su estado",
            "Motor365GetPerson" => "Consulta el perfil, el estado y los registros",
            "Motor365UpdatePerson" => "Actualiza el perfil o las respuestas",
            "Motor365GetPlan" => "Consulta las recomendaciones de los 365 días",
            "Motor365GetPlanDay" => "Consulta el día con las cuatro comidas y la sesión",
            "Motor365CloseDay" => "Guarda el cierre del día",
            "Motor365GetEvents" => "Consulta las decisiones del motor",
            "Motor365GetParameters" => "Consulta los parámetros vigentes y por defecto",
            "Motor365UpdateParameters" => "Publica una versión de los parámetros",
            "Motor365GetFoods" => "Consulta los alimentos del catálogo por 100 g",
            "Motor365GetExercises" => "Consulta el catálogo de ejercicios",
            "Motor365GetAudit" => "Consulta la auditoría",
            "Motor365Health" => "Comprueba la disponibilidad del servicio",
            _ => operation.Summary,
        };
        foreach (var (code, response) in operation.Responses)
        {
            response.Description = code switch
            {
                "400" => "invalid_request",
                "401" => "unauthenticated",
                "403" => "forbidden",
                "404" => "person_not_found",
                "409" when operation.OperationId is "Motor365GetPlan" or "Motor365GetPlanDay" or "Motor365GetEvents" => "plan_locked",
                "409" when operation.OperationId is "Motor365UpsertPerson" or "Motor365UpdatePerson" => "submission_conflict o version_conflict",
                "409" => "version_conflict",
                "413" => "request_too_large",
                "415" => "unsupported_media_type",
                "422" when operation.OperationId is "Motor365CloseDay" or "Motor365GetPlanDay" => "future_day",
                "422" when operation.OperationId == "Motor365UpdateParameters" => "invalid_parameters",
                "422" => "eligibility_required o future_day",
                "500" => "internal_error",
                "503" => "service_unavailable",
                _ => response.Description,
            };
        }

        if (context.MethodInfo.Name == nameof(Controllers.Motor365Controller.Health))
            operation.Security = [new()];
        foreach (var parameter in operation.Parameters.Where(p => p.Name == "day"))
        {
            parameter.Schema.Minimum = 1;
            parameter.Schema.Maximum = 365;
        }
    }
}

/// <summary>Respuesta de error estable del contrato v1 del motor.</summary>
public sealed record MotorApiError(string Code, string Message, MotorText Messages, bool Retryable,
    IReadOnlyList<MotorErrorIssue> Issues, string TraceId, MotorStatus? Status);

/// <summary>Homogeneiza los errores producidos antes y después de MVC.</summary>
public sealed class Motor365Errors(RequestDelegate next, ILogger<Motor365Errors> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            await next(context);
            if (!context.Response.HasStarted && context.Response.StatusCode >= 400)
                await WriteAsync(context, ForStatus(context.Response.StatusCode), context.Response.StatusCode);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw; // El middleware global registra la cancelación del cliente.
        }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            var (code, status, problem) = Map(ex);
            if (status >= 500)
                logger.LogError("Fallo Motor 365 {Code} ({ExceptionType}), trace {TraceId}", code, ex.GetType().Name, context.TraceIdentifier);
            await WriteAsync(context, code, status, problem);
        }
    }

    public static (string Code, int Status, MotorException? Problem) Map(Exception exception) => exception switch
    {
        MotorException problem => (problem.Code, problem.Code switch
        {
            "invalid_request" or "bad_day" => 400,
            "forbidden" => 403,
            "person_not_found" => 404,
            "version_conflict" or "submission_conflict" or "plan_locked" => 409,
            "eligibility_required" or "future_day" or "invalid_parameters" => 422,
            "service_unavailable" => 503,
            _ => 500,
        }, problem),
        ValidationException validation => ("invalid_request", 400, new MotorException("invalid_request", issues:
            [.. validation.Errors.Select(x => new MotorErrorIssue("invalid_field", SafeField(x.PropertyName))).Distinct()])),
        BadHttpRequestException request => (ForStatus(request.StatusCode), request.StatusCode, null),
        NotFoundException => ("person_not_found", 404, null),
        ForbiddenException => ("forbidden", 403, null),
        UnauthorizedAccessException => ("forbidden", 403, null),
        TimeoutException or OperationCanceledException => ("service_unavailable", 503, null),
        NpgsqlException { IsTransient: true } => ("service_unavailable", 503, null),
        _ => ("internal_error", 500, null),
    };

    internal static string SafeField(string field)
    {
        var segments = field.TrimStart('$', '.').Split('.');
        var last = segments.LastOrDefault() ?? "";
        if (last.Length == 0 || !last.All(c => char.IsAsciiLetterOrDigit(c) || c is '[' or ']')) return "body";
        var normalized = char.ToLowerInvariant(last[0]) + last[1..];
        var name = normalized.Split('[')[0];
        return name is "id" or "submissionId" or "n" or "sex" or "age" or "h" or "w0" or "waist0"
            or "target" or "act" or "goal" or "pace" or "veg" or "nolac" or "parqA" or "nqA" or "terms"
            or "version" or "startDate" or "log" or "ver" or "adh" or "sleep" or "energy" or "pain" or "sym"
            or "w" or "waist" or "ex" or "params" or "reason" or "expectedVersion" or "day" or "page" or "pageSize"
            ? normalized : "body";
    }

    public static MotorApiError Create(HttpContext context, string code, MotorStatus? status = null,
        IReadOnlyList<MotorErrorIssue>? issues = null)
    {
        var messages = code switch
        {
            "invalid_request" => new MotorText("Revisa los datos enviados.", "Check the information you entered."),
            "unauthenticated" => new("Inicia sesión para continuar.", "Sign in to continue."),
            "forbidden" => new("Tu cuenta no tiene acceso a esta opción.", "Your account cannot access this option."),
            "not_found" or "person_not_found" => new("No encontramos lo que solicitaste.", "We couldn't find what you requested."),
            "plan_locked" => new("Tu plan todavía no está disponible.", "Your plan is not available yet."),
            "submission_conflict" => new("El envío ya se registró con otros datos.", "This submission was already saved with different information."),
            "version_conflict" => new("Los datos cambiaron. Consulta la versión actual.", "The information changed. Check the current version."),
            "eligibility_required" => new("Confirma los términos vigentes para continuar.", "Confirm the current terms to continue."),
            "bad_day" => new("Indica un día entre 1 y 365.", "Enter a day between 1 and 365."),
            "future_day" => new("Ese día todavía no está disponible.", "That day is not available yet."),
            "invalid_parameters" => new("Revisa los valores de configuración.", "Check the settings values."),
            "request_too_large" => new("La solicitud es demasiado grande.", "The request is too large."),
            "unsupported_media_type" => new("Envía los datos en formato JSON.", "Send the information as JSON."),
            "method_not_allowed" => new("Esta operación no está disponible.", "This operation is not available."),
            "service_unavailable" => new("El servicio no está disponible por ahora.", "The service is temporarily unavailable."),
            _ => new("No pudimos completar la solicitud.", "We couldn't complete the request."),
        };
        var language = PatientContentLocalizationFilter.ResolveLanguage(context.Request);
        context.Response.Headers.ContentLanguage = language;
        if (status?.Message is { } body) status = status with { Msg = language == "en" ? body.En : body.Es };
        return new(code, language == "en" ? messages.En : messages.Es, messages,
            code == "service_unavailable", issues ?? [], context.TraceIdentifier, status);
    }

    internal static string ForStatus(int status) => status switch
    {
        400 => "invalid_request", 401 => "unauthenticated", 403 => "forbidden", 404 => "not_found",
        405 => "method_not_allowed", 409 => "version_conflict", 413 => "request_too_large",
        415 => "unsupported_media_type", 422 => "eligibility_required", 503 => "service_unavailable", _ => "internal_error",
    };

    private static Task WriteAsync(HttpContext context, string code, int status, MotorException? problem = null)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(Create(context, code, problem?.Status, problem?.Issues), context.RequestAborted);
    }
}
