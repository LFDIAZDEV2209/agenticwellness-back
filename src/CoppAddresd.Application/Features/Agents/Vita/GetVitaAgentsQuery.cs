using CoppAddresd.Domain.Entities;
using FluentValidation;
using MediatR;
using System.Text.Json;

namespace CoppAddresd.Application.Features.Agents.Vita;

/// <summary>Tipos publicados activos de Vita; el acceso del paciente se valida antes de leer el catálogo común.</summary>
public interface IVitaAgentCatalogReader
{
    Task<IReadOnlyList<AgentType>> ListAuthorizedAsync(Guid userId, CancellationToken ct);
}
public sealed record VitaRoutingDto(IReadOnlyList<string> Topics, IReadOnlyList<string> Keywords,
    IReadOnlyList<string> Channels);
public sealed record VitaAgentDto(Guid Id, string RuntimeKey, string? Slug, string Name,
    string? Description, string? Specialty, Guid VersionId, int VersionNumber, VitaRoutingDto Routing);
public sealed record GetVitaAgentsQuery(Guid UserId) : IRequest<IReadOnlyList<VitaAgentDto>>;
public sealed class GetVitaAgentsQueryValidator : AbstractValidator<GetVitaAgentsQuery>
{
    public GetVitaAgentsQueryValidator() => RuleFor(x => x.UserId).NotEmpty();
}
public sealed class GetVitaAgentsQueryHandler(VitaPatientAccess access, IVitaAgentCatalogReader catalog)
    : IRequestHandler<GetVitaAgentsQuery, IReadOnlyList<VitaAgentDto>>
{
    public async Task<IReadOnlyList<VitaAgentDto>> Handle(GetVitaAgentsQuery request, CancellationToken ct)
    {
        await access.RequireAsync(request.UserId, "chat", ct);
        var types = await catalog.ListAuthorizedAsync(request.UserId, ct);
        return types.Where(t => t.Status == Domain.Enums.AgentStatus.Activo
                && t.ActiveVersion is { IsActive: true } v && v.Id == t.ActiveVersionId && v.AgentTypeId == t.Id)
            .Select(t => new VitaAgentDto(t.Id, t.Id.ToString(), t.Slug, t.Name, t.Description, t.Specialty,
                t.ActiveVersion!.Id, t.ActiveVersion.VersionNumber, Routing(t.Metadata)))
            .ToArray();
    }

    // Solo metadatos de routing; prompts, tools, KB, secretos y overrides no cruzan esta frontera.
    public static VitaRoutingDto Routing(string metadata)
    {
        try
        {
            using var doc = JsonDocument.Parse(metadata);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("routing", out var routing)
                || routing.ValueKind != JsonValueKind.Object) return new([], [], ["text", "voice"]);
            return new(Read("topics"), Read("keywords"), routing.TryGetProperty("channels", out _)
                ? Read("channels").Where(c => c is "text" or "voice").ToArray() : ["text", "voice"]);
            string[] Read(string key) => routing.TryGetProperty(key, out var items) && items.ValueKind == JsonValueKind.Array
                ? items.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()!).Where(x => x.Length is > 0 and <= 80)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToArray() : [];
        }
        catch (JsonException) { return new([], [], []); }
    }
}
