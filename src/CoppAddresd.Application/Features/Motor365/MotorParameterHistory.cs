using System.Text.Json;
using CoppAddresd.Domain.Wellness365;

namespace CoppAddresd.Application.Features.Motor365;

public sealed record MotorParameterVersionSummary(int Version, string EngineVersion, Guid? ChangedBy,
    string? Reason, DateTimeOffset? PublishedAt, bool Defaults);

public sealed record MotorParameterVersionDetails(MotorParameterVersionSummary Revision, JsonElement Parameters);
public sealed record MotorParameterHistoryPage(IReadOnlyList<MotorParameterVersionSummary> Versions, int? NextBefore);

public interface IMotorParameterVersionStore
{
    Task<IReadOnlyList<MotorParameterVersionSummary>> ReadPublishedAsync(int? before, int limit, CancellationToken ct);
    Task<Motor365EngineParams?> FindPublishedAsync(int version, CancellationToken ct);
}

/// <summary>Lee revisiones inmutables, sin publicar ni recalcular planes.</summary>
public sealed class MotorParameterHistory(IMotorParameterVersionStore store)
{
    private static readonly MotorParameterVersionSummary Reference = new(
        WellnessEngine.ReferenceParametersVersion, WellnessEngine.ReferenceVersion, null, null, null, true);

    public async Task<MotorParameterHistoryPage> ReadAsync(int? before, int limit, CancellationToken ct)
    {
        var rows = (await store.ReadPublishedAsync(before, limit + 1, ct)).ToList();
        if ((before is null || before > Reference.Version) && rows.Count <= limit)
            rows.Add(Reference);
        return new(rows.Take(limit).ToArray(), rows.Count > limit ? rows[limit - 1].Version : null);
    }

    public async Task<MotorParameterVersionDetails?> ReadVersionAsync(int version, CancellationToken ct)
    {
        // La fila inicial puede haberse creado con el primer plan; no fue una revisión
        // administrativa, por lo que no se presenta el actor de esa inicialización.
        if (version == Reference.Version)
            return new(Reference, JsonSerializer.SerializeToElement(WellnessParameters.Default, JsonSerializerOptions.Web));
        var row = await store.FindPublishedAsync(version, ct);
        return row is null ? null : new(
            new(row.Version, row.EngineVer, row.ChangedBy, row.Reason, row.CreatedAt, false),
            JsonSerializer.Deserialize<JsonElement>(row.ParametersJson));
    }
}
