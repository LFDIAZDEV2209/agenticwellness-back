using CoppAddresd.Application.Features.Wellness365;
using CoppAddresd.Domain.Wellness365;
using CoppAddresd.Infrastructure.Persistence;
using CoppAddresd.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

public sealed class WellnessPlanGate(AppDbContext db, WellnessJourneyStore journey) : IWellnessPlanGate
{
    public async Task<WellnessBlock?> CheckAsync(Guid patientId, CancellationToken ct)
    {
        if (!await db.Motor365People.AsNoTracking().AnyAsync(p => p.PatientId == patientId, ct)) return null;
        var userId = await db.PatientProfiles.AsNoTracking()
            .Where(p => p.Id == patientId && p.DeletedAt == null).Select(p => p.UserId).SingleOrDefaultAsync(ct);
        var access = userId is { } user ? await journey.GetAccessAsync(user, ct) : null;
        if (access is null || access.PatientId != patientId || !access.MembershipActive)
            return new("membership_inactive", new("No hay una membresía activa.", "There is no active membership."));
        if (!access.ConsentCurrent)
            return new("terms", new("Faltan los consentimientos vigentes.", "Current consents are missing."));
        return null;
    }
}
