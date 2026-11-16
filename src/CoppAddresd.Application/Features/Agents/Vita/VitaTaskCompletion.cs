using CoppAddresd.Application.DTOs.ProgramProgress;
using CoppAddresd.Application.Features.ProgramProgress.Events;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Domain.Exceptions;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CoppAddresd.Application.Features.Agents.Vita;

public sealed class VitaUnavailableException : Exception { }

/// <summary>Propuesta cifrada autenticada; no equivale a autorización hasta revalidar al confirmar.</summary>
public sealed record VitaTaskProposal(Guid ProposalId, Guid UserId, Guid EnrollmentId, TaskCode TaskCode,
    DateOnly LocalDate, string ResourceVersion, DateTimeOffset ExpiresAt);
public interface IVitaProposalTokens
{
    string Protect(VitaTaskProposal proposal);
    VitaTaskProposal Unprotect(string token);
}
/// <summary>Revalida y completa en una transacción; reutiliza el núcleo canónico de CompleteTask.</summary>
public interface IVitaTaskWriter
{
    Task<CompleteTaskResult> CompleteAsync(VitaTaskProposal proposal,
        Func<CancellationToken, Task> validate, CancellationToken ct);
}
public sealed record PrepareVitaTaskRequest(string TaskCode, DateOnly LocalDate, string ResourceVersion);
public sealed record ConfirmVitaTaskRequest(string ProposalToken, bool Confirmed);
public sealed record VitaTaskProposalDto(string ProposalToken, string TaskCode, DateOnly LocalDate,
    string ResourceVersion, DateTimeOffset ExpiresAt, bool RequiresConfirmation = true);
public sealed record PrepareVitaTaskCommand(Guid UserId, PrepareVitaTaskRequest Input) : IRequest<VitaTaskProposalDto>;
public sealed record ConfirmVitaTaskCommand(Guid UserId, ConfirmVitaTaskRequest Input) : IRequest<CompleteTaskResponseDto>;

public sealed class PrepareVitaTaskCommandValidator : AbstractValidator<PrepareVitaTaskCommand>
{
    public PrepareVitaTaskCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Input.TaskCode).Must(x => x is "podcast" or "ejercicio");
        RuleFor(x => x.Input.LocalDate).NotEmpty();
        RuleFor(x => x.Input.ResourceVersion).NotEmpty().Length(64);
    }
}
public sealed class ConfirmVitaTaskCommandValidator : AbstractValidator<ConfirmVitaTaskCommand>
{
    public ConfirmVitaTaskCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Input.ProposalToken).NotEmpty().MaximumLength(4096);
        RuleFor(x => x.Input.Confirmed).Equal(true);
    }
}

public sealed class PrepareVitaTaskCommandHandler(VitaPatientAccess access, IProgramRepository programs,
    ISender sender, IVitaProposalTokens tokens, TimeProvider clock)
    : IRequestHandler<PrepareVitaTaskCommand, VitaTaskProposalDto>
{
    public async Task<VitaTaskProposalDto> Handle(PrepareVitaTaskCommand request, CancellationToken ct)
    {
        var patient = await access.RequireAsync(request.UserId, "chat", ct);
        var code = request.Input.TaskCode switch { "podcast" => TaskCode.podcast,
            "ejercicio" => TaskCode.ejercicio, _ => throw new ForbiddenException("VITA_TASK_NOT_ALLOWED") };
        var enrollment = await programs.GetActiveEnrollmentForPatientAsync(patient.Id, ct)
            ?? throw new BusinessRuleViolationException("VITA_STALE");
        if (enrollment.PatientId != patient.Id) throw new ForbiddenException("VITA_PATIENT_UNAVAILABLE");
        var scope = code == TaskCode.podcast ? "podcast" : "exercise";
        var context = await sender.Send(new GetVitaContextQuery(request.UserId, scope, request.Input.ResourceVersion), ct);
        VitaCompletionValidation.Require(context, request.Input.LocalDate, code);
        var snapshot = await programs.GetSnapshotAsync(enrollment.Id, request.Input.LocalDate, ct);
        if (snapshot?.TodayTasks.SingleOrDefault(t => t.TaskCode == code)?.Status != "Pending")
            throw new BusinessRuleViolationException("VITA_STALE");
        var proposal = new VitaTaskProposal(Guid.NewGuid(), request.UserId, enrollment.Id,
            code, request.Input.LocalDate, context.ResourceVersion!, clock.GetUtcNow().AddMinutes(5));
        return new(tokens.Protect(proposal), code.ToString(), proposal.LocalDate,
            proposal.ResourceVersion, proposal.ExpiresAt);
    }
}

