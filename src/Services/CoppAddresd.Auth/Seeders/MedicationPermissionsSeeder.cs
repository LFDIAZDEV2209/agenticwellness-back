using CoppAddresd.Auth.Data;
using CoppAddresd.Auth.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Auth.Seeders;

public static class MedicationPermissionsSeeder
{
    public static readonly string[] Codes = ["Medications.View", "Medications.Manage", "Medications.ManageAccess", "Medications.Export"];

    public static async Task SeedAsync(AuthDbContext db, ILogger logger, CancellationToken ct = default)
    {
        var existing = await db.Permissions.Where(p => Codes.Contains(p.Code)).Select(p => p.Code).ToListAsync(ct);
        var additions = Codes.Except(existing).Select(code => new Permission
        {
            Code = code, Name = code, Module = "Medications", CreatedAt = DateTime.UtcNow,
        }).ToArray();
        if (additions.Length == 0) return;
        db.Permissions.AddRange(additions);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Registrados {Count} permisos de Control de medicamentos", additions.Length);
    }
}
