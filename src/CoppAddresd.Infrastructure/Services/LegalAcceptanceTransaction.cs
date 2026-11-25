using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Services;

internal static class LegalAcceptanceTransaction
{
    public static Task<T> RunAsync<T>(AppDbContext db, Guid userId, Func<Task<T>> action, CancellationToken ct)
        => db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            if (!db.Database.IsRelational()) return await action();

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (db.Database.IsNpgsql())
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock(hashtextextended({userId.ToString()}, 0))", ct);
            var result = await action();
            await transaction.CommitAsync(ct);
            return result;
        });
}
