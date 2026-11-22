namespace CoppAddresd.Application.Features.Medications;

public sealed record RxTermsOption(string Id, string Name, string? Strength, string? DoseForm, string? Route, string? DoseUnit);

public interface IRxTermsCatalog
{
    Task<IReadOnlyList<RxTermsOption>> SearchAsync(string? search, CancellationToken ct);
}
