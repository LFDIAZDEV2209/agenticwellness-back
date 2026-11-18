using CoppAddresd.Domain.Entities;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

internal static class ClinicalMeasurementVersions
{
    public static IQueryable<ClinicalMeasurement> EffectiveClinicalMeasurements(this AppDbContext db, Guid patientId)
        => db.ClinicalMeasurements.AsNoTracking().Where(m => m.PatientId == patientId && m.Status == "confirmed"
            && !db.ClinicalMeasurements.Any(replacement => replacement.SupersedesId == m.Id
                && replacement.PatientId == patientId && replacement.Status != "pending_confirmation"));
}
