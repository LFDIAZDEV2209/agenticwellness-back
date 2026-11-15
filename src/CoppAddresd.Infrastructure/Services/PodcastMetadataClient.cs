using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CoppAddresd.Application.Common;
using CoppAddresd.Application.Features.Media;
using CoppAddresd.Application.Features.Media.PodcastMetadata;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;

namespace CoppAddresd.Infrastructure.Services;

/// <summary>Frontera estricta con AI: solo texto editorial, sin acceso a blobs ni catálogo.</summary>
public sealed class PodcastMetadataClient(HttpClient http, IOptions<AiServiceSettings> settings) : IPodcastMetadataClient
{
    public const string Endpoint = "/internal/media/podcast-metadata";
    public async Task<PodcastMetadataResponse> SuggestAsync(PodcastMetadataInput input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!PodcastMetadataContract.ValidInput(input) || string.IsNullOrWhiteSpace(settings.Value.InternalApiKey)) throw new PodcastMetadataUnavailableException();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(settings.Value.BaseUrl), Endpoint))
                { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(input, PodcastMetadataContract.Json)) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Add("X-Internal-Key", settings.Value.InternalApiKey);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.Value.TimeoutSeconds, 1, 25)));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(65536, deadline.Token);
            var result = await response.Content.ReadFromJsonAsync<PodcastMetadataResponse>(PodcastMetadataContract.Json, deadline.Token);
            if (result is null || !PodcastMetadataContract.ValidResponse(result)) throw new JsonException("Metadatos inválidos.");
            return result with { Draft = result.Draft with { Description = result.Draft.Description.Trim(), Tags = ExternalMediaRules.NormalizeTags(result.Draft.Tags) } };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or UriFormatException
            or OperationCanceledException or BrokenCircuitException) { throw new PodcastMetadataUnavailableException(); }
    }
}
