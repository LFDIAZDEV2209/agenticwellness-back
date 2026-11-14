using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoppAddresd.Application.Common;
using CoppAddresd.Application.Features.Nutrition.Commands;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;

namespace CoppAddresd.Infrastructure.Services;

/// <summary>Cliente camelCase del contrato compacto; comparte resiliencia y consentimiento con la narrativa existente.</summary>
public sealed class NutritionCycleSummaryClient(HttpClient http, IOptions<AiServiceSettings> settings) : INutritionCycleSummaryClient
{
    public const string Endpoint = "/internal/program/nutrition-cycle-summary";
    private static readonly JsonSerializerOptions StrictJson = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public async Task<NutritionCycleSummaryResponse> GenerateAsync(NutritionCycleSummaryInput input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!ValidInput(input)) throw new NutritionCycleAiUnavailableException("ai_input_invalid");
        if (string.IsNullOrWhiteSpace(settings.Value.InternalApiKey)) throw new NutritionCycleAiUnavailableException("ai_not_configured");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(settings.Value.BaseUrl), Endpoint))
            { Content = JsonContent.Create(input, options: NutritionCycleAiJson.Options) };
            request.Headers.Add("X-Internal-Key", settings.Value.InternalApiKey);
            request.Options.Set(AiConsentSubject.Key, new("patient-ai", PatientId: input.SubjectPatientId));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.Value.TimeoutSeconds, 1, 25)));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            response.EnsureSuccessStatusCode();
            // Límite también al leer el cuerpo cuando un cliente de test no usa la configuración DI.
            await response.Content.LoadIntoBufferAsync(65536, deadline.Token);
            var result = await response.Content.ReadFromJsonAsync<NutritionCycleSummaryResponse>(StrictJson, deadline.Token);
            if (result is null || !ValidResponse(result)) throw new JsonException("Contrato de resumen inválido.");
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (BrokenCircuitException) { throw new NutritionCycleAiUnavailableException("ai_circuit_open"); }
        catch (OperationCanceledException) { throw new NutritionCycleAiUnavailableException("ai_timeout"); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or UriFormatException)
        { throw new NutritionCycleAiUnavailableException("ai_unavailable"); }
    }

    private static bool Bounded(decimal? value, decimal max) => value is null || value >= 0 && value <= max;
    private static bool ValidAggregates(CycleAiAggregates a) => a.DaysWithRecords is >= 0 and <= 366 && Bounded(a.Adherence, 1)
        && Bounded(a.AvgCalories, 20000) && Bounded(a.AvgProteinG, 2000) && Bounded(a.AvgCarbsG, 2000)
        && Bounded(a.AvgFatG, 2000) && Bounded(a.AvgFiberG, 2000) && Bounded(a.AvgWaterMl, 20000);
    private static bool ValidInput(NutritionCycleSummaryInput i)
        => i.CycleNumber is >= 1 and <= 10000 && i.Language is "es" or "en"
            && i.EndDate.DayNumber - i.StartDate.DayNumber is >= 0 and < 366 && ValidAggregates(i.Aggregates)
            && i.Aggregates.DaysWithRecords <= i.EndDate.DayNumber - i.StartDate.DayNumber + 1
            && (i.PreviousCycleAggregates is null || ValidAggregates(i.PreviousCycleAggregates))
            && (i.Measurements is null || (i.Measurements.Weight is null || i.Measurements.Weight > 0 && i.Measurements.Weight <= 1000)
                && (i.Measurements.Waist is null || i.Measurements.Waist > 0 && i.Measurements.Waist <= 500)
                && (i.Measurements.Bmi is null || i.Measurements.Bmi > 0 && i.Measurements.Bmi <= 200)
                && (i.Measurements.Glucose is null || i.Measurements.Glucose > 0 && i.Measurements.Glucose <= 2000))
            && new[] { i.MealCounts.Des, i.MealCounts.Alm, i.MealCounts.Mer, i.MealCounts.Cen, i.MealCounts.Agua }.All(c => c is >= 0 and <= 10000)
            && i.Records.Count <= 100 && i.Records.Select(r => r.Date).Distinct().Count() <= i.Aggregates.DaysWithRecords
            && i.Records.All(r => r.Date >= i.StartDate && r.Date <= i.EndDate && r.MealCode is "des" or "alm" or "mer" or "cen" or "agua"
                && r.Origin is "ai_estimated" or "patient_confirmed" && Bounded(r.Calories, 20000) && Bounded(r.WaterMl, 20000)
                && Bounded(r.Macros.ProteinG, 2000) && Bounded(r.Macros.CarbsG, 2000) && Bounded(r.Macros.FatG, 2000) && Bounded(r.Macros.FiberG, 2000));
    private static bool ValidResponse(NutritionCycleSummaryResponse r)
    {
        static bool Text(string? s) => !string.IsNullOrWhiteSpace(s) && s.Length <= 2000;
        if (r.GeneratedAt == default || r.GeneratedAt.Offset != TimeSpan.Zero || r.Model is null || r.Model.Length > 200
            || r.Patterns is null || r.Gaps is null || r.Patterns.Count > 10 || r.Gaps.Count > 10 || r.Draft is null
            || !r.Patterns.All(Text) || !r.Gaps.All(Text)) return false;
        return r.Status switch
        {
            "ok" => Text(r.Summary) && Text(r.Draft.Es) && Text(r.Draft.En) && !string.IsNullOrWhiteSpace(r.Model),
            "insufficient" => r.Summary == "" && r.Patterns.Count == 0 && r.Gaps.Count == 0 && r.Draft.Es == "" && r.Draft.En == "" && r.Model == "",
            _ => false
        };
    }
}
