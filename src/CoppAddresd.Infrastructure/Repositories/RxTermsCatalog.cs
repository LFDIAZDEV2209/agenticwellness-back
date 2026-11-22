using CoppAddresd.Application.Features.Medications;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

public sealed class RxTermsCatalog(AppDbContext db) : IRxTermsCatalog
{
    public async Task<IReadOnlyList<RxTermsOption>> SearchAsync(string? search, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(search) || search.Length > 120 || search.Trim().Length < 3)
            return [];
        var tokens = search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var products = db.RxTermsProducts.AsNoTracking().Where(x => x.IsActive);
        foreach (var token in tokens)
        {
            var contains = $"%{EscapeLike(token)}%";
            products = products.Where(x => EF.Functions.ILike(x.SearchText, contains, "\\"));
        }

        var term = search.Trim();
        var prefix = $"{EscapeLike(tokens[0])}%";
        // RxTerms incluye la marca entre corchetes en FULL_NAME.
        var brandPrefix = $"%[{prefix}";
        return await products.OrderByDescending(x => x.RxCui == term)
            .ThenByDescending(x => EF.Functions.ILike(x.Name, prefix, "\\") || EF.Functions.ILike(x.Name, brandPrefix, "\\"))
            .ThenBy(x => x.Name).ThenBy(x => x.RxCui)
            .Take(25).Select(x => new RxTermsOption(x.RxCui, x.Name, x.Strength, x.DoseForm, x.Route, x.DoseUnit))
            .ToListAsync(ct);
    }

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
