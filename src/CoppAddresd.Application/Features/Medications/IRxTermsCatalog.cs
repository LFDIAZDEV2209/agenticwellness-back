namespace CoppAddresd.Application.Features.Medications;

public sealed record RxTermsOption(string Id, string Name);

public interface IRxTermsCatalog
{
    Task<IReadOnlyList<RxTermsOption>> SearchAsync(string? search, CancellationToken ct);
}
