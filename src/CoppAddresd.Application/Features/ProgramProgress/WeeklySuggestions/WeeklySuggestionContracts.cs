using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoppAddresd.Application.DTOs.ProgramProgress;
using CoppAddresd.Application.Features.ProgramProgress.PatientSummary;
using CoppAddresd.Domain.Entities;

namespace CoppAddresd.Application.Features.ProgramProgress.WeeklySuggestions;

public sealed record WeeklySuggestionOption(string Ref, string Kind, string Label);
public sealed record WeeklySuggestionSlot(string Ref, DateOnly Date, string Kind, string? Current, IReadOnlyList<WeeklySuggestionOption> Options);
public sealed record WeeklySuggestionsInput(DateOnly StartDate, DateOnly EndDate, string Language,
    IReadOnlyList<PatientSummaryFact> Facts, IReadOnlyList<WeeklySuggestionSlot> Slots)
{
    [JsonIgnore] public Guid SubjectPatientId { get; init; }
}
public sealed record WeeklySuggestion(string SlotRef, string OptionRef, string Reason, IReadOnlyList<string> SourceRefs);
public sealed record WeeklySuggestionsResponse(string Status, string Kind, bool IsEstimate, IReadOnlyList<WeeklySuggestion> Suggestions,
    string? Model, DateTimeOffset GeneratedAt);
/// <summary>Mapeo de una alternativa validada por BACK; no se envía a IA.</summary>
public sealed record WeeklySuggestionTarget(string SlotRef, string OptionRef, DateOnly Date, string SlotKind, string Kind,
    Guid? ResourceId, string Label, string ResourceVersion, int Points, int SortOrder);
public sealed record WeeklySuggestionsCapture(Guid PatientId, Guid EnrollmentId, int WeekNumber, string PlanVersion,
    WeeklySuggestionsInput Input, IReadOnlyList<WeeklySuggestionTarget> Targets, IReadOnlyList<PatientSummarySource> Sources);
public sealed record StoredWeeklySuggestion(WeeklySuggestionTarget Target, string Reason, IReadOnlyList<string> SourceRefs,
    DateTimeOffset? AppliedAt = null, Guid? AppliedBy = null);
public sealed record StoredWeeklySuggestions(Guid EnrollmentId, int WeekNumber, string Language, string GeneratedPlanVersion,
    string ExpectedPlanVersion, DateOnly StartDate, DateOnly EndDate, IReadOnlyList<StoredWeeklySuggestion> Suggestions,
    IReadOnlyList<PatientSummarySource> Sources);
public sealed record WeeklySuggestionDto(int Index, DateOnly Date, string SlotKind, string Kind, Guid? ResourceId,
    string Label, string Reason, IReadOnlyList<string> SourceRefs, string State, DateTimeOffset? AppliedAt, Guid? AppliedBy);
public sealed record WeeklySuggestionsDraft(Guid Id, string Kind, bool IsEstimate, string State, string? Model, DateTimeOffset GeneratedAt,
    string Language, DateOnly StartDate, DateOnly EndDate, string PlanVersion, bool Stale,
    IReadOnlyList<WeeklySuggestionDto> Suggestions, IReadOnlyList<PatientSummarySource> Sources);
public sealed record WeeklySuggestionsResult(string Status, WeeklySuggestionsDraft? Draft = null);
public sealed record ApplyWeeklySuggestionResult(string Status, Guid BriefId, int Index, EnrollmentWeekDetailDto Week);
public interface IWeeklySuggestionsClient
{
    Task<WeeklySuggestionsResponse> GenerateAsync(WeeklySuggestionsInput input, CancellationToken ct);
}
public interface IWeeklySuggestionsStore
{
    Task<WeeklySuggestionsCapture> CaptureAsync(Guid enrollment, int week, string language, CancellationToken ct);
    Task<string> VersionAsync(Guid enrollment, int week, CancellationToken ct);
    Task<PatientAiBrief?> LatestAsync(Guid patient, Guid enrollment, int week, string language, CancellationToken ct);
    Task<PatientAiBrief?> FindAsync(Guid patient, Guid id, CancellationToken ct);
    Task SaveAsync(PatientAiBrief row, CancellationToken ct);
    Task<ApplyWeeklySuggestionResult> ApplyAsync(Guid patient, Guid enrollment, int week, Guid brief, int index, Guid actor, CancellationToken ct);
}
public sealed class WeeklySuggestionsUnavailableException : Exception
{
    public WeeklySuggestionsUnavailableException() : base("Sugerencias de IA no disponibles.") { }
}
public static class WeeklySuggestionsJson
{
    public static byte[] Bytes(WeeklySuggestionsInput input) => JsonSerializer.SerializeToUtf8Bytes(input, PatientSummaryJson.Options);
    public static string Hash(WeeklySuggestionsInput input) => Convert.ToHexStringLower(SHA256.HashData(Bytes(input)));
    public static string HashValue<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, PatientSummaryJson.Options)));
}
