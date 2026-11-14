using CoppAddresd.Application.Features.Agents.Vita;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

/// <summary>Una consulta, acotada por asignación al usuario; no expone configuración administrativa.</summary>
public sealed class VitaAgentCatalogReader(AppDbContext db) : IVitaAgentCatalogReader
{
    public async Task<IReadOnlyList<AgentType>> ListAuthorizedAsync(Guid userId, CancellationToken ct) =>
        await db.AgentTypes.AsNoTracking()
            .Where(t => t.Status == AgentStatus.Activo && t.ActiveVersion != null && t.ActiveVersion.IsActive
                && t.ActiveVersion.AgentTypeId == t.Id
                && db.AgentInstances.Any(i => i.UserId == userId && i.AgentTypeId == t.Id && i.Status == AgentInstanceStatus.Activo))
            .OrderBy(t => t.Name).ThenBy(t => t.Id)
            .Select(t => new AgentType { Id = t.Id, Name = t.Name, Description = t.Description,
                Specialty = t.Specialty, Slug = t.Slug, Metadata = t.Metadata, Status = t.Status,
                ActiveVersionId = t.ActiveVersionId,
                ActiveVersion = new AgentTypeVersion { Id = t.ActiveVersion!.Id, AgentTypeId = t.Id,
                    VersionNumber = t.ActiveVersion.VersionNumber, IsActive = t.ActiveVersion.IsActive } })
            .ToListAsync(ct);
}
