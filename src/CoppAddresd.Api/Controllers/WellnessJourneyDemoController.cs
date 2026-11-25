using System.Security.Cryptography;
using System.Text.Json;
using CoppAddresd.Api.BackgroundJobs;
using CoppAddresd.Api.Configuration;
using CoppAddresd.Api.Context;
using CoppAddresd.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoppAddresd.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/wellness/journey")]
[RequestSizeLimit(16_384)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class WellnessJourneyDemoController(
    WellnessJourneyDemoOptions options, WellnessJourneyStore store, ICurrentContext current) : ControllerBase
{
    [HttpPost("submissions")]
    public async Task<IActionResult> Submit([FromBody] SubmitJourneyRequest request, CancellationToken ct)
    {
        var (access, error) = await GetAccessAsync(ct);
        if (error is not null)
            return error;
        if (request.SubmissionId == Guid.Empty || request.Draft is null)
            return BadRequest(new { code = "invalid_submission" });

        var draft = request.Draft;
        if (Validate(draft) is { } issue)
            return UnprocessableEntity(new { issue.Code, issues = new[] { issue } });

        // El aviso pertenece al cuestionario. El backend ya verificó ambos
        // recibos legales reales. Ignorar el reloj del cliente al reintentar.
        var data = JsonSerializer.Serialize(draft, JsonSerializerOptions.Web);
        var comparable = JsonSerializer.SerializeToUtf8Bytes(
            draft with { Terms = draft.Terms! with { AcceptedAt = null } }, JsonSerializerOptions.Web);
        var hash = Convert.ToHexStringLower(SHA256.HashData(comparable));
        var job = await store.SubmitAsync(access!, request.SubmissionId, hash, data, ct);
        if (job is null)
            return Conflict(new { code = "submission_conflict" });

        return JobResponse(job);
    }

    [HttpGet("latest")]
    public Task<IActionResult> Latest(CancellationToken ct) => ReadAsync(null, ct);

    [HttpGet("generation/{generationId:guid}")]
    public Task<IActionResult> GetGeneration(Guid generationId, CancellationToken ct) => ReadAsync(generationId, ct);

    [HttpPost("generation/{generationId:guid}/retry")]
    public async Task<IActionResult> Retry(Guid generationId, [FromBody] RetryJourneyRequest request, CancellationToken ct)
    {
        var (access, error) = await GetAccessAsync(ct);
        if (error is not null)
            return error;
        if (request.ExpectedAttempts < 1)
            return BadRequest(new { code = "invalid_attempt" });
        var job = await store.RetryAsync(access!, generationId, request.ExpectedAttempts, ct);
        if (job is null)
            return NotFound(new { code = "no_generation" });
        if (job.Status == "failed" && job.Attempts != request.ExpectedAttempts)
            return Conflict(new { code = "retry_conflict" });
        return JobResponse(job);
    }

    private async Task<IActionResult> ReadAsync(Guid? generationId, CancellationToken ct)
    {
        var (access, error) = await GetAccessAsync(ct);
        if (error is not null)
            return error;
        var job = await store.GetAsync(access!, generationId, ct);
        return job is null ? NotFound(new { code = "no_generation" }) : Ok(ToResult(job));
    }

    private async Task<(WellnessJourneyAccess?, IActionResult?)> GetAccessAsync(CancellationToken ct)
    {
        if (!options.Enabled)
            return (null, NotFound(new { code = "journey_unavailable" }));
        if (!User.HasClaim("aud", "app") || current.UserId is not { } userId)
            return (null, Forbid());
        var access = await store.GetAccessAsync(userId, ct);
        return access switch
        {
            null => (null, NotFound(new { code = "journey_unavailable" })),
            { MembershipActive: false } => (null, StatusCode(StatusCodes.Status403Forbidden, new { code = "membership_inactive" })),
            { ConsentCurrent: false } => (null, StatusCode(StatusCodes.Status403Forbidden, new { code = "consent_required" })),
            _ => (access, null),
        };
    }

    private IActionResult JobResponse(WellnessJourneySnapshot job) =>
        job.Status is "pending" or "running"
            ? AcceptedAtAction(nameof(GetGeneration), new { generationId = job.GenerationId }, ToResult(job))
            : Ok(ToResult(job));

    private object ToResult(WellnessJourneySnapshot job)
    {
        var fixture = job.ResultJson is { } json
            ? JsonSerializer.Deserialize<WellnessJourneyDemoFixture>(json, JsonSerializerOptions.Web)
            : null;
        var language = Request.GetTypedHeaders().AcceptLanguage
            .OrderByDescending(value => value.Quality ?? 1)
            .FirstOrDefault(value => value.Quality is not 0)?.Value.Value;
        var output = language is not null && (language.Equals("en", StringComparison.OrdinalIgnoreCase)
            || language.StartsWith("en-", StringComparison.OrdinalIgnoreCase)) ? fixture?.En : fixture?.Es;
        return new
        {
            job.SubmissionId,
            job.GenerationId,
            job.Status,
            job.Attempts,
            job.StartedAt,
            exercise = new { job.Status, result = output?.Exercise },
            nutrition = new { job.Status, result = output?.Nutrition },
            inbox = new { job.Status, items = output?.Inbox ?? [] },
            error = job.ErrorCode is null ? null : new { code = job.ErrorCode, retryable = true },
        };
    }

    private static GateIssue? Validate(JourneyDraft draft)
    {
        if (draft.Terms is not { Version: "wellness-v1-2026-11", AcceptedAt: not null })
            return new("terms", 6, "terms", "Falta aceptar los términos y el aviso de bienestar.");
        if (draft.Goal != "bajar")
            return new("unsupported_goal", 0, "goal", "Selecciona tu objetivo.");
        if (draft.Sex is not ("F" or "M"))
            return new("unanswered", 1, "sex", "Indica tu sexo para calcular el gasto energético.");
        if (draft.Age is null or > 120)
            return new("unanswered", 1, "age", "Indica tu edad.");
        if (draft.HeightCm is not (> 0 and <= 300))
            return new("unanswered", 1, "heightCm", "Indica tu estatura.");
        if (draft.WeightKg is not (> 0 and <= 1000))
            return new("unanswered", 1, "weightKg", "Indica tu peso actual.");
        if (draft.WaistCm is not (> 0 and <= 500))
            return new("unanswered", 1, "waistCm", "Indica tu cintura.");
        if (draft.TargetWeightKg is not (> 0 and <= 1000))
            return new("unanswered", 1, "targetWeightKg", "Indica tu peso meta.");
        if (draft.Activity is not (>= 1 and <= 4))
            return new("unanswered", 2, "activity", "Selecciona tu nivel de actividad.");
        if (draft.Pace is not ("s" or "m" or "max"))
            return new("unanswered", 2, "pace", "Selecciona tu nivel de actividad.");
        if (draft.Parq is not { Length: 7 } || draft.Parq.Any(answer => answer is not (0 or 1)))
            return new("unanswered", 4, "parq", "Responde todas las preguntas de seguridad del ejercicio.");
        if (draft.Nqs is not { Length: 6 } || draft.Nqs.Any(answer => answer is not (0 or 1)))
            return new("unanswered", 5, "nqs", "Responde todas las preguntas de seguridad de la nutrición.");
        // Los umbrales replican la compuerta del cuestionario que usa la app.
        if (draft.Age < 18)
            return new("age", 1, "age", "Esta versión es para mayores de 18 años.");
        if (draft.WeightKg.Value < 18.5m * draft.HeightCm.Value * draft.HeightCm.Value / 10_000m)
            return new("bmi", 0, "goal", "Con tu IMC actual no se recomienda un objetivo de bajar o definir. Elige subir masa muscular o consulta a un profesional.");
        if (draft.Parq.Contains(1) && !draft.DoctorConfirmed)
            return new("doctor", 4, "doctorConfirmed", "Marcaste una respuesta de seguridad del ejercicio. Consulta primero a tu médico y confírmalo para continuar.");
        if (draft.Nqs.Contains(1) && !draft.NutritionistConfirmed)
            return new("doctor", 5, "nutritionistConfirmed", "Marcaste una respuesta de seguridad de la nutrición. Consulta primero a un profesional y confírmalo para continuar.");
        return null;
    }

    private sealed record GateIssue(string Code, int Step, string Field, string MessageKey);
    public sealed record SubmitJourneyRequest(Guid SubmissionId, JourneyDraft? Draft);
    public sealed record RetryJourneyRequest(int ExpectedAttempts);
    public sealed record JourneyTerms(string? Version, DateTimeOffset? AcceptedAt);
    public sealed record JourneyDraft(
        string? Goal, string? Sex, int? Age, decimal? HeightCm, decimal? WeightKg, decimal? WaistCm,
        decimal? TargetWeightKg, int? Activity, string? Pace, bool Vegetarian, bool DairyFree,
        int?[]? Parq, int?[]? Nqs, bool DoctorConfirmed, bool NutritionistConfirmed, JourneyTerms? Terms);
}
