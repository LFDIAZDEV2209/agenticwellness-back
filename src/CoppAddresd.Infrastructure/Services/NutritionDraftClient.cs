using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using CoppAddresd.Application.Common;
using CoppAddresd.Application.Features.Nutrition.Cases;
using CoppAddresd.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoppAddresd.Infrastructure.Services;

/// <summary>
/// Cliente de la propuesta nutricional. El plazo propio queda por debajo del
/// timeout de borde (CloudFront corta a los 30 s): si la IA no responde, se
/// devuelve el fallback determinista en vez de un 504 al profesional.
/// </summary>
public sealed class NutritionDraftClient(
    HttpClient client,
    IOptions<AiServiceSettings> settings,
    ILogger<NutritionDraftClient>? logger = null
) : INutritionDraftClient
{
    /// <summary>
    /// 22 s: mayor que el timeout interno del AI Service (15 s) y menor que el
    /// corte de CloudFront (30 s), dejando margen para serializar y persistir.
    /// </summary>
    private const int DeadlineSeconds = 22;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public async Task<NutritionDraftResponse> GenerateAsync(
        NutritionDraftRequest request,
        CancellationToken ct
    )
    {
        var started = Stopwatch.StartNew();
        try
        {
            using var call = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(new Uri(settings.Value.BaseUrl), "/internal/nutrition/plan-draft")
            );
            call.Headers.Add("X-Internal-Key", settings.Value.InternalApiKey);
            call.Options.Set(
                AiConsentSubject.Key,
                new("patient-ai", PatientId: request.SubjectPatientId)
            );
            call.Content = JsonContent.Create(request, options: Json);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(DeadlineSeconds));
            using var response = await client.SendAsync(call, deadline.Token);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<NutritionDraftResponse>(
                    Json,
                    deadline.Token
                ) ?? throw new JsonException("Respuesta de nutrición vacía.");
        }
        catch (Exception ex)
            when (!ct.IsCancellationRequested
                && ex is HttpRequestException or JsonException or OperationCanceledException
            )
        {
            // Fallback explícito: el plan determinista sigue siendo válido para
            // revisión manual; nunca se expone el detalle del proveedor.
            logger?.LogWarning(
                "Nutrition draft AI unavailable; using deterministic fallback after {ElapsedMs} ms ({Error})",
                started.ElapsedMilliseconds,
                ex.GetType().Name
            );
            return new(
                new(
                    "Propuesta determinista pendiente de revisión.",
                    "Deterministic proposal pending review."
                ),
                new(
                    "Revisar porciones y alertas antes de aprobar.",
                    "Review portions and alerts before approval."
                ),
                request.Menus,
                "deterministic_fallback"
            );
        }
    }
}
