using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoppAddresd.Domain.Entities;

namespace CoppAddresd.Application.Features.ProgramProgress.PatientSummary;

public sealed record PatientSummaryFact(string Ref, string Section, string Text);
public sealed record PatientSummaryInput(DateOnly StartDate, DateOnly EndDate, string Language, IReadOnlyList<PatientSummaryFact> Facts)
{
    [JsonIgnore] public Guid SubjectPatientId { get; init; }
}
public sealed record PatientSummaryAttention(string Text, IReadOnlyList<string> SourceRefs);
public sealed record PatientSummaryResponse(string Status, string Kind, bool IsEstimate, string Summary,
    IReadOnlyList<string> SourceRefs, IReadOnlyList<PatientSummaryAttention> Attention, string? Model, DateTimeOffset GeneratedAt);
/// <summary>La UI resuelve sección e intervalo dentro del paciente autorizado; nunca recibe URLs generadas por IA.</summary>
public sealed record PatientSummarySource(string Ref, string Section, DateOnly From, DateOnly To);
public sealed record StoredPatientSummary(string Language, DateOnly StartDate, DateOnly EndDate,
    PatientSummaryResponse Response, IReadOnlyList<PatientSummarySource> Sources);
public sealed record PatientSummaryDraft(Guid Id, string Kind, bool IsEstimate, string Summary,
    IReadOnlyList<string> SourceRefs, IReadOnlyList<PatientSummaryAttention> Attention, string? Model,
    DateTimeOffset GeneratedAt, string Language, DateOnly StartDate, DateOnly EndDate, IReadOnlyList<PatientSummarySource> Sources);
public sealed record PatientSummaryResult(string Status, PatientSummaryDraft? Summary = null);
public sealed record PatientSummaryData(string? ProgramState, int? Week, DateOnly? ProgramStart,
    int NutritionRecords, int NutritionDays, int ExerciseCompletions, int PodcastCompletions, int CompletedTests, int Measurements);
public interface IPatientSummaryStore
{
    Task<string> TimezoneAsync(Guid patientId, CancellationToken ct);
    Task<PatientSummaryData> ReadAsync(Guid patientId, DateOnly from, DateOnly to, DateTime fromUtc, DateTime capturedAtUtc, CancellationToken ct);
    Task<PatientAiBrief?> LatestAsync(Guid patientId, string language, CancellationToken ct);
    Task SaveAsync(PatientAiBrief brief, CancellationToken ct);
}
public interface IPatientSummaryClient
{
    Task<PatientSummaryResponse> GenerateAsync(PatientSummaryInput input, CancellationToken ct);
}
public sealed class PatientSummaryUnavailableException : Exception
{
    public PatientSummaryUnavailableException() : base("Resumen de IA no disponible.") { }
}
public static class PatientSummaryJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static byte[] Bytes(PatientSummaryInput input) => JsonSerializer.SerializeToUtf8Bytes(input, Options);
    public static string Hash(PatientSummaryInput input) => Convert.ToHexStringLower(SHA256.HashData(Bytes(input)));
}
