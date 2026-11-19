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
            var contains = "%" + token.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            products = products.Where(x => EF.Functions.ILike(x.SearchText, contains, "\\"));
        }

        var term = search.Trim();
        return await products.OrderByDescending(x => x.RxCui == term)
            .ThenBy(x => x.Name).ThenBy(x => x.RxCui)
            .Take(25).Select(x => new RxTermsOption(x.RxCui, x.Name)).ToListAsync(ct);
    }
}
