using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Exceptions;

namespace CoppAddresd.Application.Features.Agents.Vita;

/// <summary>Identidad resuelta una vez por petición; consentimiento comprobado por operación.</summary>
public sealed class VitaPatientAccess(IPatientRepository patients, ILegalConsentService consent,
    TimeProvider clock)
{
    public async Task<PatientProfile> RequireAsync(Guid userId, string scope, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (userId == Guid.Empty) throw new ForbiddenException("VITA_PATIENT_UNAVAILABLE");
        var patient = await patients.GetByUserIdAsync(userId, ct);
        if (patient is null || patient.UserId != userId || patient.DeletedAt is not null || patient.Status != "Activo")
            throw new ForbiddenException("VITA_PATIENT_UNAVAILABLE");
        await consent.EnsureUserAsync(userId, scope, ct);
        return patient;
    }

    public DateOnly Today(string timezone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(
        clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById(timezone)).DateTime);
}
