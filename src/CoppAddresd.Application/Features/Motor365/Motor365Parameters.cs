using System.Text.Json;
using System.Text.Json.Nodes;
using CoppAddresd.Domain.Wellness365;
using MediatR;

namespace CoppAddresd.Application.Features.Motor365;

public sealed record GetMotorParams : IRequest<MotorParamsDto>;
public sealed record PutMotorParams(MotorParamsInput Input) : IRequest<MotorParamsDto>;
public sealed record GetMotorAudit(int Page, int PageSize) : IRequest<IReadOnlyList<MotorAuditEntry>>;
public sealed record GetMotorHealth : IRequest<MotorHealth>;
public sealed record GetMotorFoods : IRequest<IReadOnlyDictionary<string, IReadOnlyList<object>>>;
public sealed record GetMotorExercises : IRequest<IReadOnlyList<MotorExerciseCatalogDto>>;

public sealed class GetMotorParamsHandler(IMotor365Store store) : IRequestHandler<GetMotorParams, MotorParamsDto>
{
    public async Task<MotorParamsDto> Handle(GetMotorParams request, CancellationToken ct) =>
        MotorParameterVersions.Dto(await store.FindParamsAsync(null, ct));
}

public sealed class PutMotorParamsHandler(IMotor365Store store, IMotor365Actor actor, TimeProvider clock)
    : IRequestHandler<PutMotorParams, MotorParamsDto>
{
    public Task<MotorParamsDto> Handle(PutMotorParams request, CancellationToken ct) =>
        store.InTransactionAsync(true, async token =>
        {
            var userId = actor.UserId ?? throw new MotorException("forbidden");
            var current = await store.FindParamsAsync(null, token);
            var view = MotorParameterVersions.Dto(current);
            var node = JsonSerializer.SerializeToNode(view.Params, JsonSerializerOptions.Web)!.AsObject();
            var before = node.ToJsonString();
            MotorParameterVersions.Apply(node, JsonNode.Parse(request.Input.Params.GetRawText())!.AsObject());
            var revised = MotorParameterVersions.Parse(node);
            if (JsonSerializer.Serialize(revised, JsonSerializerOptions.Web) == before) return view;
            if (request.Input.ExpectedVersion is { } expected && expected != view.ParamsVersion)
                throw new MotorException("version_conflict");

            var now = clock.GetUtcNow();
            if (current is null) store.AddParams(MotorParameterVersions.Initial(now, userId));
            var version = view.ParamsVersion + 1;
            store.AddParams(new()
            {
                Version = version,
                ParametersJson = JsonSerializer.Serialize(revised, JsonSerializerOptions.Web),
                ChangedBy = userId,
                Reason = string.IsNullOrWhiteSpace(request.Input.Reason) ? "params.update" : request.Input.Reason.Trim(),
                CreatedAt = now,
            });
            return view with { Params = revised, ParamsVersion = version };
        }, ct);
}

internal static class MotorParameterVersions
{
    internal static Motor365EngineParams Initial(DateTimeOffset now, Guid userId) => new()
    {
        Version = WellnessEngine.ReferenceParametersVersion,
        ParametersJson = JsonSerializer.Serialize(WellnessParameters.Default, JsonSerializerOptions.Web),
        ChangedBy = userId,
        CreatedAt = now,
        Reason = "defaults",
    };

    internal static MotorParamsDto Dto(Motor365EngineParams? row) =>
        new(Read(row), WellnessParameters.Default, WellnessEngine.ReferenceVersion,
            row?.Version ?? WellnessEngine.ReferenceParametersVersion);

    internal static WellnessParameters Read(Motor365EngineParams? row)
    {
        if (row is null) return WellnessParameters.Default;
        if (row.EngineVer != WellnessEngine.ReferenceVersion) throw new MotorException("service_unavailable");
        try
        {
            return Parse(JsonNode.Parse(row.ParametersJson)!.AsObject());
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or MotorException)
        {
            throw new MotorException("service_unavailable");
        }
    }

    internal static void Apply(JsonObject target, JsonObject patch)
    {
        foreach (var (key, value) in patch)
        {
            if (!target.TryGetPropertyValue(key, out var existing) || value is null) throw Invalid();
            if (existing is JsonObject child && value is JsonObject changes)
                Apply(child, changes);
            else if (existing is JsonArray && value is JsonArray)
                target[key] = value.DeepClone();
            else if (existing is JsonValue && value.GetValueKind() == JsonValueKind.Number)
                target[key] = value.DeepClone();
            else
                throw Invalid();
        }
    }

