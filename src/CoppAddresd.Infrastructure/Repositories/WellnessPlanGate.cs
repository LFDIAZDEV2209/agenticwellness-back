using CoppAddresd.Application.Features.Wellness365;
using CoppAddresd.Domain.Wellness365;
using CoppAddresd.Infrastructure.Persistence;
using CoppAddresd.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

public sealed class WellnessPlanGate(AppDbContext db, WellnessJourneyStore journey) : IWellnessPlanGate
{
    public async Task<WellnessBlock?> CheckAsync(Guid patientId, bool includeScreening, CancellationToken ct)
    {
        var userId = await db.PatientProfiles.AsNoTracking()
            .Where(p => p.Id == patientId && p.DeletedAt == null).Select(p => p.UserId).SingleOrDefaultAsync(ct);
        var access = userId is { } user ? await journey.GetAccessAsync(user, ct) : null;
        if (access is null || access.PatientId != patientId || !access.MembershipActive)
            return new("membership_inactive", new("No hay una membresía activa.", "There is no active membership."));
        if (!access.ConsentCurrent)
            return new("terms", new("Faltan los consentimientos vigentes.", "Current consents are missing."));
        if (!includeScreening) return null;

        var screening = await db.Motor365Screenings.AsNoTracking().Where(s => s.PersonId == patientId)
            .OrderByDescending(s => s.RecordedAt).ThenByDescending(s => s.SubmissionId).FirstOrDefaultAsync(ct);
        if (screening is null || !screening.HasCompleteAnswers)
            return new("unanswered", new("Faltan respuestas del cuestionario.", "Questionnaire answers are missing."));

        var review = await db.Motor365ProfessionalReviews.AsNoTracking()
            .Where(r => r.PersonId == patientId && r.SubmissionId == screening.SubmissionId)
            .OrderByDescending(r => r.ReviewedAt).ThenByDescending(r => r.Id)
            .Select(r => (Motor365ReviewDecision?)r.Decision).FirstOrDefaultAsync(ct);
        short?[] answers = [screening.ParQ1, screening.ParQ2, screening.ParQ3, screening.ParQ4, screening.ParQ5,
            screening.ParQ6, screening.ParQ7, screening.Nqs1, screening.Nqs2, screening.Nqs3, screening.Nqs4,
            screening.Nqs5, screening.Nqs6];
        return (answers.Contains((short?)1) || review is not null) && review != Motor365ReviewDecision.Approved
            ? new("doctor", new("El cuestionario requiere revisión profesional.", "The questionnaire requires professional review."))
            : null;
    }
}
