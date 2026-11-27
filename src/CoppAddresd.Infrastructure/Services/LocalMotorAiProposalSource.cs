using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoppAddresd.Application.Features.Motor365;
using Microsoft.Extensions.Configuration;

namespace CoppAddresd.Infrastructure.Services;

/// <summary>Adaptador local optativo. Un proveedor remoto requiere una integración aprobada por separado.</summary>
public sealed class LocalMotorAiProposalSource(IHttpClientFactory clients, IConfiguration config) : IMotorAiProposalSource
{
    public const string HttpClientName = "Motor365AiLocal";
    private static readonly JsonSerializerOptions ProposalJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
    };

    private readonly Uri? _endpoint = Resolve(config);
    public bool Enabled => _endpoint is not null;

    private static Uri? Resolve(IConfiguration config) =>
        config.GetValue<bool>("Motor365Ai:EnableLocalProposals") &&
        Uri.TryCreate(config["Motor365Ai:ProposalUrl"], UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttp && uri.Host == "127.0.0.1" &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
            ? uri : null;

    private static bool Finite(double? value) => !value.HasValue || double.IsFinite(value.Value);

    public async Task<MotorAiProposalResult> ProposeAsync(MotorAiProposalInput input, CancellationToken ct)
    {
        if (_endpoint is null) return new(null, "provider_unavailable");
        try
        {
            using var client = clients.CreateClient(HttpClientName);
            using var response = await client.PostAsJsonAsync(_endpoint, input, ct);
            if (!response.IsSuccessStatusCode) return new(null, "provider_unavailable");
            var payload = await response.Content.ReadAsByteArrayAsync(ct);
            var proposal = JsonSerializer.Deserialize<MotorAiProposal>(payload, ProposalJson);
            if (proposal?.Changes is null || proposal.EngineVersion is null || proposal.ParametersVersion < 1)
                return new(null, "invalid_proposal");
            if (proposal.Changes.Nutrition is { } n && (!Finite(n.Kcal) || !Finite(n.ProteinG) ||
                !Finite(n.CarbsG) || !Finite(n.FatG)))
                return new(null, "invalid_proposal");
            return new(proposal, null);
        }
        catch (JsonException) { return new(null, "invalid_proposal"); }
        catch (HttpRequestException) { return new(null, "provider_unavailable"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(null, "provider_unavailable"); }
    }
}
