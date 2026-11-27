using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoppAddresd.Application.Features.Wellness365;
using CoppAddresd.Domain.Wellness365;

namespace CoppAddresd.Application.Features.Motor365;

public sealed record MotorAiNutrition(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Kcal,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? ProteinG,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? CarbsG,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? FatG);

public sealed record MotorAiExercise(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Level,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Minutes);

public sealed record MotorAiChanges(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MotorAiNutrition? Nutrition,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MotorAiExercise? Exercise);
public sealed record MotorAiProposal(
    [property: JsonPropertyName("engine_ver")] string EngineVersion,
    [property: JsonPropertyName("params_version")] int ParametersVersion,
    MotorAiChanges Changes, string Reason);

public sealed record MotorAiProposalInput(
    [property: JsonPropertyName("engine_ver")] string EngineVersion,
    [property: JsonPropertyName("params_version")] int ParametersVersion,
    MotorAiNutrition Nutrition, MotorAiExercise? Exercise);

public sealed record MotorAiDecision(Guid Id, string Status, string? Code, MotorText Reason,
    [property: JsonPropertyName("engine_ver")] string EngineVersion,
    [property: JsonPropertyName("params_version")] int ParametersVersion);

public sealed record MotorAiAuditEntry(Guid Id, long Sequence, int Day, DateTimeOffset OccurredAt, Guid ActorUserId,
    string Status, string? Code, MotorText Reason, MotorAiChanges? Proposed, MotorAiChanges? Applied, Guid? Reverts,
    [property: JsonPropertyName("engine_ver")] string EngineVersion,
    [property: JsonPropertyName("params_version")] int ParametersVersion);

public sealed record MotorAiProposalResult(MotorAiProposal? Proposal, string? Error);

public interface IMotorAiProposalSource
{
    bool Enabled { get; }
    Task<MotorAiProposalResult> ProposeAsync(MotorAiProposalInput input, CancellationToken ct);
}

public interface IMotorAiDecisionStore
{
    Task<Motor365AiDecision?> LatestAsync(Guid patientId, int day, CancellationToken ct);
    Task<IReadOnlyList<Motor365AiDecision>> HistoryAsync(Guid patientId, int day, long after, int limit, CancellationToken ct);
    void Add(Motor365AiDecision decision);
}

internal sealed record MotorAiBase(MotorView View, MotorCalculation Calculation, WellnessDay Day)
{
    internal string Fingerprint() => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        View.Person.Ver, View.Person.TodayLocalDate, View.Person.SubmissionId, Calculation.Version, Day,
    })));

    internal bool Eligible => Day.Today && !Day.Paused && !View.Logs.Any(x => x.Day == Day.Day);
}

