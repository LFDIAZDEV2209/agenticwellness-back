using CoppAddresd.Auth.Data;
using CoppAddresd.Auth.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Auth.Services;

/// <summary>
/// Aprovisionamiento explícito por flujo de alta, nunca por rol. El llamador
/// autoriza la operación y controla la transacción; una suspensión no se revierte.
/// </summary>
internal static class ApplicationAccessProvisioning
{
    public static async Task<string?> EnsureAsync(
        AuthDbContext db, Guid userId, string applicationCode, CancellationToken ct)
    {
        var application = await db.Applications.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Code == applicationCode && a.IsActive, ct);
        if (application is null)
            return $"La aplicación {applicationCode} no está disponible. No se otorgó acceso.";

        var access = await db.UserApplications.AsNoTracking()
            .SingleOrDefaultAsync(a => a.UserId == userId && a.ApplicationId == application.Id, ct);
        if (access?.IsSuspended == true)
            return $"El acceso a {applicationCode} está suspendido. Reactívalo mediante el flujo de acceso correspondiente.";
        if (access is not null) return null;

        db.UserApplications.Add(new UserApplication
        {
            UserId = userId,
            ApplicationId = application.Id,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return null;
    }
}
