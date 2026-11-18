using System.Data;
using CoppAddresd.Application.DTOs.ProgramProgress;
using CoppAddresd.Application.Features.Agents.Vita;
using CoppAddresd.Application.Features.ProgramProgress.DTOs.Nutrition;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

// Extensión aislada: no altera contratos ni fuentes de Bienestar. Comparar revisión fuera
// de la transacción de CompleteTask permitiría completar contenido cambiado entre ambos pasos.
public sealed partial class ProgramRepository : IVitaTaskWriter
{
    internal Task<NutritionLogResultDto> RecordVitaMealAsync(VitaCaptureProposal proposal, CancellationToken ct)
    {
        var program = proposal.Data.Program!;
        var meal = program.Meal!;
        return LogNutritionCoreAsync(proposal.PatientId, Enum.Parse<MealCode>(meal.MealCode), program.LocalDate,
            meal.ToIntake(proposal.Channel), proposal.UserId, $"vita:{proposal.Nonce:N}", ct, program.EnrollmentId);
    }

    internal async Task<CompleteTaskResult> RecordVitaCheckInAsync(VitaCaptureProposal proposal, CancellationToken ct)
    {
        if (dbContext.Database.CurrentTransaction is null)
            throw new InvalidOperationException("El check-in de Vita requiere la transacción del paciente.");
        var program = proposal.Data.Program!;
        var enrollment = await dbContext.ProgramEnrollments.FromSqlInterpolated(
            $"SELECT * FROM app.program_enrollments WHERE id = {program.EnrollmentId} FOR UPDATE").FirstOrDefaultAsync(ct);
        if (enrollment is null || enrollment.PatientId != proposal.PatientId || enrollment.Status != ProgramEnrollmentStatus.Active)
            throw new BusinessRuleViolationException("VITA_CAPTURE_ENROLLMENT_CHANGED");
        if (program.LocalDate < enrollment.StartLocalDate || program.LocalDate > PatientLocalToday(enrollment.Timezone))
            throw new UnprocessableEntityException("VITA_CAPTURE_INVALID_DATE");
        if (await dbContext.EmotionalRecords.AnyAsync(r => r.ProgramEnrollmentId == enrollment.Id
                && r.RecordedLocalDate == program.LocalDate, ct))
            throw new BusinessRuleViolationException("VITA_CAPTURE_CHECKIN_ALREADY_RECORDED");

        var result = await CompleteTaskCoreAsync(new(enrollment.Id, program.LocalDate, TaskCode.emocional,
            $"vita:{proposal.Nonce:N}", proposal.Data.ObservedAt.UtcDateTime,
            program.MoodScore, null, null, SourceRefType: proposal.Channel, ActorId: proposal.UserId), ct);
        if (result.Outcome != CompleteTaskOutcome.Created)
            throw new BusinessRuleViolationException("VITA_CAPTURE_CHECKIN_ALREADY_RECORDED");
        return result;
    }

    public Task<CompleteTaskResult> CompleteAsync(VitaTaskProposal proposal,
        Func<CancellationToken, Task> validate, CancellationToken ct) =>
        dbContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            ct.ThrowIfCancellationRequested();
            dbContext.ChangeTracker.Clear();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            // Mismo orden que consentimiento; su retirada no puede intercalarse con la escritura.
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({proposal.UserId.ToString()}, 0))", ct);
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT id FROM app.program_enrollments WHERE id = {proposal.EnrollmentId} FOR UPDATE", ct);
            var owned = await dbContext.ProgramEnrollments.AsNoTracking().AnyAsync(e => e.Id == proposal.EnrollmentId
                && e.Status == ProgramEnrollmentStatus.Active && e.Patient != null && e.Patient.UserId == proposal.UserId
                && e.Patient.Status == "Activo" && e.Patient.DeletedAt == null, ct);
            if (!owned) throw new ForbiddenException("VITA_PROPOSAL_OWNER_MISMATCH");
            var today = await GetPatientLocalTodayAsync(proposal.EnrollmentId, ct);
            if (today != proposal.LocalDate) throw new BusinessRuleViolationException("VITA_STALE");
            await validate(ct);
            var clientId = $"vita:{proposal.ProposalId:N}";
            var existing = await dbContext.TaskCompletions.AsNoTracking().FirstOrDefaultAsync(t =>
                t.EnrollmentId == proposal.EnrollmentId && t.LocalDate == proposal.LocalDate && t.TaskCode == proposal.TaskCode, ct);
            CompleteTaskResult result;
            if (existing is not null)
            {
                if (existing.ClientRequestId != clientId) throw new BusinessRuleViolationException("VITA_STALE");
                result = await BuildReplayResultAsync(proposal.EnrollmentId, existing, ct);
            }
            else
            {
                // Núcleo canónico: tareas programadas, contenido efectivo, XP, descanso y constraints.
                result = await CompleteTaskCoreAsync(new(proposal.EnrollmentId, proposal.LocalDate,
                    proposal.TaskCode, clientId, null, null, null, proposal.ResourceVersion, ActorId: proposal.UserId), ct);
            }
            await transaction.CommitAsync(ct);
            return result;
        });
}
