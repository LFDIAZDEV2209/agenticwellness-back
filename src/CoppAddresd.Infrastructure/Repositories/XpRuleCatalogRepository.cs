using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

/// <summary>
/// Implementación EF del catálogo de reglas XP (SPEC §14, B5-R) sobre
/// PostgreSQL (schema <c>app</c>). Solo persistencia y lecturas set-based;
/// la validación de rangos (multiplier &gt; 0, base &gt;= 0, ventana de
/// vigencia) vive en el validador/handler de la capa de aplicación y en las
/// CHECK constraints de la tabla.
/// </summary>
public sealed class XpRuleCatalogRepository(AppDbContext dbContext, TimeProvider clock) : IXpRuleCatalogRepository
{
    public async Task<IReadOnlyList<XpRule>> ListAsync(CancellationToken ct = default)
        => await dbContext.XpRules.AsNoTracking()
            .OrderBy(r => r.Code)
            .ToListAsync(ct);

    public async Task<XpRule?> GetByCodeAsync(string code, CancellationToken ct = default)
        => await dbContext.XpRules.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Code == code, ct);

    public Task<XpRule> UpdateAsync(XpRule rule, Guid? actorId = null, CancellationToken ct = default) =>
        dbContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            // La transacción explícita siempre dispara AuditTriggerInterceptor,
            // incluso cuando EF ejecutaría un UPDATE aislado en autocommit.
            await using var tx = await dbContext.Database.BeginTransactionAsync(ct);
            await dbContext.Database.SqlQuery<Guid>($"""
                SELECT id AS "Value" FROM app.xp_rules WHERE id = {rule.Id} FOR UPDATE
                """).SingleAsync(ct);
            var publishedAt = clock.GetUtcNow().UtcDateTime;
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO app.xp_rule_revisions (code, superseded_at, snapshot)
                SELECT code, {publishedAt}, to_jsonb(x) FROM app.xp_rules AS x WHERE id = {rule.Id}
                """, ct);

            await dbContext.XpRules.Where(row => row.Id == rule.Id).ExecuteUpdateAsync(set => set
                .SetProperty(row => row.BaseXp, rule.BaseXp)
                .SetProperty(row => row.Multiplier, rule.Multiplier)
                .SetProperty(row => row.MaxPerDay, rule.MaxPerDay)
                .SetProperty(row => row.MaxPerWeek, rule.MaxPerWeek)
                .SetProperty(row => row.RequiresValidation, rule.RequiresValidation)
                .SetProperty(row => row.Active, rule.Active)
                .SetProperty(row => row.ValidUntil, rule.ValidUntil)
                .SetProperty(row => row.UpdatedAt, publishedAt), ct);
            await tx.CommitAsync(ct);
            rule.UpdatedAt = publishedAt;
            return rule;
        });
}
