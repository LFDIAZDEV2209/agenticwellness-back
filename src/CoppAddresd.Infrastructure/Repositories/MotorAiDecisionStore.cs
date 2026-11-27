using CoppAddresd.Application.Features.Motor365;
using CoppAddresd.Domain.Wellness365;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

public sealed class MotorAiDecisionStore(AppDbContext db) : IMotorAiDecisionStore
{
    private IQueryable<Motor365AiDecision> ForDay(Guid patientId, int day) =>
        db.Set<Motor365AiDecision>().AsNoTracking().Where(x => x.PersonId == patientId && x.Day == day);

    public Task<Motor365AiDecision?> LatestAsync(Guid patientId, int day, CancellationToken ct) =>
        ForDay(patientId, day).OrderByDescending(x => x.Sequence).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<Motor365AiDecision>> HistoryAsync(Guid patientId, int day, long after, int limit, CancellationToken ct) =>
        await ForDay(patientId, day).Where(x => x.Sequence > after).OrderBy(x => x.Sequence).Take(limit).ToListAsync(ct);

    public void Add(Motor365AiDecision decision) => db.Set<Motor365AiDecision>().Add(decision);
}
