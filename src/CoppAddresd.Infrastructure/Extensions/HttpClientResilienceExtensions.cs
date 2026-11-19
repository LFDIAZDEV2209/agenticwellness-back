using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Extensions.Http;

namespace CoppAddresd.Infrastructure.Extensions;

public static class HttpClientResilienceExtensions
{
    internal static readonly HttpRequestOptionsKey<bool> SingleAttempt = new("CoppAddresd.SingleAttempt");

    public static IHttpClientBuilder AddResiliencePolicy(this IHttpClientBuilder builder)
    {
        return builder.AddPolicyHandler((services, request) =>
        {
            var logger = services.GetRequiredService<ILogger<AiServiceResilience>>();
            
            var retryPolicy = HttpPolicyExtensions
                .HandleTransientHttpError()
                .WaitAndRetryAsync(
                    retryCount: 3,
                    sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                    onRetry: (outcome, delay, attempt, _) =>
                    {
                        logger.LogWarning(
                            "Retry {Attempt} after {Delay}s due to: {StatusCode}",
                            attempt,
                            delay.TotalSeconds,
                            outcome.Result?.StatusCode);
                    });

            var circuitBreakerPolicy = HttpPolicyExtensions
                .HandleTransientHttpError()
                .CircuitBreakerAsync(
                    handledEventsAllowedBeforeBreaking: 5,
                    durationOfBreak: TimeSpan.FromSeconds(30),
                    onBreak: (outcome, delay) =>
                    {
                        logger.LogWarning(
                            "Circuit broken for {Delay}s due to: {StatusCode}",
                            delay.TotalSeconds,
                            outcome.Result?.StatusCode);
                    },
                    onReset: () => logger.LogInformation("Circuit reset"),
                    onHalfOpen: () => logger.LogInformation("Circuit half-open"));

            // Un turno de canal puede haber escrito datos aunque se pierda la respuesta.
            return request.Options.TryGetValue(SingleAttempt, out var singleAttempt) && singleAttempt
                ? circuitBreakerPolicy
                : Policy.WrapAsync(retryPolicy, circuitBreakerPolicy);
        });
    }
}

internal class AiServiceResilience;