    internal static WellnessParameters Parse(JsonObject json)
    {
        WellnessParameters? p;
        try
        {
            p = json.Deserialize<WellnessParameters>(JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            throw Invalid();
        }

        if (p is null || p.Pct is null || p.Pro is null || p.Band is null || p.Fat is null || p.BreakEvery is null ||
            !ValidShare(p.Pct.Lose, .20) || !ValidShare(p.Pct.Gain, .15) || !ValidShare(p.Pct.Tone, .12) ||
            !ValidBand(p.Band.Lose, -2, 0) || !ValidBand(p.Band.Gain, 0, 2) || !ValidBand(p.Band.Tone, -2, 0) ||
            !InRange(p.Pro.Lose, 1, 2.2) || !InRange(p.Pro.Gain, 1, 2.2) || !InRange(p.Pro.Tone, 1, 2.2) ||
            !InRange(p.ProCap, 1, 2.2) || !InRange(p.Fat.Rest, .20, .35) || !InRange(p.Fat.Train, .20, .35) ||
            p.FloorFemale is < 1200 or > 2500 || p.FloorMale is < 1500 or > 3000 ||
            p.Step is < 10 or > 100 || !InRange(p.AdhMin, .70, 1) || p.WeighMin is < 4 or > 7 ||
            p.BreakEvery.Lose is < 2 or > 52 || p.BreakEvery.Gain is < 2 or > 52 || p.BreakEvery.Tone is < 2 or > 52 ||
            p.DefCut < 1 || p.DefCut >= p.BreakEvery.Tone || p.CarbMin is < 80 or > 120)
            throw Invalid();

        return p;
    }

    private static bool ValidShare(WellnessShares? s, double max) =>
        s is not null && s.Slow >= 0 && s.Slow <= s.Moderate && s.Moderate <= s.Maximum && s.Maximum <= max;

    private static bool ValidBand(IReadOnlyList<double>? values, double min, double max) =>
        values is { Count: 2 } && values[0] >= min && values[0] < values[1] && values[1] <= max;

    private static bool InRange(double n, double min, double max) => n >= min && n <= max;

    private static MotorException Invalid() =>
        new("invalid_parameters", issues: [new("invalid_parameters", "params")]);
}

public sealed class GetMotorAuditHandler(IMotor365Store store)
    : IRequestHandler<GetMotorAudit, IReadOnlyList<MotorAuditEntry>>
{
    public Task<IReadOnlyList<MotorAuditEntry>> Handle(GetMotorAudit request, CancellationToken ct) =>
        store.ReadAuditAsync(request.Page, request.PageSize, ct);
}

public sealed class GetMotorHealthHandler(IMotor365Store store) : IRequestHandler<GetMotorHealth, MotorHealth>
{
    public async Task<MotorHealth> Handle(GetMotorHealth request, CancellationToken ct) =>
        await store.CanConnectAsync(ct) ? new(true, WellnessEngine.ReferenceVersion) : throw new MotorException("service_unavailable");
}

public sealed class GetMotorFoodsHandler : IRequestHandler<GetMotorFoods, IReadOnlyDictionary<string, IReadOnlyList<object>>>
{
    public Task<IReadOnlyDictionary<string, IReadOnlyList<object>>> Handle(GetMotorFoods request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<object>>>(WellnessNutrition.Catalog.ToDictionary(
            x => x.Key, x => (IReadOnlyList<object>)[x.Value.Es, x.Value.En, x.Value.Kcal, x.Value.Protein, x.Value.Carbs, x.Value.Fat,
                x.Value.Group, x.Value.UnitGrams, x.Value.UnitEs, x.Value.UnitEn]));
    }
}

public sealed class GetMotorExercisesHandler : IRequestHandler<GetMotorExercises, IReadOnlyList<MotorExerciseCatalogDto>>
{
    public Task<IReadOnlyList<MotorExerciseCatalogDto>> Handle(GetMotorExercises request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<MotorExerciseCatalogDto>>(WellnessExercise.OrderedCatalog.Select(
            x => new MotorExerciseCatalogDto(x.Id, x.Name, x.Category, x.Impact, x.MinimumLevel, x.Icon, x.How)).ToArray());
    }
}