public static class VitaCompletionValidation
{
    public static void Require(VitaContextDto context, DateOnly date, TaskCode code)
    {
        if (context.Status == "forbidden") throw new ForbiddenException("VITA_FORBIDDEN");
        if (context.Status == "unavailable") throw new VitaUnavailableException();
        if (context.Status != "ok" || context.LocalDate != date || context.Data is null
            || code == TaskCode.ejercicio && context.Data.Value.TryGetProperty("rest", out var rest) && rest.GetBoolean())
            throw new BusinessRuleViolationException("VITA_STALE");
    }
}

public sealed class ConfirmVitaTaskCommandHandler(VitaPatientAccess access, ISender sender,
    IVitaProposalTokens tokens, IVitaTaskWriter writer, TimeProvider clock,
    IProgramMetricsQueue? metrics = null,
    CoppAddresd.Application.WhatsApp.IWhatsAppReminderHooks? reminders = null,
    ILogger<ConfirmVitaTaskCommandHandler>? logger = null) : IRequestHandler<ConfirmVitaTaskCommand, CompleteTaskResponseDto>
{
    public async Task<CompleteTaskResponseDto> Handle(ConfirmVitaTaskCommand request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!request.Input.Confirmed) throw new ForbiddenException("VITA_CONFIRMATION_REQUIRED");
        var proposal = tokens.Unprotect(request.Input.ProposalToken);
        if (proposal.UserId != request.UserId) throw new ForbiddenException("VITA_PROPOSAL_OWNER_MISMATCH");
        if (proposal.ExpiresAt <= clock.GetUtcNow()) throw new BusinessRuleViolationException("VITA_STALE");
        if (proposal.TaskCode is not (TaskCode.podcast or TaskCode.ejercicio))
            throw new ForbiddenException("VITA_TASK_NOT_ALLOWED");
        await access.RequireAsync(request.UserId, "chat", ct);
        var result = await writer.CompleteAsync(proposal, async token =>
        {
            // También se comprueba después del lock y en cada reintento de serialización.
            if (proposal.ExpiresAt <= clock.GetUtcNow()) throw new BusinessRuleViolationException("VITA_STALE");
            var scope = proposal.TaskCode == TaskCode.podcast ? "podcast" : "exercise";
            var context = await sender.Send(new GetVitaContextQuery(request.UserId, scope, proposal.ResourceVersion), token);
            VitaCompletionValidation.Require(context, proposal.LocalDate, proposal.TaskCode);
        }, ct);
        if (result.Outcome == CompleteTaskOutcome.Created && metrics is not null)
        {
            // Misma pre-agregación existente; solo después del commit, jamás al leer o reproducir.
            await metrics.EnqueueAsync(new TaskCompletedMetricEvent(proposal.LocalDate,
                proposal.TaskCode.ToString(), result.PointsAwarded), ct);
            if (result.PointsAwarded > 0) await metrics.EnqueueAsync(new XpAwardedMetricEvent(
                proposal.LocalDate, $"TASK_{proposal.TaskCode}", result.PointsAwarded), ct);
        }
        if (reminders is not null)
        {
            try { await reminders.RefreshEnrollmentAsync(proposal.EnrollmentId, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger?.LogWarning("Vita: reconciliación WhatsApp diferida ({ErrorType})", ex.GetType().Name); }
        }
        return CompleteTaskResponseDto.FromResult(result);
    }
}
