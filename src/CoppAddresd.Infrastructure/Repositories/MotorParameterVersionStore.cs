using CoppAddresd.Application.Features.Motor365;
using CoppAddresd.Domain.Wellness365;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

public sealed class MotorParameterVersionStore(AppDbContext db) : IMotorParameterVersionStore
{
    public async Task<IReadOnlyList<MotorParameterVersionSummary>> ReadPublishedAsync(int? before, int limit, CancellationToken ct) =>
        await db.Motor365EngineParams.AsNoTracking()
            .Where(row => row.Version > WellnessEngine.ReferenceParametersVersion && (!before.HasValue || row.Version < before))
            .OrderByDescending(row => row.Version).Take(limit)
            .Select(row => new MotorParameterVersionSummary(row.Version, row.EngineVer, row.ChangedBy,
                row.Reason, row.CreatedAt, false)).ToArrayAsync(ct);

    public Task<Motor365EngineParams?> FindPublishedAsync(int version, CancellationToken ct) =>
        db.Motor365EngineParams.AsNoTracking().SingleOrDefaultAsync(row => row.Version == version, ct);
}
