using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

/// <summary>
/// Política compartida por app, directorio, resumen y programa: paciente activo
/// con acceso por defecto, salvo una restricción explícita. Solo lectura.
/// </summary>
internal static class MedicationAccessQuery
{
    internal static IQueryable<ResolvedMedicationAccess> Resolve(AppDbContext db)
        => db.PatientProfiles.AsNoTracking().Where(p => p.DeletedAt == null)
            .Select(p => new ResolvedMedicationAccess
            {
                PatientId = p.Id,
                Enabled = p.Status != "Inactivo" && p.Status != "Inactive"
                    && !db.PatientModuleAccess.Any(a => a.PatientId == p.Id
                        && a.ModuleCode == "medications" && !a.Enabled),
                Revision = db.PatientModuleAccess.Where(a => a.PatientId == p.Id && a.ModuleCode == "medications")
                    .Select(a => (int?)a.Revision).FirstOrDefault() ?? 0,
            });
}

internal sealed record ResolvedMedicationAccess
{
    public Guid PatientId { get; init; }
    public bool Enabled { get; init; }
    public int Revision { get; init; }
}