public sealed class MotorAiAdjustments(MotorPeople people, IMotorAiProposalSource source,
    IMotorAiDecisionStore decisions, IWellnessPlanGate gate, IMotor365Actor actor, TimeProvider clock)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public bool Enabled => source.Enabled;

    public Task<MotorCalendarDay> ReadDayAsync(Guid id, int? day, int? historyDays, bool includeMenu, CancellationToken ct) =>
        people.WithAiDayAsync(id, day, write: false, async (basis, token) =>
        {
            var effective = basis.Day;
            MotorAiDecision? summary = null;
            if (basis.View.Day == basis.Day.Day && await decisions.LatestAsync(id, basis.Day.Day, token) is { } decision)
            {
                // El motivo de un rechazo del día sigue visible aunque la revisión haya cambiado.
                if (decision.Status != "applied")
                    summary = Summary(decision);
                else if (basis.Eligible && decision.BaseFingerprint == basis.Fingerprint()
                    && Apply(basis, ReadChanges(decision)) is { Day: { } adjusted })
                {
                    effective = adjusted;
                    summary = Summary(decision);
                }
            }
            return MotorPeople.ProjectCalendarDay(basis.View, basis.Calculation, effective, historyDays, includeMenu)
                with { AiAdjustmentAvailable = basis.Eligible, AiAdjustment = summary };
        }, ct);

    public async Task<MotorAiDecision> RequestAsync(Guid id, int day, CancellationToken ct)
    {
        var (basis, previous) = await people.WithAiDayAsync(id, day, write: false, async (snapshot, token) =>
        {
            await RequireGateAsync(id, token);
            if (!snapshot.Eligible) throw new MotorException("adjustment_not_available");
            return (snapshot, await decisions.LatestAsync(id, day, token));
        }, ct);

        var fingerprint = basis.Fingerprint();
        if (previous is { Status: "applied" } && previous.BaseFingerprint == fingerprint)
            return Summary(previous);

        // El tercero no recibe identificadores, perfil, registros, síntomas ni texto del paciente.
        var b = basis.Day;
        var proposal = await source.ProposeAsync(new(WellnessEngine.ReferenceVersion, basis.Calculation.Version,
            new(b.Targets.Kcal, b.Targets.Protein, b.Targets.Carbs, b.Targets.Fat),
            new(b.Exercise.Level, b.Exercise.Minutes)), ct);

        return await people.WithAiDayAsync(id, day, write: true, async (current, token) =>
        {
            await RequireGateAsync(id, token);
            var latest = await decisions.LatestAsync(id, day, token);
            if (latest?.Id != previous?.Id) throw new MotorException("version_conflict");
            var candidate = proposal.Proposal;
            var changes = candidate?.Changes;
            var code = !current.Eligible || fingerprint != current.Fingerprint() ? "stale_version" : proposal.Error;
            MotorText? approved = null;
            if (code is null && candidate is not null)
            {
                if (candidate.EngineVersion != WellnessEngine.ReferenceVersion ||
                    candidate.ParametersVersion != current.Calculation.Version)
                    code = "stale_version";
                else
                {
                    var (effective, error) = Apply(current, changes);
                    code = error;
                    if (effective is not null && (approved = KnownReason(candidate.Reason, current.Day, effective)) is null)
                        code = "unsafe_content";
                }
            }
            code ??= candidate is null ? "invalid_proposal" : null;
            var status = code is null ? "applied" : code == "provider_unavailable" ? "unavailable" : "rejected";
            return Record(id, current, status, code, code is null ? approved! : Explain(code), changes);
        }, ct);
    }

    public Task<MotorAiDecision?> RevertAsync(Guid id, int day, CancellationToken ct) =>
        people.WithAiDayAsync<MotorAiDecision?>(id, day, write: true, async (basis, token) =>
        {
            if (await decisions.LatestAsync(id, day, token) is not { } latest) return null;
            if (latest.Status == "reverted") return Summary(latest);
            if (latest.Status != "applied") return null;
            return Record(id, basis, "reverted", null,
                new("Se restauró el plan base del motor.", "The engine's base plan was restored."), null, latest.Id);
        }, ct);

    public async Task<MotorAiAuditEntry[]> ReadAuditAsync(Guid id, int day, long after, int limit, CancellationToken ct) =>
        (await decisions.HistoryAsync(id, day, after, limit, ct)).Select(row =>
        {
            var proposed = ReadChanges(row);
            return new MotorAiAuditEntry(row.Id, row.Sequence, row.Day, row.OccurredAt, row.ActorUserId,
                row.Status, row.Code, new(row.ReasonEs, row.ReasonEn), proposed, row.Status == "applied" ? proposed : null,
                row.Reverts, row.EngineVersion, row.ParametersVersion);
        }).ToArray();

    private async Task RequireGateAsync(Guid id, CancellationToken ct)
    {
        if (await gate.CheckAsync(id, ct) is { } block)
            throw new MotorException("plan_locked", status: new(false, block.Code, block.Message.Es,
                new(block.Message.Es, block.Message.En)));
    }

    private MotorAiDecision Record(Guid id, MotorAiBase basis, string status, string? code, MotorText reason,
        MotorAiChanges? changes, Guid? reverts = null)
    {
        var row = new Motor365AiDecision
        {
            Id = Guid.NewGuid(), PersonId = id, ActorUserId = actor.UserId ?? throw new MotorException("forbidden"),
            Day = basis.Day.Day, OccurredAt = clock.GetUtcNow(), EngineVersion = WellnessEngine.ReferenceVersion,
            ParametersVersion = basis.Calculation.Version, BaseFingerprint = basis.Fingerprint(),
            Status = status, Code = code, ReasonEs = reason.Es, ReasonEn = reason.En,
            ProposedJson = changes is null ? null : JsonSerializer.Serialize(changes, Json), Reverts = reverts,
        };
        decisions.Add(row);
        return Summary(row);
    }

    private static MotorAiDecision Summary(Motor365AiDecision row) => new(row.Id, row.Status, row.Code,
        new(row.ReasonEs, row.ReasonEn), row.EngineVersion, row.ParametersVersion);

    private static MotorAiChanges? ReadChanges(Motor365AiDecision row) =>
        row.ProposedJson is null ? null : JsonSerializer.Deserialize<MotorAiChanges>(row.ProposedJson, Json);

    // Solo texto propio y traducido. Una razón desconocida nunca se publica ni se persiste.
    private static MotorText? KnownReason(string? reason, WellnessDay basis, WellnessDay effective)
    {
        var nutrition = basis.Targets != effective.Targets;
        var exercise = basis.Exercise.Level != effective.Exercise.Level || basis.Exercise.Minutes != effective.Exercise.Minutes;
        return reason switch
        {
            "shorter_session" when exercise && !nutrition =>
                new("Una sesión más suave para facilitar la constancia.", "A gentler session to support consistency."),
            "nutrition_balance" when nutrition && !exercise =>
                new("Un ajuste pequeño dentro de los objetivos del motor.", "A small adjustment within the engine's targets."),
            "daily_consistency" when nutrition && exercise && effective.Exercise.Minutes < basis.Exercise.Minutes =>
                new("Una sesión más corta y un ajuste pequeño de energía para facilitar la constancia.",
                    "A shorter session and a small energy adjustment to support consistency."),
            "daily_consistency" when nutrition && exercise =>
                new("Una sesión más suave y un ajuste pequeño de nutrición para facilitar la constancia.",
                    "A gentler session and a small nutrition adjustment to support consistency."),
            _ => null,
        };
    }

    private static MotorText Explain(string code) => code switch
    {
        "kcal_floor" => new("Se conservó el plan base porque la energía propuesta está por debajo del mínimo.",
            "The base plan was kept because the proposed energy was below the minimum."),
        "kcal_cap" => new("Se conservó el plan base porque la energía propuesta supera el máximo.",
            "The base plan was kept because the proposed energy exceeded the maximum."),
        "macro_bounds" => new("Se conservó el plan base porque los macros propuestos no cumplen los límites del motor.",
            "The base plan was kept because the proposed macros were outside the engine's limits."),
        "exercise_level" => new("Se conservó el plan base porque el nivel propuesto no está disponible.",
            "The base plan was kept because the proposed exercise level was not available."),
        "exercise_duration" => new("Se conservó el plan base porque la duración propuesta no es válida.",
            "The base plan was kept because the proposed duration was not valid."),
        "stale_version" => new("El plan cambió y se descartó la propuesta.", "The plan changed, so the proposal was discarded."),
        "unsafe_content" => new("El motivo no pasó la revisión de seguridad. Tu plan no cambió.",
            "The reason did not pass the safety check. Your plan did not change."),
        "provider_unavailable" => new("El ajuste no está disponible ahora. Puedes continuar con tu plan base.",
            "The adjustment is unavailable right now. You can continue with your base plan."),
        _ => new("La propuesta no es válida. Puedes continuar con tu plan base.",
            "The proposal is invalid. You can continue with your base plan."),
    };

    private static (WellnessDay? Day, string? Error) Apply(MotorAiBase basis, MotorAiChanges? changes)
    {
        var day = basis.Day;
        var profile = basis.Calculation.Profile;
        var targets = day.Targets;
        var exercise = day.Exercise;
        if (changes?.Nutrition is { } proposed)
        {
            var (floor, ceiling) = WellnessEngine.AiCalorieBounds(profile, day);
            var kcal = proposed.Kcal ?? targets.Kcal;
            if (proposed.Kcal.HasValue && kcal < floor) return (null, "kcal_floor");
            if (proposed.Kcal.HasValue && kcal > ceiling) return (null, "kcal_cap");
            var candidate = new WellnessMacros(kcal, proposed.ProteinG ?? targets.Protein,
                proposed.CarbsG ?? targets.Carbs, proposed.FatG ?? targets.Fat);
            if (candidate != targets && !WellnessEngine.AreAiMacrosAllowed(profile, day, candidate))
                return (null, "macro_bounds");
            targets = candidate;
        }
        if (changes?.Exercise is { } dose)
        {
            var level = dose.Level ?? exercise.Level;
            if (level < 0 || level > exercise.Level || exercise.Items.Any(x => x.Definition.MinimumLevel > level && level != exercise.Level))
                return (null, "exercise_level");
            if (WellnessExercise.ReduceDose(exercise, profile.Goal, level, dose.Minutes ?? exercise.Minutes) is not { } reduced)
                return (null, "exercise_duration");
            exercise = reduced;
        }
        if (targets == day.Targets && exercise.Level == day.Exercise.Level && exercise.Minutes == day.Exercise.Minutes)
            return (null, "invalid_proposal");
        return (day with { Targets = targets, Exercise = exercise }, null);
    }
}
