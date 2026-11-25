using System.Text.Json;
using CoppAddresd.Application.Features.Nutrition.Cases;
using CoppAddresd.Application.Features.Nutrition.Queries;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Exceptions;
using FluentValidation;
using MediatR;

namespace CoppAddresd.Application.Features.Nutrition.Commands;

public sealed record CyclePatientMessage(string? Es, string? En);
public sealed record CycleAttachmentDto(Guid Id, string Kind, string? Caption, bool VisibleToPatient, string? Url);
public sealed record CycleReviewDto(Guid CycleId, int Revision, DateTime ReviewedAt, DateTime? PublishedAt,
    string? ProfessionalNotes, CyclePatientMessage PatientMessage, bool CriticalAlertsConfirmed, bool AiDraftConfirmed,
    JsonElement Summary, JsonElement? AiDraft, IReadOnlyList<CycleAttachmentDto> Attachments);
public sealed record CycleIntakeAverages(decimal? Calories, decimal? ProteinG, decimal? CarbsG, decimal? FatG, decimal? FiberG, decimal? WaterMl);
public sealed record ReviewAttachmentFile(Guid PatientId, NutritionCycleAttachment Attachment);

public interface INutritionCycleReviewStore
{
    Task<T> WithPatientLockAsync<T>(Guid patientId, Func<CancellationToken, Task<T>> operation, CancellationToken ct);
    Task<NutritionCycleReview?> LoadAsync(Guid cycleId, CancellationToken ct);
    Task SaveAsync(NutritionCycleReview review, CancellationToken ct);
    Task<string> AuthorNameAsync(Guid userId, CancellationToken ct);
    Task<CycleIntakeAverages> AveragesAsync(Guid patientId, DateOnly from, DateOnly to, CancellationToken ct);
    Task<NutritionPrescription?> PrescriptionAsync(Guid id, CancellationToken ct);
    Task MarkCycleReviewedAsync(Guid cycleId, CancellationToken ct);
    Task AuditAsync(NutritionCycleReview review, Guid actorId, string action, CancellationToken ct);
    Task NotifyPublishedAsync(Guid patientId, Guid cycleId, DateTime now, CancellationToken ct);
    Task<ReviewAttachmentFile?> AttachmentAsync(Guid id, CancellationToken ct);
    Task AddAttachmentAsync(NutritionCycleAttachment attachment, CancellationToken ct);
    Task DeleteAttachmentAsync(NutritionCycleAttachment attachment, CancellationToken ct);
}
public interface INutritionAttachmentUrlSigner
{
    /// <summary>El llamador ya comprobó permiso y alcance, o identidad del paciente y visibilidad publicada.</summary>
    string Sign(Guid attachmentId, DateTimeOffset expiresAt);
}
public sealed record SaveNutritionCycleReview(Guid CycleId, int Revision, string? ProfessionalNotes,
    CyclePatientMessage? PatientMessage, bool CriticalAlertsConfirmed = false, bool AiDraftConfirmed = false) : IRequest<CycleReviewDto>;
public sealed record PublishNutritionCycleReview(Guid CycleId, int Revision, Guid NextPrescriptionId, int NextPrescriptionRevision) : IRequest<CycleReviewDto>;
public sealed record GetNutritionCycleReview(Guid CycleId) : IRequest<CycleReviewDto?>;

