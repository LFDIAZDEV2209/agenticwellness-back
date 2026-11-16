using CoppAddresd.Application.Features.Agents.Vita;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

/// <summary>Catálogo publicado de Vita para pacientes autorizados; no expone configuración administrativa.</summary>
public sealed class VitaAgentCatalogReader(AppDbContext db) : IVitaAgentCatalogReader
{
    public async Task<IReadOnlyList<AgentType>> ListAuthorizedAsync(Guid userId, CancellationToken ct) =>
        await db.AgentTypes.AsNoTracking()
            .Where(t => t.Status == AgentStatus.Activo && t.ActiveVersion != null && t.ActiveVersion.IsActive
                && t.ActiveVersion.Id == t.ActiveVersionId && t.ActiveVersion.AgentTypeId == t.Id)
            .OrderBy(t => t.Name).ThenBy(t => t.Id)
            .Select(t => new AgentType { Id = t.Id, Name = t.Name, Description = t.Description,
                Specialty = t.Specialty, Slug = t.Slug, Metadata = t.Metadata, Status = t.Status,
                ActiveVersionId = t.ActiveVersionId,
                ActiveVersion = new AgentTypeVersion { Id = t.ActiveVersion!.Id, AgentTypeId = t.Id,
                    VersionNumber = t.ActiveVersion.VersionNumber, IsActive = t.ActiveVersion.IsActive } })
            .ToListAsync(ct);
}
