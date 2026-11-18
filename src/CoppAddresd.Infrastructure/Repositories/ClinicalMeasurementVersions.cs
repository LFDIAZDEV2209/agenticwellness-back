using CoppAddresd.Domain.Entities;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

internal static class ClinicalMeasurementVersions
{
    public static IQueryable<Guid> SupersededClinicalMeasurementIds(this AppDbContext db, Guid patientId)
        => db.ClinicalMeasurements
            .Where(replacement => replacement.PatientId == patientId && replacement.SupersedesId != null
                && replacement.Status != "pending_confirmation")
            .Select(replacement => replacement.SupersedesId!.Value);

    public static IQueryable<ClinicalMeasurement> EffectiveClinicalMeasurements(this AppDbContext db, Guid patientId)
    {
        var supersededIds = db.SupersededClinicalMeasurementIds(patientId);
        return db.ClinicalMeasurements.AsNoTracking()
            .Where(m => m.PatientId == patientId && m.Status == "confirmed" && !supersededIds.Contains(m.Id));
    }
}