public sealed class CycleReviewHandler(IPatientScopeGuard scope, IPatientScopeActor actor, INutritionCycleReviewStore store,
    INutritionCycleReadStore reading, INutritionAttachmentUrlSigner urls, NutritionCases cases, TimeProvider clock)
    : IRequestHandler<SaveNutritionCycleReview, CycleReviewDto>, IRequestHandler<PublishNutritionCycleReview, CycleReviewDto>,
    IRequestHandler<GetNutritionCycleReview, CycleReviewDto?>
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<CycleReviewDto> Handle(SaveNutritionCycleReview request, CancellationToken ct)
    {
        var patient = await scope.EnsureResourceAsync(PatientResource.Cycle, request.CycleId, "Nutrition.Approve", ct);
        if (request.Revision < 0 || request.ProfessionalNotes?.Length > 10000 || request.PatientMessage?.Es?.Length > 10000 || request.PatientMessage?.En?.Length > 10000)
            throw new ValidationException("Revisión o textos inválidos; máximo 10000 caracteres por texto.");
        var author = actor.UserId ?? throw new ForbiddenException("Falta identidad profesional.");
        var review = await store.WithPatientLockAsync(patient, async writeCt =>
        {
            var current = await store.LoadAsync(request.CycleId, writeCt);
            EnsureEditable(current, request.Revision);
            current ??= new() { CycleId = request.CycleId };
            current.Revision++; current.ReviewerId = author; current.ReviewedAt = clock.GetUtcNow().UtcDateTime;
            current.ProfessionalName = await store.AuthorNameAsync(author, writeCt);
            current.SummaryJson = await SnapshotAsync(request.CycleId, patient, writeCt);
            current.ProfessionalNotes = request.ProfessionalNotes?.Trim();
            current.PatientMessageJson = JsonSerializer.Serialize(request.PatientMessage ?? new(null, null), Json);
            current.CriticalAlertsConfirmed = request.CriticalAlertsConfirmed;
            current.AiDraftConfirmed = current.AiDraftJson is not null && request.AiDraftConfirmed;
            await store.SaveAsync(current, writeCt); await store.AuditAsync(current, author, "NutritionCycleReviewSaved", writeCt);
            return current;
        }, ct);
        return await ViewAsync(review, ct);
    }

    public async Task<CycleReviewDto> Handle(PublishNutritionCycleReview request, CancellationToken ct)
    {
        var patient = await scope.EnsureResourceAsync(PatientResource.Cycle, request.CycleId, "Nutrition.Approve", ct);
        var nextPatient = await scope.EnsureResourceAsync(PatientResource.Prescription, request.NextPrescriptionId, "Nutrition.Approve", ct);
        if (patient != nextPatient) throw new NotFoundException("Recomendación no encontrada.");
        var author = actor.UserId ?? throw new ForbiddenException("Falta identidad profesional.");
        var professionalId = await actor.ProfessionalIdAsync(ct);
        var nutritionActor = new NutritionActor(author, professionalId, actor.IsAdmin, false, true, true);
        var review = await store.WithPatientLockAsync(patient, async writeCt =>
        {
            var current = await store.LoadAsync(request.CycleId, writeCt) ?? throw new UnprocessableEntityException("Guarda la revisión antes de publicar.");
            if (current.PublishedAt is not null && current.NextPrescriptionId == request.NextPrescriptionId) return current;
            EnsureEditable(current, request.Revision);
            var message = JsonSerializer.Deserialize<CyclePatientMessage>(current.PatientMessageJson, Json);
            if (string.IsNullOrWhiteSpace(current.ProfessionalNotes) || string.IsNullOrWhiteSpace(message?.Es) || string.IsNullOrWhiteSpace(message?.En)
                || !current.CriticalAlertsConfirmed) throw new UnprocessableEntityException("Completa evaluación, mensaje es/en y validación de alertas (pasos 4 y 6).");
            if (HasAiDraft(current.AiDraftJson) && !current.AiDraftConfirmed)
                throw new UnprocessableEntityException("Confirma el borrador de IA antes de publicar.");
            var header = await reading.HeaderAsync(request.CycleId, patient, writeCt) ?? throw new NotFoundException("Ciclo no encontrado.");
            var today = NutritionStateMachine.Today(clock.GetUtcNow(), header.Timezone);
            if (header.Cycle.Number is null || header.Cycle.EndDate is null || header.Cycle.EndDate >= today)
                throw new UnprocessableEntityException("El cierre se publica después de terminar el ciclo local.");
            if (header.SuccessorStart is not null) throw new BusinessRuleViolationException("El ciclo ya tiene sucesor publicado.");
            var next = await store.PrescriptionAsync(request.NextPrescriptionId, writeCt) ?? throw new NotFoundException("Recomendación no encontrada.");
            if (next.PatientId != patient || next.CycleId == request.CycleId || next.IsPublished)
                throw new UnprocessableEntityException("El cierre exige un borrador del ciclo siguiente de la misma persona.");
            // Reutiliza validación, fechas, revisión optimista y bitácora del editor existente.
            await cases.ExecuteAsync(new NutritionCaseCommand(NutritionOperation.Approve, patient, next.Id,
                request.NextPrescriptionRevision, nutritionActor, CriticalConfirmed: true), writeCt);
            current.PublishedAt = clock.GetUtcNow().UtcDateTime; current.NextPrescriptionId = next.Id; current.Revision++;
            current.ReviewerId = author; current.ProfessionalName = await store.AuthorNameAsync(author, writeCt);
            current.ReviewedAt = current.PublishedAt.Value;
            await store.MarkCycleReviewedAsync(request.CycleId, writeCt);
            // El sucesor ya fija el último día atribuible: hoy no se cuenta dos veces en publicación tardía.
            current.SummaryJson = await SnapshotAsync(request.CycleId, patient, writeCt);
            await store.SaveAsync(current, writeCt);
            await store.NotifyPublishedAsync(patient, request.CycleId, current.PublishedAt.Value, writeCt);
            await store.AuditAsync(current, author, "NutritionCycleReviewPublished", writeCt);
            return current;
        }, ct);
        return await ViewAsync(review, ct);
    }

    public async Task<CycleReviewDto?> Handle(GetNutritionCycleReview request, CancellationToken ct)
    {
        await scope.EnsureResourceAsync(PatientResource.Cycle, request.CycleId, "Nutrition.View", ct);
        var review = await reading.ReadAsync(readCt => store.LoadAsync(request.CycleId, readCt), ct);
        return review is null ? null : await ViewAsync(review, ct);
    }

    internal static void EnsureEditable(NutritionCycleReview? review, int revision)
    {
        if (review?.PublishedAt is not null) throw new BusinessRuleViolationException("La revisión publicada es inmutable.");
        if ((review?.Revision ?? 0) != revision) throw new BusinessRuleViolationException("La revisión cambió; vuelve a cargarla.");
    }

    private static bool HasAiDraft(string? json)
    {
        if (json is null) return false;
        using var value = JsonDocument.Parse(json);
        // insufficient conserva la trazabilidad de la consulta, pero no hay texto IA que confirmar.
        var root = value.RootElement;
        return root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("status", out var status)
            || status.ValueKind != JsonValueKind.String || status.GetString() != "insufficient"
            || !root.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.Object
            || !draft.TryGetProperty("es", out var es) || es.ValueKind != JsonValueKind.String || es.GetString() != ""
            || !draft.TryGetProperty("en", out var en) || en.ValueKind != JsonValueKind.String || en.GetString() != "";
    }

    private async Task<string> SnapshotAsync(Guid cycleId, Guid patient, CancellationToken ct)
    {
        var header = await reading.HeaderAsync(cycleId, patient, ct) ?? throw new NotFoundException("Ciclo no encontrado.");
        var today = NutritionStateMachine.Today(clock.GetUtcNow(), header.Timezone);
        var end = NutritionCycleCalendar.AttributedThrough(header.Cycle, header.SuccessorStart, today, header.Timezone);
        if (header.Cycle.StartDate is not { } start || end is null) throw new UnprocessableEntityException("El ciclo aún no ha comenzado.");
        var data = await reading.RangeAsync(cycleId, patient, start, end.Value, ct);
        var adherence = NutritionCycleAdherence.Calculate(header, data, start, end, today, 1, 100, ct);
        var averages = await store.AveragesAsync(patient, start, end.Value, ct);
        return JsonSerializer.Serialize(new { capturedAt = clock.GetUtcNow(), adherence, averages }, Json);
    }

    private async Task<CycleReviewDto> ViewAsync(NutritionCycleReview review, CancellationToken ct)
    {
        var notes = await actor.HasPermissionAsync("Nutrition.NotesView", ct);
        var photos = await actor.HasPermissionAsync("Nutrition.PhotosView", ct);
        if (photos) await scope.EnsureResourceAsync(PatientResource.Cycle, review.CycleId, "Nutrition.PhotosView", ct);
        var expiry = clock.GetUtcNow().AddMinutes(5);
        return new(review.CycleId, review.Revision, review.ReviewedAt, review.PublishedAt, notes ? review.ProfessionalNotes : null,
            JsonSerializer.Deserialize<CyclePatientMessage>(review.PatientMessageJson, Json)!, review.CriticalAlertsConfirmed, review.AiDraftConfirmed,
            JsonSerializer.Deserialize<JsonElement>(review.SummaryJson), notes && review.AiDraftJson is not null ? JsonSerializer.Deserialize<JsonElement>(review.AiDraftJson) : null,
            photos ? review.Attachments.Select(a => new CycleAttachmentDto(a.Id, a.Kind, a.Caption, a.VisibleToPatient, urls.Sign(a.Id, expiry))).ToArray() : []);
    }
}
