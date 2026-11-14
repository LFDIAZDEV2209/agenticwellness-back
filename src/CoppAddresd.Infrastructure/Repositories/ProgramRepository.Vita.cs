using System.Data;
using CoppAddresd.Application.DTOs.ProgramProgress;
using CoppAddresd.Application.Features.Agents.Vita;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

// Extensión aislada: no altera contratos ni fuentes de Bienestar. Comparar revisión fuera
// de la transacción de CompleteTask permitiría completar contenido cambiado entre ambos pasos.
public sealed partial class ProgramRepository : IVitaTaskWriter
{
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
