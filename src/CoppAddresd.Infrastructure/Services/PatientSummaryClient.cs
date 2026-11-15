using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoppAddresd.Application.Common;
using CoppAddresd.Application.Features.ProgramProgress.PatientSummary;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;

namespace CoppAddresd.Infrastructure.Services;

/// <summary>Contrato 4.4a: bytes exactos para trazabilidad, límite de cuerpo y validación de citas.</summary>
public sealed class PatientSummaryClient(HttpClient http, IOptions<AiServiceSettings> settings) : IPatientSummaryClient
{
    public const string Endpoint = "/internal/program/patient-summary";
    private static readonly JsonSerializerOptions StrictJson = new(JsonSerializerDefaults.Web)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public async Task<PatientSummaryResponse> GenerateAsync(PatientSummaryInput input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(settings.Value.InternalApiKey) || !PatientSummaryContract.ValidInput(input))
            throw new PatientSummaryUnavailableException();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(settings.Value.BaseUrl), Endpoint))
                { Content = new ByteArrayContent(PatientSummaryJson.Bytes(input)) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Add("X-Internal-Key", settings.Value.InternalApiKey);
            request.Options.Set(AiConsentSubject.Key, new("patient-ai", PatientId: input.SubjectPatientId, RequireRecipientConsent: true));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.Value.TimeoutSeconds, 1, 25)));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(65536, deadline.Token);
            var result = await response.Content.ReadFromJsonAsync<PatientSummaryResponse>(StrictJson, deadline.Token);
            if (result is null || !PatientSummaryContract.ValidResponse(result, input.Facts.Select(f => f.Ref)))
                throw new JsonException("Contrato de resumen inválido.");
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or UriFormatException
            or OperationCanceledException or BrokenCircuitException)
        { throw new PatientSummaryUnavailableException(); }
    }
}
