namespace CoppAddresd.Api.Security;

/// <summary>Los identificadores del motor y los tokens de enlaces privados no son datos de diagnóstico.</summary>
public static class RequestLogPath
{
    public static string Redact(PathString path) => path.StartsWithSegments("/api/v1/sos/maps")
        ? "/api/v1/sos/maps/[redacted]"
        : path.StartsWithSegments("/api/v1/s") ? "/api/v1/s/[redacted]"
        : path.StartsWithSegments("/api/v1/vita/attachments", out var suffix) && suffix.HasValue
            ? "/api/v1/vita/attachments/[redacted]"
            : path.StartsWithSegments("/v1/people", out var motor) && motor.HasValue
                ? "/v1/people/[redacted]"
                : path.StartsWithSegments("/api/v1/people", out var calendar) && calendar.HasValue
                    ? "/api/v1/people/[redacted]"
                    : path.ToString();
}
